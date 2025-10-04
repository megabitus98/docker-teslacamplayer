using System;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private double DateTimeToTimelinePercentage(DateTime dateTime)
    {
        var percentage = Math.Round(dateTime.Subtract(_clip.StartDate).TotalSeconds / _clip.TotalSeconds * 100, 2);
        return Math.Clamp(percentage, 0, 100);
    }

    private string SegmentStartMarkerStyle(ClipVideoSegment segment)
    {
        var percentage = DateTimeToTimelinePercentage(segment.StartDate);
        return $"left: {percentage}%";
    }

    private string EventMarkerStyle()
    {
        if (_clip?.Event?.Timestamp == null)
        {
            return "display: none";
        }

        var percentage = DateTimeToTimelinePercentage(_clip.Event.Timestamp);
        return $"left: {percentage}%";
    }
}
