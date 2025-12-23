using System.Collections.Generic;
using TeslaCamPlayer.BlazorHosted.Server.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface ISeiParserService
{
    List<SeiMetadata> ExtractSeiMessages(string videoFilePath);
    SeiMetadata GetSeiForTime(List<SeiMetadata> messages, double timeSeconds, double frameRate = 30.0);

    /// <summary>
    /// Extract SEI messages for a time range using MP4 frame timeline.
    /// Maps video time (seconds from video start) to SEI frame indices.
    /// </summary>
    /// <param name="allMessages">All SEI messages from the video file</param>
    /// <param name="timeline">MP4 frame timeline for time-to-frame mapping</param>
    /// <param name="startSeconds">Start time in seconds from video start</param>
    /// <param name="durationSeconds">Duration in seconds</param>
    /// <returns>List of SEI messages for the specified time range</returns>
    List<SeiMetadata> ExtractSeiMessagesForTimeRange(
        List<SeiMetadata> allMessages,
        Mp4FrameTimeline timeline,
        double startSeconds,
        double durationSeconds);
}
