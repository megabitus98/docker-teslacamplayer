using System;
using System.Collections.Generic;
using System.Linq;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

/// <summary>
/// Normalize is the one piece of pure logic the whole multi-interval export hangs off: the black
/// filler lengths, the per-interval timestamp offsets, the HUD frame count and the progress bar are
/// all derived from its output.
/// </summary>
public class ExportIntervalTests
{
    private static readonly DateTime ClipStart = new(2026, 7, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClipEnd = ClipStart.AddMinutes(10);

    private static ExportInterval At(double startMinutes, double endMinutes)
        => new() { StartTimeUtc = ClipStart.AddMinutes(startMinutes), EndTimeUtc = ClipStart.AddMinutes(endMinutes) };

    private static List<(double Start, double End)> Minutes(IEnumerable<ExportInterval> intervals)
        => intervals.Select(i => ((i.StartTimeUtc - ClipStart).TotalMinutes, (i.EndTimeUtc - ClipStart).TotalMinutes)).ToList();

    [Fact]
    public void ClampsToClipBounds()
    {
        var result = ExportInterval.Normalize(new[] { At(-5, 3) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (0d, 3d) }, Minutes(result));

        result = ExportInterval.Normalize(new[] { At(8, 30) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (8d, 10d) }, Minutes(result));
    }

    [Fact]
    public void DropsEmptyInvertedAndOutOfRange()
    {
        var result = ExportInterval.Normalize(
            new[] { At(2, 2), At(5, 4), At(20, 25) },
            ClipStart,
            ClipEnd);

        Assert.Empty(result);
    }

    [Fact]
    public void SortsByStart()
    {
        var result = ExportInterval.Normalize(new[] { At(6, 7), At(1, 2), At(4, 5) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (1d, 2d), (4d, 5d), (6d, 7d) }, Minutes(result));
    }

    [Fact]
    public void MergesOverlapping()
    {
        // Without merging, the overlap is counted twice in the total and the HUD desyncs.
        var result = ExportInterval.Normalize(new[] { At(1, 4), At(3, 6) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (1d, 6d) }, Minutes(result));
    }

    [Fact]
    public void MergesTouchingAndFullyContained()
    {
        var result = ExportInterval.Normalize(new[] { At(1, 3), At(3, 5) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (1d, 5d) }, Minutes(result));

        result = ExportInterval.Normalize(new[] { At(1, 8), At(3, 5) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (1d, 8d) }, Minutes(result));
    }

    [Fact]
    public void KeepsDisjointIntervalsApart()
    {
        var result = ExportInterval.Normalize(new[] { At(1, 2), At(5, 7) }, ClipStart, ClipEnd);
        Assert.Equal(new[] { (1d, 2d), (5d, 7d) }, Minutes(result));
    }

    [Fact]
    public void TotalSecondsSumsGapsOutNotSpan()
    {
        // The span here is 6 minutes; the export is only 3. Using the span would stall the progress
        // bar and, via the HUD frame count and shortest=1 overlay, truncate the output.
        var result = ExportInterval.Normalize(new[] { At(1, 2), At(5, 7) }, ClipStart, ClipEnd);
        Assert.Equal(TimeSpan.FromMinutes(3).TotalSeconds, ExportInterval.TotalSeconds(result));
    }

    [Fact]
    public void HandlesNullAndEmptyInput()
    {
        Assert.Empty(ExportInterval.Normalize(null, ClipStart, ClipEnd));
        Assert.Empty(ExportInterval.Normalize(Array.Empty<ExportInterval>(), ClipStart, ClipEnd));
        Assert.Equal(0, ExportInterval.TotalSeconds(null));
    }

    [Fact]
    public void DoesNotMutateTheCallersIntervals()
    {
        var original = At(-5, 30);
        ExportInterval.Normalize(new[] { original }, ClipStart, ClipEnd);

        Assert.Equal(ClipStart.AddMinutes(-5), original.StartTimeUtc);
        Assert.Equal(ClipStart.AddMinutes(30), original.EndTimeUtc);
    }
}
