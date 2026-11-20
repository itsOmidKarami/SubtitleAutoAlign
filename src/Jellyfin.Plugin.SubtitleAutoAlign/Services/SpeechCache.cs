using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <summary>
/// Stores ffsubsync's speech analysis (<c>--serialize-speech</c> output) per video file,
/// so later alignments for the same video can skip the audio analysis.
/// An entry is keyed by video path, the extra ffsubsync arguments (they can change the
/// analysis), and the video's size and last-write time, so a replaced or still-copying
/// video never matches an older analysis.
/// </summary>
public sealed class SpeechCache
{
    // These make ffsubsync read the reference as a video even when it is an .npz,
    // so a saved analysis can't be used with them.
    private static readonly string[] UnsupportedOptions = ["--pgs-ref-stream", "--whisper-weights"];

    private readonly string _directory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechCache"/> class.
    /// </summary>
    /// <param name="directory">Folder that holds cache entries and work folders.</param>
    public SpeechCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    private string WorkDirectory => Path.Combine(_directory, "work");

    /// <summary>
    /// Whether alignments with these extra ffsubsync arguments can use a saved analysis.
    /// </summary>
    /// <param name="extraArguments">The configured extra arguments.</param>
    /// <returns>False when an option forces ffsubsync to read the video itself.</returns>
    public static bool CanCache(string? extraArguments) =>
        !FfSubSyncArgumentBuilder.TokenizeExtraArguments(extraArguments)
            .Any(token => UnsupportedOptions.Any(option => token.StartsWith(option, StringComparison.Ordinal)));

    /// <summary>
    /// Gets the path ffsubsync writes its analysis to when run against <paramref name="workLink"/>.
    /// </summary>
    /// <param name="workLink">A link created by <see cref="CreateWorkLink"/>.</param>
    /// <returns>The <c>.npz</c> path next to the link.</returns>
    public static string ProducedPath(string workLink) => Path.ChangeExtension(workLink, ".npz");

    /// <summary>
    /// The entry path for the video in its current state; take it before a run and pass it to <see cref="Store"/>.
    /// </summary>
    /// <param name="videoPath">Video file path.</param>
    /// <param name="extraArguments">The extra ffsubsync arguments the analysis is made with.</param>
    /// <returns>The entry path, or null if the video doesn't exist.</returns>
    public string? EntryPath(string videoPath, string? extraArguments)
    {
        var info = new FileInfo(videoPath);
        if (!info.Exists)
        {
            return null;
        }

        var argumentsKey = Hash(extraArguments ?? string.Empty)[..12];
        return Path.Combine(_directory, $"{VideoKey(videoPath)}-{argumentsKey}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.npz");
    }

    /// <summary>
    /// Returns the saved analysis for the video in its current state, or null.
    /// </summary>
    /// <param name="videoPath">Video file path.</param>
    /// <param name="extraArguments">The extra ffsubsync arguments in use.</param>
    /// <returns>The entry path, or null when none matches.</returns>
    public string? TryGet(string videoPath, string? extraArguments)
    {
        var entry = EntryPath(videoPath, extraArguments);
        return entry is not null && File.Exists(entry) ? entry : null;
    }

    /// <summary>
    /// Saves a freshly produced analysis as <paramref name="expectedEntry"/>, replacing older
    /// entries for the video. If the video changed since <paramref name="expectedEntry"/> was
    /// taken (e.g. it was still being copied), the analysis is discarded instead.
    /// </summary>
    /// <param name="videoPath">Video the analysis belongs to.</param>
    /// <param name="extraArguments">The extra ffsubsync arguments it was made with.</param>
    /// <param name="producedNpz">The <c>.npz</c> ffsubsync wrote.</param>
    /// <param name="expectedEntry">The <see cref="EntryPath"/> taken before the run.</param>
    /// <returns>True if saved.</returns>
    public bool Store(string videoPath, string? extraArguments, string producedNpz, string expectedEntry)
    {
        if (!string.Equals(EntryPath(videoPath, extraArguments), expectedEntry, StringComparison.Ordinal))
        {
            File.Delete(producedNpz);
            return false;
        }

        Directory.CreateDirectory(_directory);
        foreach (var file in EntriesFor(videoPath).Where(file => !string.Equals(file, expectedEntry, StringComparison.Ordinal)))
        {
            File.Delete(file);
        }

        File.Move(producedNpz, expectedEntry, overwrite: true);
        return true;
    }

    /// <summary>
    /// Deletes every saved analysis for the video.
    /// </summary>
    /// <param name="videoPath">Video file path.</param>
    public void Remove(string videoPath)
    {
        foreach (var file in EntriesFor(videoPath))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Creates a work folder holding a symlink to the video, so ffsubsync writes its
    /// analysis there instead of next to the video in the media folder.
    /// </summary>
    /// <param name="videoPath">Video file path.</param>
    /// <returns>The link path, or null if it could not be created.</returns>
    public string? CreateWorkLink(string videoPath)
    {
        try
        {
            var work = Path.Combine(WorkDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var link = Path.Combine(work, "ref" + Path.GetExtension(videoPath));
            File.CreateSymbolicLink(link, videoPath);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a work folder created by <see cref="CreateWorkLink"/>. Never throws.
    /// </summary>
    /// <param name="workLink">The link path.</param>
    public void DeleteWorkLink(string workLink)
    {
        TryDeleteDirectory(Path.GetDirectoryName(workLink));
    }

    /// <summary>
    /// Deletes work folders left behind by runs that never finished (e.g. a crash). Call before
    /// any run starts. Never throws.
    /// </summary>
    public void DeleteStaleWorkFolders() => TryDeleteDirectory(WorkDirectory);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string VideoKey(string videoPath) => Hash(videoPath);

    private static void TryDeleteDirectory(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover work folder only costs a few bytes.
        }
    }

    private string[] EntriesFor(string videoPath) =>
        Directory.Exists(_directory) ? Directory.GetFiles(_directory, VideoKey(videoPath) + "-*.npz") : [];
}
