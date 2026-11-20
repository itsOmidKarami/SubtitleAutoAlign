using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleAutoAlign.Configuration;
using Jellyfin.Plugin.SubtitleAutoAlign.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <inheritdoc />
public sealed class SubtitleAlignmentService : ISubtitleAlignmentService
{
    private readonly IProcessRunner _processRunner;
    private readonly IFfSubSyncLocator _ffSubSyncLocator;
    private readonly Func<PluginConfiguration> _configurationAccessor;
    private readonly Func<string?> _ffmpegDirectoryAccessor;
    private readonly ILogger<SubtitleAlignmentService> _logger;
    private readonly SpeechCache? _speechCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleAlignmentService"/> class.
    /// </summary>
    /// <param name="processRunner">Abstraction used to invoke ffsubsync.</param>
    /// <param name="ffSubSyncLocator">Resolves which ffsubsync executable to invoke.</param>
    /// <param name="configurationAccessor">Delegate returning the current plugin configuration.</param>
    /// <param name="ffmpegDirectoryAccessor">Delegate returning the directory holding the ffmpeg/ffprobe
    /// binaries to use (Jellyfin's own), or null to let ffsubsync search PATH.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="speechCache">Saved speech analyses; null disables caching.</param>
    public SubtitleAlignmentService(
        IProcessRunner processRunner,
        IFfSubSyncLocator ffSubSyncLocator,
        Func<PluginConfiguration> configurationAccessor,
        Func<string?> ffmpegDirectoryAccessor,
        ILogger<SubtitleAlignmentService> logger,
        SpeechCache? speechCache = null)
    {
        _speechCache = speechCache;
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _ffSubSyncLocator = ffSubSyncLocator ?? throw new ArgumentNullException(nameof(ffSubSyncLocator));
        _configurationAccessor = configurationAccessor ?? throw new ArgumentNullException(nameof(configurationAccessor));
        _ffmpegDirectoryAccessor = ffmpegDirectoryAccessor ?? throw new ArgumentNullException(nameof(ffmpegDirectoryAccessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configuration = _configurationAccessor();
        var outputPath = SubtitleFileNaming.BuildAlignedOutputPath(request.SubtitlePath);

        if (File.Exists(outputPath) && !configuration.OverwriteExistingAlignedSubtitle)
        {
            _logger.LogDebug("Aligned subtitle already exists at {OutputPath}; skipping", outputPath);
            return new AlignmentResult(Success: false, OutputPath: outputPath, Skipped: true, ErrorMessage: null);
        }

        var extra = configuration.ExtraArguments;
        var cache = _speechCache is not null && SpeechCache.CanCache(extra) ? _speechCache : null;

        // Reference is a saved analysis when one exists; otherwise a work link to the
        // video, so ffsubsync can save its analysis outside the media folder. The entry
        // path is taken before the run so a video that changes meanwhile isn't cached.
        var cachedAnalysis = cache?.TryGet(request.VideoPath, extra);
        var expectedEntry = cachedAnalysis is null ? cache?.EntryPath(request.VideoPath, extra) : null;
        var workLink = expectedEntry is not null ? cache!.CreateWorkLink(request.VideoPath) : null;
        var reference = cachedAnalysis ?? workLink ?? request.VideoPath;

        var ffSubSyncPath = _ffSubSyncLocator.Resolve(configuration.FfSubSyncPath);
        _logger.LogInformation(
            "Aligning subtitle {SubtitlePath} against {VideoPath} -> {OutputPath} (using {FfSubSyncPath}{CacheNote})",
            request.SubtitlePath,
            request.VideoPath,
            outputPath,
            ffSubSyncPath,
            cachedAnalysis is null ? string.Empty : ", saved speech analysis");

        ProcessRunResult? run;
        try
        {
            run = await RunAsync(ffSubSyncPath, reference, request.SubtitlePath, outputPath, configuration, workLink is not null, cancellationToken).ConfigureAwait(false);
            KeepAnalysis(request.VideoPath, extra, workLink, expectedEntry, run);
        }
        finally
        {
            DeleteWorkLink(workLink);
        }

        if (run is null)
        {
            return new AlignmentResult(Success: false, OutputPath: outputPath, Skipped: false, ErrorMessage: "ffsubsync could not be started.");
        }

        // A run that used a saved analysis or a work link failed: retry once the way
        // 0.2.0.0 did, so caching can never make an alignment fail that used to work.
        if (run is { ExitCode: not 0, TimedOut: false } && !string.Equals(reference, request.VideoPath, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "ffsubsync failed with the saved speech analysis for {SubtitlePath} ({StandardError}); retrying against the video",
                request.SubtitlePath,
                run.StandardError);
            if (cachedAnalysis is not null)
            {
                TryRemove(request.VideoPath);
            }

            run = await RunAsync(ffSubSyncPath, request.VideoPath, request.SubtitlePath, outputPath, configuration, false, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return new AlignmentResult(Success: false, OutputPath: outputPath, Skipped: false, ErrorMessage: "ffsubsync could not be started.");
            }
        }

        var result = run;

        if (result.TimedOut)
        {
            _logger.LogWarning("ffsubsync timed out aligning {SubtitlePath}", request.SubtitlePath);
            return new AlignmentResult(Success: false, OutputPath: outputPath, Skipped: false, ErrorMessage: "ffsubsync timed out.");
        }

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "ffsubsync exited with code {ExitCode} for {SubtitlePath}: {StandardError}",
                result.ExitCode,
                request.SubtitlePath,
                result.StandardError);
            return new AlignmentResult(
                Success: false,
                OutputPath: outputPath,
                Skipped: false,
                ErrorMessage: $"ffsubsync exited with code {result.ExitCode}: {result.StandardError}");
        }

        _logger.LogInformation("Successfully aligned subtitle to {OutputPath}", outputPath);
        return new AlignmentResult(Success: true, OutputPath: outputPath, Skipped: false, ErrorMessage: null);
    }

