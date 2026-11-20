using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleAutoAlign.Models;

namespace Jellyfin.Plugin.SubtitleAutoAlign.Services;

/// <summary>
/// Runs ffsubsync to produce an aligned copy of a subtitle file.
/// </summary>
public interface ISubtitleAlignmentService
{
    /// <summary>
    /// Aligns the subtitle described by <paramref name="request"/> against its
    /// reference video, writing the result alongside the original.
    /// </summary>
    /// <param name="request">The alignment request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The outcome of the alignment attempt. Never throws on ffsubsync
    /// failure; callers can rely on the returned result instead.</returns>
    Task<AlignmentResult> AlignAsync(AlignmentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Analyses a video's speech and saves it, so later alignments for it are fast.
    /// </summary>
    /// <param name="videoPath">The video file.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True when an analysis for the video is saved (already or now). Never throws on ffsubsync failure.</returns>
    Task<bool> PreAnalyseAsync(string videoPath, CancellationToken cancellationToken);
}
