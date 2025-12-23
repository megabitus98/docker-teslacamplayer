using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

/// <summary>
/// Service for extracting frame timing information from MP4 files.
/// </summary>
public interface IMp4TimingService
{
    /// <summary>
    /// Extract frame timing information from MP4 file using stts atom.
    /// Returns a timeline with frame start times for time-to-frame mapping.
    /// </summary>
    /// <param name="videoFilePath">Path to MP4 video file</param>
    /// <returns>Frame timeline, or null if extraction fails</returns>
    Task<Mp4FrameTimeline> GetFrameTimelineAsync(string videoFilePath);
}
