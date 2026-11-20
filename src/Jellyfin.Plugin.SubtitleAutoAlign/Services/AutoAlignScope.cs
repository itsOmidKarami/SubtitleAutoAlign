using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <summary>
/// Decides which library items the automatic watcher handles.
/// </summary>
public static class AutoAlignScope
{
    /// <summary>
    /// Movies and TV episodes are watched; trailers, extras and other videos are not.
    /// </summary>
    /// <param name="item">The updated library item.</param>
    /// <returns>True if new subtitles on this item should be auto-aligned.</returns>
    public static bool IsWatchedType(BaseItem item) => item is Movie or Episode;

    /// <summary>
    /// True when the item has no library, or at least one of its libraries is not disabled.
    /// </summary>
    /// <param name="itemLibraryIds">IDs of the libraries (collection folders) the item belongs to.</param>
    /// <param name="disabledLibraryIds">Library IDs the user unticked, in any GUID format; unparsable entries are ignored, null means none.</param>
    /// <returns>True if the item is in a watched library.</returns>
    public static bool IsInEnabledLibrary(IEnumerable<Guid> itemLibraryIds, IEnumerable<string>? disabledLibraryIds)
    {
        var disabled = ParseIds(disabledLibraryIds);
        var libraries = itemLibraryIds.ToList();
        return libraries.Count == 0 || libraries.Any(id => !disabled.Contains(id));
    }

    /// <summary>
    /// True only when one of the item's libraries is in <paramref name="selectedLibraryIds"/>
    /// (opt-in lists, such as pre-analysis).
    /// </summary>
    /// <param name="itemLibraryIds">IDs of the libraries the item belongs to.</param>
    /// <param name="selectedLibraryIds">Ticked library IDs, in any GUID format; null means none.</param>
    /// <returns>True if the item is in a ticked library.</returns>
    public static bool IsInSelectedLibrary(IEnumerable<Guid> itemLibraryIds, IEnumerable<string>? selectedLibraryIds)
    {
        var selected = ParseIds(selectedLibraryIds);
        return itemLibraryIds.Any(selected.Contains);
    }

    /// <summary>
    /// True when ffsubsync can analyse the path: an existing file that isn't a <c>.strm</c>
    /// pointer. DVD/Blu-ray movies (a folder) and missing files are skipped.
    /// </summary>
    /// <param name="videoPath">The item's path.</param>
    /// <returns>True if the path is an analysable video file.</returns>
    public static bool IsAnalysableFile(string? videoPath) =>
        !string.IsNullOrEmpty(videoPath)
        && System.IO.File.Exists(videoPath)
        && !string.Equals(System.IO.Path.GetExtension(videoPath), ".strm", StringComparison.OrdinalIgnoreCase);

    private static HashSet<Guid> ParseIds(IEnumerable<string>? ids) => (ids ?? [])
        .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
        .Where(guid => guid != Guid.Empty)
        .ToHashSet();
}
