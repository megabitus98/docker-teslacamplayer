using System;
using System.Linq;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using Xunit;

namespace TeslaCamPlayer.Tests;

public class ExportEncoderTests
{
    [Theory]
    [InlineData("high", "slow", "17")]
    [InlineData("low", "fast", "24")]
    [InlineData("medium", "medium", "20")]
    [InlineData(null, "medium", "20")]
    public void CodecArgs_Libx264_KeepsExistingPresetCrf(string quality, string preset, string crf)
    {
        var args = ExportEncoder.CodecArgs("libx264", quality);
        Assert.Equal(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", preset, "-crf", crf }, args);
    }

    [Theory]
    [InlineData("high", "18")]
    [InlineData("low", "28")]
    [InlineData("medium", "23")]
    public void CodecArgs_Nvenc(string quality, string cq)
    {
        var args = ExportEncoder.CodecArgs("h264_nvenc", quality);
        Assert.Equal(new[] { "-c:v", "h264_nvenc", "-pix_fmt", "yuv420p", "-preset", "p5", "-rc", "vbr", "-cq", cq }, args);
    }

    [Theory]
    [InlineData("high", "18")]
    [InlineData("low", "28")]
    [InlineData("medium", "23")]
    public void CodecArgs_Qsv(string quality, string q)
    {
        var args = ExportEncoder.CodecArgs("h264_qsv", quality);
        Assert.Equal(new[] { "-c:v", "h264_qsv", "-pix_fmt", "nv12", "-global_quality", q }, args);
    }

    [Theory]
    [InlineData("high", "18")]
    [InlineData("low", "28")]
    [InlineData("medium", "23")]
    public void CodecArgs_Vaapi(string quality, string qp)
    {
        var args = ExportEncoder.CodecArgs("h264_vaapi", quality);
        Assert.Equal(new[] { "-c:v", "h264_vaapi", "-qp", qp }, args);
    }

    [Theory]
    [InlineData("high", "65")]
    [InlineData("low", "45")]
    [InlineData("medium", "55")]
    public void CodecArgs_VideoToolbox(string quality, string q)
    {
        var args = ExportEncoder.CodecArgs("h264_videotoolbox", quality);
        Assert.Equal(new[] { "-c:v", "h264_videotoolbox", "-pix_fmt", "yuv420p", "-q:v", q }, args);
    }

    [Fact]
    public void CodecArgs_UnknownEncoder_FallsBackToSoftware()
        => Assert.Contains("libx264", ExportEncoder.CodecArgs("h264_bogus", "medium"));

    [Fact]
    public void FilterSuffix_OnlyVaapi()
    {
        Assert.Equal(",format=nv12,hwupload", ExportEncoder.FilterSuffix("h264_vaapi"));
        Assert.All(
            ExportEncoder.CandidatesInPriorityOrder.Where(e => e != "h264_vaapi"),
            e => Assert.Equal("", ExportEncoder.FilterSuffix(e)));
    }

    [Fact]
    public void GlobalArgs_OnlyVaapi()
    {
        Assert.Equal(new[] { "-vaapi_device", "/dev/dri/renderD128" }, ExportEncoder.GlobalArgs("h264_vaapi"));
        Assert.Empty(ExportEncoder.GlobalArgs("libx264"));
    }

    [Fact]
    public void Candidates_EndWithSoftwareFloor()
        => Assert.Equal(ExportEncoder.Software, ExportEncoder.CandidatesInPriorityOrder.Last());
}
