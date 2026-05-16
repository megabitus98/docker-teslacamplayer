using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

/// <summary>
/// Reads the <c>mvhd</c> atom of an ISO BMFF (.mp4) file to obtain duration without spawning ffprobe.
/// Returns null on any failure so the caller can fall back to ffprobe.
///
/// Optimized for TeslaCam files which place <c>moov</c> at the end of the file behind a large
/// <c>mdat</c>. The reader does a single tail read first to find <c>moov</c> there; if the file
/// uses a different layout (e.g. faststart with <c>moov</c> at the start), it falls back to a
/// top-level box walk.
/// </summary>
public static class Mp4DurationReader
{
    // Top-level box scan ceiling — guards against malformed files. Real Tesla MP4s have ~3 top-level boxes.
    private const int MaxTopLevelBoxes = 64;

    // Cap on how much of the moov box we'll buffer to find mvhd. mvhd is normally the first child of moov.
    private const int MoovReadCap = 256 * 1024;

    // Single read from the tail of the file to find moov in TeslaCam layouts. Sized to comfortably
    // contain moov + 4 bytes of slack: Tesla moov atoms run ~5-50 KiB.
    private const int TailReadSize = 256 * 1024;

    public static async Task<TimeSpan?> TryReadDurationAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await ReadDurationFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Mp4DurationReader failed for {Path}", path);
            return null;
        }
    }

    private static async Task<TimeSpan?> ReadDurationFromStreamAsync(Stream stream, CancellationToken ct)
    {
        var fileLength = stream.Length;
        if (fileLength < 16) return null;

        // Fast path: one read from the tail of the file. Saves 2-3 small seek-and-reads on
        // spinning disks where every random read costs a seek (~10ms+).
        var tail = await TryReadFromTailAsync(stream, fileLength, ct).ConfigureAwait(false);
        if (tail.HasValue) return tail;

        // Fallback: walk top-level boxes from the start of the file.
        return await WalkBoxesAsync(stream, fileLength, ct).ConfigureAwait(false);
    }

    private static async Task<TimeSpan?> TryReadFromTailAsync(Stream stream, long fileLength, CancellationToken ct)
    {
        var readSize = (int)Math.Min(TailReadSize, fileLength);
        var tailStart = fileLength - readSize;
        var buffer = new byte[readSize];

        stream.Position = tailStart;
        if (!await TryReadExactAsync(stream, buffer.AsMemory(0, readSize), ct).ConfigureAwait(false))
            return null;

        var match = FindMoovInTail(buffer, readSize, tailStart, fileLength);
        if (match.HasValue)
        {
            var (contentBufferOffset, contentOffset, contentLen, fitsInBuffer) = match.Value;
            if (fitsInBuffer)
            {
                return FindMvhdAndParse(buffer, contentBufferOffset, (int)contentLen);
            }
            // moov extends past the tail buffer — re-read it bounded by MoovReadCap.
            return await ReadMoovDurationAsync(stream, contentOffset, contentLen, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static (int contentBufferOffset, long contentOffset, long contentLen, bool fitsInBuffer)? FindMoovInTail(
        byte[] buffer, int readSize, long tailStart, long fileLength)
    {
        // Scan for the "moov" signature. The 4 bytes immediately before are the box size.
        // Validate by checking that size matches the remaining file length from that position.
        for (var i = 4; i <= readSize - 4; i++)
        {
            if (buffer[i] != (byte)'m' || buffer[i + 1] != (byte)'o' ||
                buffer[i + 2] != (byte)'o' || buffer[i + 3] != (byte)'v')
                continue;

            var sizeOffset = i - 4;
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sizeOffset, 4));
            var boxFileOffset = tailStart + sizeOffset;
            long contentOffset;
            long contentLen;

            if (size == 1)
            {
                if (sizeOffset + 16 > readSize) continue;
                var large = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(sizeOffset + 8, 8));
                if (large < 16 || boxFileOffset + large != fileLength) continue;
                contentOffset = boxFileOffset + 16;
                contentLen = large - 16;
            }
            else if (size == 0)
            {
                if (boxFileOffset + 8 > fileLength) continue;
                contentOffset = boxFileOffset + 8;
                contentLen = fileLength - contentOffset;
            }
            else
            {
                // For a valid moov, the box should extend exactly to EOF when it's the last box.
                if (size < 8 || boxFileOffset + size != fileLength) continue;
                contentOffset = boxFileOffset + 8;
                contentLen = size - 8;
            }

            var contentBufferOffset = (int)(contentOffset - tailStart);
            var fitsInBuffer = contentBufferOffset >= 0 && contentBufferOffset + contentLen <= readSize;
            return (contentBufferOffset, contentOffset, contentLen, fitsInBuffer);
        }
        return null;
    }

    private static async Task<TimeSpan?> WalkBoxesAsync(Stream stream, long fileLength, CancellationToken ct)
    {
        var header = new byte[16];
        var scanned = 0;
        var position = 0L;

        while (position < fileLength && scanned < MaxTopLevelBoxes)
        {
            ct.ThrowIfCancellationRequested();
            stream.Position = position;

            if (!await TryReadExactAsync(stream, header.AsMemory(0, 8), ct).ConfigureAwait(false))
                return null;

            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);

            long contentOffset;
            long boxEnd;

            if (size == 1)
            {
                if (!await TryReadExactAsync(stream, header.AsMemory(8, 8), ct).ConfigureAwait(false))
                    return null;
                var largesize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (largesize < 16 || largesize > fileLength - position) return null;
                contentOffset = position + 16;
                boxEnd = position + largesize;
            }
            else if (size == 0)
            {
                contentOffset = position + 8;
                boxEnd = fileLength;
            }
            else if (size < 8 || size > fileLength - position)
            {
                return null;
            }
            else
            {
                contentOffset = position + 8;
                boxEnd = position + size;
            }

            if (type == "moov")
            {
                return await ReadMoovDurationAsync(stream, contentOffset, boxEnd - contentOffset, ct).ConfigureAwait(false);
            }

            position = boxEnd;
            scanned++;
        }

        return null;
    }

    private static TimeSpan? FindMvhdAndParse(ReadOnlySpan<byte> moovContent)
    {
        var pos = 0;
        while (pos + 8 <= moovContent.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(moovContent.Slice(pos, 4));
            var type = moovContent.Slice(pos + 4, 4);
            int headerLen;
            long boxLen;

            if (size == 1)
            {
                if (pos + 16 > moovContent.Length) return null;
                boxLen = (long)BinaryPrimitives.ReadUInt64BigEndian(moovContent.Slice(pos + 8, 8));
                headerLen = 16;
            }
            else if (size == 0)
            {
                boxLen = moovContent.Length - pos;
                headerLen = 8;
            }
            else if (size < 8)
            {
                return null;
            }
            else
            {
                boxLen = size;
                headerLen = 8;
            }

            if (type[0] == (byte)'m' && type[1] == (byte)'v' && type[2] == (byte)'h' && type[3] == (byte)'d')
            {
                var payloadAvailable = (int)Math.Min(boxLen - headerLen, moovContent.Length - pos - headerLen);
                if (payloadAvailable < 4) return null;
                return ParseMvhd(moovContent.Slice(pos + headerLen, payloadAvailable));
            }

            if (boxLen <= 0 || pos + boxLen > moovContent.Length) return null;
            pos += (int)boxLen;
        }
        return null;
    }

    private static async Task<TimeSpan?> ReadMoovDurationAsync(Stream stream, long moovOffset, long moovSize, CancellationToken ct)
    {
        var readSize = (int)Math.Min(moovSize, MoovReadCap);
        if (readSize < 16) return null;

        var buffer = new byte[readSize];
        stream.Position = moovOffset;
        if (!await TryReadExactAsync(stream, buffer.AsMemory(0, readSize), ct).ConfigureAwait(false))
            return null;

        return FindMvhdAndParse(buffer, 0, readSize);
    }

    private static TimeSpan? FindMvhdAndParse(byte[] buffer, int offset, int length)
        => FindMvhdAndParse(buffer.AsSpan(offset, length));

    private static TimeSpan? ParseMvhd(ReadOnlySpan<byte> payload)
    {
        // FullBox header: 1 byte version + 3 bytes flags.
        var version = payload[0];

        if (version == 0)
        {
            // creation_time(4) + modification_time(4) + timescale(4) + duration(4) = 16 bytes after FullBox header.
            if (payload.Length < 20) return null;
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
            var duration = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(16, 4));
            return ToTimeSpan(timescale, duration);
        }

        if (version == 1)
        {
            // creation_time(8) + modification_time(8) + timescale(4) + duration(8) = 28 bytes after FullBox header.
            if (payload.Length < 32) return null;
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(20, 4));
            var duration = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(24, 8));
            return ToTimeSpan(timescale, duration);
        }

        return null;
    }

    private static TimeSpan? ToTimeSpan(uint timescale, ulong duration)
    {
        if (timescale == 0 || duration == 0) return null;
        var seconds = (double)duration / timescale;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > TimeSpan.FromDays(1).TotalSeconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.Slice(total), ct).ConfigureAwait(false);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}
