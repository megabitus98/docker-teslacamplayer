namespace TeslaCamPlayer.BlazorHosted.Server.Models;

/// <summary>
/// Represents the frame timing timeline extracted from MP4 stts atom.
/// Provides binary search for time-to-frame index mapping.
/// </summary>
public class Mp4FrameTimeline
{
    /// <summary>
    /// Array of frame start times in milliseconds (cumulative).
    /// Frame i is valid from FrameStartsMs[i] to FrameStartsMs[i+1].
    /// </summary>
    public double[] FrameStartsMs { get; set; }

    /// <summary>
    /// Total duration of the video timeline in milliseconds.
    /// </summary>
    public double TotalDurationMs { get; set; }

    /// <summary>
    /// MP4 timescale (ticks per second) from mdhd atom.
    /// </summary>
    public uint Timescale { get; set; }

    /// <summary>
    /// Number of frames in the timeline.
    /// </summary>
    public int FrameCount => FrameStartsMs?.Length ?? 0;

    /// <summary>
    /// Binary search to find frame index for a given time in milliseconds.
    /// Matches web UI logic (sei-parser-interop.js:findFrameIndexForMs).
    /// </summary>
    /// <param name="targetMs">Target time in milliseconds</param>
    /// <returns>Frame index, or -1 if timeline is invalid</returns>
    public int FindFrameIndexForMs(double targetMs)
    {
        if (FrameStartsMs == null || FrameStartsMs.Length == 0)
            return -1;

        // Time before first frame → return frame 0
        if (targetMs <= FrameStartsMs[0])
            return 0;

        // Binary search for frame containing targetMs
        int low = 0, high = FrameStartsMs.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            double midStart = FrameStartsMs[mid];
            double nextStart = mid + 1 < FrameStartsMs.Length
                ? FrameStartsMs[mid + 1]
                : double.PositiveInfinity;

            if (targetMs < midStart)
            {
                high = mid - 1;
            }
            else if (targetMs >= nextStart)
            {
                low = mid + 1;
            }
            else
            {
                // Found: midStart <= targetMs < nextStart
                return mid;
            }
        }

        // Time after last frame → return last frame
        return FrameStartsMs.Length - 1;
    }
}
