using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

/// <summary>
/// Service for extracting frame timing information from MP4 files.
/// Replicates web UI's dashcam-mp4.js logic for timeline construction.
/// </summary>
public class Mp4TimingService : IMp4TimingService
{
    public async Task<Mp4FrameTimeline> GetFrameTimelineAsync(string videoFilePath)
    {
        if (!File.Exists(videoFilePath))
        {
            Log.Warning("Video file not found for timing extraction: {Path}", videoFilePath);
            return null;
        }

        try
        {
            return await Task.Run(() => ExtractTimeline(videoFilePath));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MP4 timing extraction error for {Path}", videoFilePath);
            return null;
        }
    }

    private Mp4FrameTimeline ExtractTimeline(string videoFilePath)
    {
        using var fs = new FileStream(videoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        // Navigate MP4 box hierarchy: moov → trak → mdia → minf → stbl
        var moov = FindBox(reader, "moov", 0, reader.BaseStream.Length);
        if (!moov.HasValue)
        {
            Log.Warning("No moov box found in {Path}", videoFilePath);
            return null;
        }

        var trak = FindBox(reader, "trak", moov.Value.start, moov.Value.end);
        if (!trak.HasValue)
        {
            Log.Warning("No trak box found in {Path}", videoFilePath);
            return null;
        }

        var mdia = FindBox(reader, "mdia", trak.Value.start, trak.Value.end);
        if (!mdia.HasValue)
        {
            Log.Warning("No mdia box found in {Path}", videoFilePath);
            return null;
        }

        // Extract timescale from mdhd atom (ticks per second)
        var mdhd = FindBox(reader, "mdhd", mdia.Value.start, mdia.Value.end);
        if (!mdhd.HasValue)
        {
            Log.Warning("No mdhd box found in {Path}", videoFilePath);
            return null;
        }

        reader.BaseStream.Seek(mdhd.Value.start, SeekOrigin.Begin);
        var mdhdVersion = reader.ReadByte();
        uint timescale;

        if (mdhdVersion == 1)
        {
            // Version 1: 64-bit values
            reader.BaseStream.Seek(mdhd.Value.start + 20, SeekOrigin.Begin);
            timescale = ReadUInt32BigEndian(reader);
        }
        else
        {
            // Version 0: 32-bit values
            reader.BaseStream.Seek(mdhd.Value.start + 12, SeekOrigin.Begin);
            timescale = ReadUInt32BigEndian(reader);
        }

        if (timescale == 0)
        {
            Log.Warning("Invalid timescale (0) in {Path}", videoFilePath);
            return null;
        }

        // Navigate to stbl (sample table)
        var minf = FindBox(reader, "minf", mdia.Value.start, mdia.Value.end);
        if (!minf.HasValue)
        {
            Log.Warning("No minf box found in {Path}", videoFilePath);
            return null;
        }

        var stbl = FindBox(reader, "stbl", minf.Value.start, minf.Value.end);
        if (!stbl.HasValue)
        {
            Log.Warning("No stbl box found in {Path}", videoFilePath);
            return null;
        }

        // Parse stts (time-to-sample) atom for frame durations
        var stts = FindBox(reader, "stts", stbl.Value.start, stbl.Value.end);
        if (!stts.HasValue)
        {
            Log.Warning("No stts box found in {Path}", videoFilePath);
            return null;
        }

        reader.BaseStream.Seek(stts.Value.start, SeekOrigin.Begin);
        var version = reader.ReadByte(); // version
        reader.ReadBytes(3); // flags
        var entryCount = ReadUInt32BigEndian(reader);

        var frameDurationsMs = new List<double>();
        for (uint i = 0; i < entryCount; i++)
        {
            var count = ReadUInt32BigEndian(reader);
            var delta = ReadUInt32BigEndian(reader);

            // Convert timescale delta to milliseconds: (delta / timescale) * 1000
            var durationMs = (delta / (double)timescale) * 1000.0;

            for (uint j = 0; j < count; j++)
            {
                frameDurationsMs.Add(durationMs);
            }
        }

        if (frameDurationsMs.Count == 0)
        {
            Log.Warning("No frame durations found in {Path}", videoFilePath);
            return null;
        }

        // Build cumulative frame timeline (like web UI's buildFrameTimeline)
        var frameStartsMs = new double[frameDurationsMs.Count];
        double acc = 0;
        for (int i = 0; i < frameDurationsMs.Count; i++)
        {
            frameStartsMs[i] = acc;
            acc += frameDurationsMs[i];
        }

        Log.Information(
            "Extracted MP4 timing for {Path}: {FrameCount} frames, {Duration:F2}s, timescale={Timescale}",
            videoFilePath,
            frameStartsMs.Length,
            acc / 1000.0,
            timescale);

        return new Mp4FrameTimeline
        {
            FrameStartsMs = frameStartsMs,
            TotalDurationMs = acc,
            Timescale = timescale
        };
    }

    /// <summary>
    /// Find MP4 box within a range. Returns (contentStart, contentEnd) positions.
    /// </summary>
    private (long start, long end)? FindBox(BinaryReader reader, string boxType, long searchStart, long searchEnd)
    {
        reader.BaseStream.Seek(searchStart, SeekOrigin.Begin);

        while (reader.BaseStream.Position + 8 <= searchEnd)
        {
            var pos = reader.BaseStream.Position;
            var boxSize = ReadUInt32BigEndian(reader);
            var type = new string(reader.ReadChars(4));

            if (type == boxType)
            {
                long contentStart = pos + 8;
                long contentEnd = pos + boxSize;
                return (contentStart, contentEnd);
            }

            if (boxSize == 0)
            {
                // Box extends to end of file
                boxSize = (uint)(searchEnd - pos);
            }
            else if (boxSize == 1)
            {
                // 64-bit box size (not commonly used in Tesla videos)
                Log.Warning("64-bit box sizes not supported");
                return null;
            }

            reader.BaseStream.Seek(pos + boxSize, SeekOrigin.Begin);
        }

        return null;
    }

    /// <summary>
    /// Read 32-bit big-endian unsigned integer.
    /// </summary>
    private uint ReadUInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return BitConverter.ToUInt32(bytes, 0);
    }
}
