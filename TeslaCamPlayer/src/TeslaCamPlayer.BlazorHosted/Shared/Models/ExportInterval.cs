using System;
using System.Collections.Generic;
using System.Linq;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>One selected time range of a clip. Several of these concatenate into a single export.</summary>
public class ExportInterval
{
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }

    public double DurationSeconds => (EndTimeUtc - StartTimeUtc).TotalSeconds;

    /// <summary>
    /// Clamps each interval to the clip, drops empty ones, sorts by start and merges overlapping or
    /// touching neighbours. Merging matters: overlapping intervals would otherwise be counted twice in
    /// the total duration, which drives the HUD frame count, the black filler lengths and the progress bar.
    /// </summary>
    public static List<ExportInterval> Normalize(IEnumerable<ExportInterval> intervals, DateTime clipStart, DateTime clipEnd)
    {
        var clamped = (intervals ?? Enumerable.Empty<ExportInterval>())
            .Where(i => i != null)
            .Select(i => new ExportInterval
            {
                StartTimeUtc = i.StartTimeUtc < clipStart ? clipStart : i.StartTimeUtc,
                EndTimeUtc = i.EndTimeUtc > clipEnd ? clipEnd : i.EndTimeUtc
            })
            .Where(i => i.EndTimeUtc > i.StartTimeUtc)
            .OrderBy(i => i.StartTimeUtc)
            .ToList();

        var merged = new List<ExportInterval>();
        foreach (var interval in clamped)
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (last != null && interval.StartTimeUtc <= last.EndTimeUtc)
            {
                if (interval.EndTimeUtc > last.EndTimeUtc)
                    last.EndTimeUtc = interval.EndTimeUtc;
                continue;
            }

            merged.Add(interval);
        }

        return merged;
    }

    public static double TotalSeconds(IEnumerable<ExportInterval> intervals)
        => (intervals ?? Enumerable.Empty<ExportInterval>()).Sum(i => i.DurationSeconds);
}