    /// <inheritdoc />
    public async Task<bool> PreAnalyseAsync(string videoPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoPath);
        var configuration = _configurationAccessor();
        var extra = configuration.ExtraArguments;
        if (_speechCache is null || !SpeechCache.CanCache(extra))
        {
            return false;
        }

        if (_speechCache.TryGet(videoPath, extra) is not null)
        {
            _logger.LogDebug("Speech analysis for {VideoPath} is already saved", videoPath);
            return true;
        }

        var expectedEntry = _speechCache.EntryPath(videoPath, extra);
        var workLink = expectedEntry is null ? null : _speechCache.CreateWorkLink(videoPath);
        if (workLink is null)
        {
            _logger.LogWarning("Could not prepare speech analysis for {VideoPath}", videoPath);
            return false;
        }

        _logger.LogInformation("Analysing speech in advance for {VideoPath}", videoPath);
        ProcessRunResult? run;
        try
        {
            run = await RunAsync(_ffSubSyncLocator.Resolve(configuration.FfSubSyncPath), workLink, null, null, configuration, true, cancellationToken).ConfigureAwait(false);
            KeepAnalysis(videoPath, extra, workLink, expectedEntry, run);
        }
        finally
        {
            DeleteWorkLink(workLink);
        }

        var saved = _speechCache.TryGet(videoPath, extra) is not null;
        if (saved)
        {
            _logger.LogInformation("Saved speech analysis for {VideoPath}", videoPath);
        }
        else
        {
            _logger.LogWarning(
                "Speech analysis for {VideoPath} was not saved (exit code {ExitCode}, timed out {TimedOut}): {StandardError}",
                videoPath,
                run?.ExitCode,
                run?.TimedOut,
                run?.StandardError);
        }

        return saved;
    }

    /// <summary>
    /// Runs ffsubsync once. Returns null (after logging) if it could not be started.
    /// </summary>
    private async Task<ProcessRunResult?> RunAsync(
        string ffSubSyncPath,
        string reference,
        string? subtitlePath,
        string? outputPath,
        PluginConfiguration configuration,
        bool serializeSpeech,
        CancellationToken cancellationToken)
    {
        var arguments = FfSubSyncArgumentBuilder.Build(
            reference,
            subtitlePath,
            outputPath,
            configuration.ExtraArguments,
            _ffmpegDirectoryAccessor(),
            serializeSpeech);

        try
        {
            var run = await _processRunner.RunAsync(
                ffSubSyncPath,
                arguments,
                TimeSpan.FromSeconds(configuration.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to run ffsubsync for {Reference}", subtitlePath ?? reference);
            return null;
        }
    }

    private void TryRemove(string videoPath)
    {
        try
        {
            _speechCache?.Remove(videoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the saved speech analysis for {VideoPath}", videoPath);
        }
    }

    /// <summary>
    /// After a successful run against a work link, saves the analysis it produced, unless the
    /// video changed since <paramref name="expectedEntry"/> was taken.
    /// </summary>
    private void KeepAnalysis(string videoPath, string? extraArguments, string? workLink, string? expectedEntry, ProcessRunResult? run)
    {
        if (workLink is null || expectedEntry is null || _speechCache is null || run is not { ExitCode: 0, TimedOut: false })
        {
            return;
        }

        var produced = SpeechCache.ProducedPath(workLink);
        if (!File.Exists(produced))
        {
            return;
        }

        try
        {
            if (!_speechCache.Store(videoPath, extraArguments, produced, expectedEntry))
            {
                _logger.LogInformation("{VideoPath} changed during analysis; not saving it", videoPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the speech analysis for {VideoPath}", videoPath);
        }
    }

    private void DeleteWorkLink(string? workLink)
    {
        if (workLink is not null)
        {
            _speechCache?.DeleteWorkLink(workLink);
        }
    }
}
