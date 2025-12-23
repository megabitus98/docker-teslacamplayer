using Google.Protobuf;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class SeiParserService : ISeiParserService
{
    public List<SeiMetadata> ExtractSeiMessages(string videoFilePath)
    {
        if (!File.Exists(videoFilePath))
        {
            Log.Warning("Video file not found for SEI extraction: {Path}", videoFilePath);
            return new List<SeiMetadata>();
        }

        var messages = new List<SeiMetadata>();

        try
        {
            using var fs = new FileStream(videoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            // Find mdat box
            var mdatOffset = FindMdatBox(reader);
            if (mdatOffset < 0)
            {
                Log.Debug("No mdat box found in {Path}", videoFilePath);
                return messages;
            }

            reader.BaseStream.Seek(mdatOffset, SeekOrigin.Begin);
            var mdatBoxSize = ReadUInt32BigEndian(reader);
            reader.BaseStream.Seek(4, SeekOrigin.Current); // Skip "mdat" fourcc

            var mdatDataSize = mdatBoxSize - 8; // Subtract header size
            long endPosition = reader.BaseStream.Position + mdatDataSize;

            // Parse NAL units within mdat
            while (reader.BaseStream.Position + 4 < endPosition)
            {
                var nalSize = ReadUInt32BigEndian(reader);

                if (nalSize < 2 || reader.BaseStream.Position + nalSize > endPosition)
                {
                    if (nalSize > 0 && nalSize < 1000000) // Sanity check
                    {
                        reader.BaseStream.Seek(nalSize, SeekOrigin.Current);
                    }
                    continue;
                }

                var nalStartPos = reader.BaseStream.Position;
                var nalHeader = reader.ReadByte();
                var nalType = nalHeader & 0x1F;
                var payloadType = nalSize > 1 ? reader.ReadByte() : (byte)0;

                // NAL type 6 = SEI, payload type 5 = user data unregistered
                if (nalType == 6 && payloadType == 5)
                {
                    reader.BaseStream.Seek(nalStartPos, SeekOrigin.Begin);
                    var nalData = new byte[nalSize];
                    reader.Read(nalData, 0, (int)nalSize);

                    var sei = DecodeSei(nalData);
                    if (sei != null)
                    {
                        messages.Add(sei);
                    }
                }

                reader.BaseStream.Seek(nalStartPos + nalSize, SeekOrigin.Begin);
            }

            Log.Information("Extracted {Count} SEI messages from {Path}", messages.Count, videoFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SEI extraction error for {Path}", videoFilePath);
        }

        return messages;
    }

    private SeiMetadata DecodeSei(byte[] nalData)
    {
        if (nalData.Length < 4)
        {
            return null;
        }

        // Skip NAL header and find UUID marker
        // Tesla SEI has signature: multiple 0x42 bytes followed by 0x69
        int i = 3;
        while (i < nalData.Length && nalData[i] == 0x42)
        {
            i++;
        }

        if (i <= 3 || i + 1 >= nalData.Length || nalData[i] != 0x69)
        {
            return null;
        }

        try
        {
            // Remove H.264 emulation prevention bytes and parse protobuf
            var payloadStart = i + 1;
            var payloadEnd = nalData.Length - 1; // Remove trailing 0x80 stop bit
            var payload = StripEmulationBytes(nalData, payloadStart, payloadEnd - payloadStart);

            return SeiMetadata.Parser.ParseFrom(payload);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to decode SEI protobuf: {Message}", ex.Message);
            return null;
        }
    }

    private byte[] StripEmulationBytes(byte[] data, int start, int length)
    {
        var result = new List<byte>();
        int zeros = 0;

        for (int i = start; i < start + length && i < data.Length; i++)
        {
            byte b = data[i];

            // H.264 emulation prevention: skip 0x03 after two 0x00 bytes
            if (zeros >= 2 && b == 0x03)
            {
                zeros = 0;
                continue;
            }

            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return result.ToArray();
    }

    private long FindMdatBox(BinaryReader reader)
    {
        reader.BaseStream.Seek(0, SeekOrigin.Begin);

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var boxSize = ReadUInt32BigEndian(reader);
            var boxType = new string(reader.ReadChars(4));

            if (boxType == "mdat")
            {
                return reader.BaseStream.Position - 8;
            }

            if (boxSize == 0)
            {
                // Box extends to end of file
                boxSize = (uint)(reader.BaseStream.Length - (reader.BaseStream.Position - 8));
            }
            else if (boxSize == 1)
            {
                // 64-bit size (not handling this case for simplicity)
                Log.Warning("64-bit box sizes not supported");
                return -1;
            }

            reader.BaseStream.Seek(reader.BaseStream.Position - 8 + boxSize, SeekOrigin.Begin);
        }

        return -1;
    }

    private uint ReadUInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }
        return BitConverter.ToUInt32(bytes, 0);
    }

    public SeiMetadata GetSeiForTime(List<SeiMetadata> messages, double timeSeconds, double frameRate = 30.0)
    {
        if (messages == null || messages.Count == 0)
        {
            return null;
        }

        var frameIndex = (int)(timeSeconds * frameRate);

        if (frameIndex >= 0 && frameIndex < messages.Count)
        {
            return messages[frameIndex];
        }

        // Return last message if beyond end
        return messages[messages.Count - 1];
    }

    public List<SeiMetadata> ExtractSeiMessagesForTimeRange(
        List<SeiMetadata> allMessages,
        Mp4FrameTimeline timeline,
        double startSeconds,
        double durationSeconds)
    {
        if (allMessages == null || allMessages.Count == 0 || timeline == null)
        {
            Log.Warning("Cannot extract SEI messages: null input");
            return new List<SeiMetadata>();
        }

        var startMs = startSeconds * 1000.0;
        var endMs = (startSeconds + durationSeconds) * 1000.0;

        // Binary search for start and end frame indices using timeline
        var startFrameIndex = timeline.FindFrameIndexForMs(startMs);
        var endFrameIndex = timeline.FindFrameIndexForMs(endMs);

        if (startFrameIndex < 0 || endFrameIndex < 0)
        {
            Log.Warning("Frame indices not found for time range [{Start:F2}s - {End:F2}s]",
                startSeconds, startSeconds + durationSeconds);
            return new List<SeiMetadata>();
        }

        // Clamp to SEI message bounds
        startFrameIndex = Math.Max(0, startFrameIndex);
        endFrameIndex = Math.Min(allMessages.Count - 1, endFrameIndex);

        if (endFrameIndex < startFrameIndex)
        {
            Log.Warning("Invalid frame range for SEI extraction: [{Start}..{End}]",
                startFrameIndex, endFrameIndex);
            return new List<SeiMetadata>();
        }

        var count = endFrameIndex - startFrameIndex + 1;
        var result = allMessages.GetRange(startFrameIndex, count);

        Log.Debug(
            "SEI time-based extraction: time=[{StartS:F2}s..{EndS:F2}s], frames=[{StartF}..{EndF}], count={Count}",
            startSeconds, startSeconds + durationSeconds,
            startFrameIndex, endFrameIndex,
            result.Count);

        return result;
    }
}
