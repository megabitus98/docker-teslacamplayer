using System;
using System.Linq;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

/// <summary>
/// The HUD overlay is applied with shortest=1, so if the HUD frame sequence is shorter than the video
/// ffmpeg silently trims the video and still exits 0. These pin the arithmetic that prevents that.
/// </summary>
public class ExportTimingTests
{
    private const double Fps = 30.0;

    // Chunk durations that actually occur: whole frames, awkward fractions, and many small pieces.
    public static TheoryData<double[]> ChunkSets => new()
    {
        new[] { 10.0, 10.0, 15.0 },
        new[] { 8.117, 8.117, 8.117 },
        new[] { 25.41, 25.41, 25.41 },
        new[] { 8.12, 8.12 },
        Enumerable.Repeat(0.1, 25).ToArray(),
        new[] { 178.0 },
        new[] { 0.021 },
    };

    private static double VideoFrames(double[] chunks)
        => chunks.Sum(d => Math.Ceiling(Fps * d));

    private static double HudFrames(double[] chunks)
        => Math.Ceiling(Fps * ExportTiming.HudDurationSeconds(chunks.Sum(), chunks.Length, Fps));

    [Theory]
    [MemberData(nameof(ChunkSets))]
    public void HudIsNeverShorterThanTheVideo(double[] chunks)
    {
        // The invariant that keeps shortest=1 trimming the HUD instead of the export.
        Assert.True(
            HudFrames(chunks) >= VideoFrames(chunks),
            $"HUD {HudFrames(chunks)} frames < video {VideoFrames(chunks)} frames — export would be truncated");
    }

    [Fact]
    public void OldFormulaTruncatedTheVideo()
    {
        // Documents the bug this replaced: the HUD length used to be ceil(fps * total), and
        // sum(ceil(x)) > ceil(sum(x)) whenever the chunks aren't whole frames.
        var chunks = new[] { 25.41, 25.41, 25.41 };
        var oldHudFrames = Math.Ceiling(Fps * chunks.Sum());

        Assert.True(oldHudFrames < VideoFrames(chunks));
        Assert.Equal(2, VideoFrames(chunks) - oldHudFrames);

        // Whole-frame durations hid it — which is why the original exports all measured clean.
        var wholeFrames = new[] { 10.0, 10.0, 15.0 };
        Assert.Equal(VideoFrames(wholeFrames), Math.Ceiling(Fps * wholeFrames.Sum()));
    }

    [Fact]
    public void ChunkOutputSecondsSnapsUpToTheFrameGrid()
    {
        Assert.Equal(10.0, ExportTiming.ChunkOutputSeconds(10.0, Fps));      // already whole frames
        Assert.Equal(244 / Fps, ExportTiming.ChunkOutputSeconds(8.12, Fps)); // ceil(243.6) = 244
        Assert.Equal(1 / Fps, ExportTiming.ChunkOutputSeconds(0.021, Fps));  // sub-frame still costs a frame
    }

    [Theory]
    [MemberData(nameof(ChunkSets))]
    public void ChunkOutputSecondsNeverShortensAChunk(double[] chunks)
    {
        foreach (var d in chunks)
        {
            var snapped = ExportTiming.ChunkOutputSeconds(d, Fps);
            Assert.True(snapped >= d, $"{snapped} < {d}");
            Assert.True(snapped - d < 1 / Fps, "snapped more than one frame past the chunk");
        }
    }

    [Fact]
    public void DegenerateFrameRatesDoNotDivideByZero()
    {
        Assert.Equal(5.0, ExportTiming.ChunkOutputSeconds(5.0, 0));
        Assert.Equal(5.0, ExportTiming.HudDurationSeconds(5.0, 3, 0));
        Assert.Equal(5.0, ExportTiming.HudDurationSeconds(5.0, -1, Fps));
    }
}
