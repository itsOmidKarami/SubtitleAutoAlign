using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <summary>
/// Pure diff/filter logic used to decide which subtitle files are new,
/// external, and eligible for auto-alignment.
/// </summary>
public static class SubtitleChangeDetector
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt",
        ".ass",
        ".ssa",
        ".sub",
        ".vtt",
    };

    /// <summary>
    /// Returns the subtitle paths present in <paramref name="current"/> but not
    /// in <paramref name="previous"/>.
    /// </summary>
    /// <param name="previous">Previously observed subtitle paths for an item.</param>
    /// <param name="current">Currently observed subtitle paths for an item.</param>
    /// <returns>Paths that newly appeared.</returns>
    public static IReadOnlyList<string> DiffNewSubtitlePaths(
        IReadOnlyCollection<string> previous,
        IReadOnlyCollection<string> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousSet = new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
        return current.Where(path => !previousSet.Contains(path)).ToList();
    }

    /// <summary>
    /// Keeps the subtitles last written at or after <paramref name="sinceUtc"/>. Used the
    /// first time an item is seen after startup, so subtitles that existed before the
    /// restart are not treated as new downloads. Files that can't be read are skipped.
    /// </summary>
    /// <param name="paths">Candidate subtitle paths.</param>
    /// <param name="sinceUtc">Watcher start time.</param>
    /// <param name="lastWriteUtc">Returns a file's last-write time in UTC.</param>
    /// <returns>The paths written since <paramref name="sinceUtc"/>.</returns>
    public static IReadOnlyList<string> WrittenSince(
        IEnumerable<string> paths,
        DateTime sinceUtc,
        Func<string, DateTime> lastWriteUtc)
    {
        return paths.Where(path =>
        {
            try
            {
                return lastWriteUtc(path) >= sinceUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }).ToList();
    }

    /// <summary>
    /// Determines whether a subtitle path is eligible for auto-alignment:
    /// a recognized subtitle extension, and not already an aligned output.
    /// </summary>
    /// <param name="subtitlePath">Candidate subtitle path.</param>
    /// <returns>True if the file should be considered for alignment.</returns>
    public static bool IsCandidateSubtitle(string subtitlePath)
    {
        if (string.IsNullOrEmpty(subtitlePath))
        {
            return false;
        }

        if (SubtitleFileNaming.IsAlreadyAligned(subtitlePath))
        {
            return false;
        }

        var extension = Path.GetExtension(subtitlePath);
        return SubtitleExtensions.Contains(extension);
    }
}
