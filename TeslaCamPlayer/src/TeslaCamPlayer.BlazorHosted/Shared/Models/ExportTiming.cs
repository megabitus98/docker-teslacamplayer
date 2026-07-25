using System;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>
/// Frame-grid arithmetic for the export pipeline. ffmpeg emits whole frames, so a chunk of D seconds
/// at F fps occupies ceil(F*D) frames — slightly more output time than D. Getting this wrong is silent:
/// the HUD overlay runs with shortest=1, so a HUD that comes out shorter than the video trims the video
/// and ffmpeg still exits 0.
/// </summary>
public static class ExportTiming
{
    /// <summary>Output time a chunk of <paramref name="duration"/> actually occupies once snapped to the frame grid.</summary>
    public static double ChunkOutputSeconds(double duration, double fps)
        => fps <= 0 ? duration : Math.Ceiling(duration * fps) / fps;

    /// <summary>
    /// How long the HUD frame sequence must be to cover a video built from <paramref name="chunkCount"/>
    /// chunks totalling <paramref name="totalSeconds"/>.
    /// The video is sum(ceil(fps*Di)) frames and the HUD is ceil(fps*sum(Di)); since sum(ceil(x)) >= ceil(sum(x))
    /// the video is otherwise always the longer input. sum(ceil(fps*Di)) &lt;= fps*total + n bounds the gap at
    /// n frames, so n/fps seconds of slack always makes the HUD the shorter input.
    /// </summary>
    public static double HudDurationSeconds(double totalSeconds, int chunkCount, double fps)
        => fps <= 0 ? totalSeconds : totalSeconds + Math.Max(0, chunkCount) / fps;
}
