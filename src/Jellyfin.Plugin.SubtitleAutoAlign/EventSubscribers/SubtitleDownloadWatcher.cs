using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleAutoAlign.Models;
using Jellyfin.Plugin.SubtitleAutoAlign.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAutoAlign.EventSubscribers;

/// <summary>
/// Watches the Jellyfin library for newly-appeared external subtitle files on
/// movies and TV episodes and enqueues them for auto-alignment.
///
/// Jellyfin does not expose a confirmed, ABI-stable "subtitle downloaded"
/// event, so this watches <see cref="ILibraryManager.ItemUpdated"/> and diffs
/// each item's external subtitle files against a previously observed
/// snapshot. It does not attempt to prove a subtitle came specifically from
/// the OpenSubtitles provider; any newly-appeared, non-aligned external
/// subtitle on a movie or episode is treated as eligible.
/// </summary>
public sealed class SubtitleDownloadWatcher : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleAlignmentService _alignmentService;
    private readonly ILogger<SubtitleDownloadWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _knownSubtitlePaths = new();
    private readonly SpeechCache _speechCache;

    // Alignments (someone is waiting) always run before background speech analysis.
    private readonly PriorityWorkQueue<AlignmentRequest, PreAnalysisJob> _queue = new(job => job.VideoPath);

    private DateTime _startedUtc = DateTime.UtcNow;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleDownloadWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager, used to observe item updates.</param>
    /// <param name="alignmentService">Service used to perform the actual alignment.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="speechCache">Saved speech analyses, cleaned up when videos are removed.</param>
    public SubtitleDownloadWatcher(
        ILibraryManager libraryManager,
        ISubtitleAlignmentService alignmentService,
        ILogger<SubtitleDownloadWatcher> logger,
        SpeechCache speechCache)
    {
        _speechCache = speechCache ?? throw new ArgumentNullException(nameof(speechCache));
        WriteDiagnosticMarker("SubtitleDownloadWatcher constructor called");

        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _alignmentService = alignmentService ?? throw new ArgumentNullException(nameof(alignmentService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        WriteDiagnosticMarker("StartAsync called");

        _startedUtc = DateTime.UtcNow;

        // Before the worker starts: nothing can be using a work folder yet.
        _speechCache.DeleteStaleWorkFolders();

        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => ProcessQueueAsync(_workerCts.Token), CancellationToken.None);

        _logger.LogInformation("Subtitle Auto Align is watching the library for newly-appeared subtitle files.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes directly to a fixed file, bypassing the logging pipeline entirely.
    /// Temporary diagnostic; safe to remove once the startup issue is confirmed fixed.
    /// </summary>
    private static void WriteDiagnosticMarker(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                "/tmp/subtitle-auto-align-diagnostics.log",
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best-effort diagnostic only; never let this affect real startup.
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        _queue.Complete();
        _workerCts?.Cancel();

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } video || !AutoAlignScope.IsWatchedType(video))
        {
            _logger.LogDebug(
                "Ignoring ItemUpdated for item {ItemName} ({ItemType}): not a movie or episode",
                e.Item?.Name,
                e.Item?.GetType().Name);
            return;
        }

        var videoPath = video.Path;
        if (string.IsNullOrEmpty(videoPath))
        {
            _logger.LogDebug("{ItemName} has no video path; skipping", video.Name);
            return;
        }

        var currentSubtitlePaths = SubtitleDiscovery.GetExternalSubtitlePaths(video);

        var firstSight = !_knownSubtitlePaths.ContainsKey(video.Id);
        var previousSubtitlePaths = _knownSubtitlePaths.GetOrAdd(video.Id, _ => new HashSet<string>());

        _logger.LogDebug(
            "Checked {ItemName} for new subtitles: {CurrentCount} external subtitle(s) currently present, {PreviousCount} previously known",
            video.Name,
            currentSubtitlePaths.Count,
            previousSubtitlePaths.Count);

        var newPaths = SubtitleChangeDetector.DiffNewSubtitlePaths(previousSubtitlePaths, currentSubtitlePaths);
        if (firstSight)
        {
            // No snapshot yet (e.g. after a restart): only files written since startup are new downloads.
            newPaths = SubtitleChangeDetector.WrittenSince(newPaths, _startedUtc, System.IO.File.GetLastWriteTimeUtc);
        }

        _knownSubtitlePaths[video.Id] = new HashSet<string>(currentSubtitlePaths, StringComparer.OrdinalIgnoreCase);

        if (newPaths.Count == 0)
        {
            return;
        }

        // Checked after the snapshot update, so subtitles that appear while this is
        // off are not aligned later; the "Align all subtitles" task covers those.
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.EnableAutoAlign == false)
        {
            _logger.LogInformation("Automatic alignment is disabled; not aligning new subtitles for {ItemName}", video.Name);
            return;
        }

        var libraryIds = _libraryManager.GetCollectionFolders(video).Select(folder => folder.Id);
        if (configuration is not null && !AutoAlignScope.IsInEnabledLibrary(libraryIds, configuration.DisabledLibraryIds))
        {
            _logger.LogInformation("{ItemName} is not in a watched library; not aligning its new subtitles", video.Name);
            return;
        }

        // Our own .autoaligned outputs also appear as new files; skip them quietly.
        var candidates = newPaths.Where(SubtitleChangeDetector.IsCandidateSubtitle).ToList();
        if (candidates.Count == 0)
        {
            _logger.LogDebug("No new candidate subtitles for {ItemName}", video.Name);
            return;
        }

        _logger.LogInformation(
            "Found {NewCount} newly-appeared subtitle file(s) for {ItemName}: {NewPaths}",
            candidates.Count,
            video.Name,
            string.Join(", ", candidates));

        foreach (var subtitlePath in candidates)
        {
            var request = new AlignmentRequest(video.Id, videoPath, subtitlePath);
            if (_queue.TryEnqueueHigh(request))
            {
                _logger.LogInformation("Enqueued {SubtitlePath} for auto-alignment", subtitlePath);
            }
            else
            {
                _logger.LogWarning("Alignment queue full; dropped request for {SubtitlePath}", subtitlePath);
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } video || !AutoAlignScope.IsWatchedType(video) || string.IsNullOrEmpty(video.Path))
        {
            return;
        }

        var selected = Plugin.Instance?.Configuration.PreAnalyseLibraryIds;
        if (selected is not { Length: > 0 })
        {
            return;
        }

        var libraryIds = _libraryManager.GetCollectionFolders(video).Select(folder => folder.Id).ToArray();
        if (!AutoAlignScope.IsInSelectedLibrary(libraryIds, selected))
        {
            return;
        }

        if (_queue.TryEnqueueLow(new PreAnalysisJob(video.Path, libraryIds)))
        {
            _logger.LogInformation(
                "Queued {ItemName} for speech analysis in advance ({Waiting} waiting)",
                video.Name,
                _queue.LowCount);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } video || !AutoAlignScope.IsWatchedType(video) || string.IsNullOrEmpty(video.Path))
        {
            return;
        }

        try
        {
            _speechCache.Remove(video.Path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the saved speech analysis for {ItemName}", video.Name);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            (AlignmentRequest? High, PreAnalysisJob? Low) work;
            try
            {
                work = await _queue.DequeueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                if (work.High is { } request)
                {
                    await _alignmentService.AlignAsync(request, cancellationToken).ConfigureAwait(false);
                }
                else if (work.Low is { } job)
                {
                    // Re-checked here so unticking a library stops its queued backlog.
                    if (AutoAlignScope.IsInSelectedLibrary(job.LibraryIds, Plugin.Instance?.Configuration.PreAnalyseLibraryIds))
                    {
                        await _alignmentService.PreAnalyseAsync(job.VideoPath, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogDebug("Skipping speech analysis for {VideoPath}: its library is no longer ticked", job.VideoPath);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected error processing {Work}", work.High?.SubtitlePath ?? work.Low?.VideoPath);
            }
        }
    }
}

/// <summary>
/// A queued speech analysis for a newly added video, with the libraries it belongs to.
/// </summary>
/// <param name="VideoPath">The video file.</param>
/// <param name="LibraryIds">IDs of the video's libraries when it was queued.</param>
internal sealed record PreAnalysisJob(string VideoPath, Guid[] LibraryIds);
