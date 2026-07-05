using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>
/// The account ("cloud") wrapped-key block and plaintext size parsed from an
/// encrypted TeslaCam file's 8192-byte header. Ported from tesla-dashcam-decrypt
/// (tesla_decrypt.py / EcryptfsFile). See docs/superpowers/specs for the format.
/// </summary>
public sealed class EncryptedHeader
{
    public ulong PlaintextSize { get; init; }
    public uint KeyId { get; init; }
    public byte[] PublicKey { get; init; } = Array.Empty<byte>(); // 65 bytes, uncompressed P-256 (0x04||X||Y)
    public string Vin { get; init; } = string.Empty;
    public ulong Timestamp { get; init; }
    public byte[] WrappedKey { get; init; } = Array.Empty<byte>(); // 44 bytes = GCM nonce(12)||ct(16)||tag(16)

    /// <summary>
    /// True when the file has an account-wrapped key the dashcam API can unwrap.
    /// event.json / thumb.png have a zeroed block (in-car "_CONSOLE" recipient only)
    /// and cannot be decrypted off-vehicle.
    /// </summary>
    public bool HasCloudKey => PublicKey.Length == 65 && PublicKey[0] == 0x04;
}

/// <summary>
/// Stateless parser + decryptor for Tesla's eCryptfs-derived EncryptedClips format
/// (firmware 2026.20+). Pure in-box crypto (MD5 + AES-128-CBC); no network, no deps.
/// The 16-byte file-encryption key (FEK) must be obtained separately from
/// dashcam.tesla.com/api/1/decrypt/batch (see <see cref="TeslaKeyService"/>).
/// </summary>
public static class EcryptfsDecryptor
{
    public const int PageSize = 4096;
    public const int HeaderSize = 8192;
    public const int DetectBytes = 16;
    private const uint Magic = 0x3C81B7F5; // stored XOR-split across bytes [8:12] and [12:16]

    /// <summary>O(1) check: XOR the two 32-bit words at offsets 8 and 12.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> head)
    {
        if (head.Length < DetectBytes)
            return false;

        var m1 = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(8, 4));
        var m2 = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(12, 4));
        return (m1 ^ m2) == Magic;
    }

    /// <summary>Reads only the first 16 bytes of a file to decide if it's encrypted.</summary>
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[DetectBytes];
            var read = fs.ReadAtLeast(head, DetectBytes, throwOnEndOfStream: false);
            return read >= DetectBytes && IsEncrypted(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static EncryptedHeader ParseHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < HeaderSize)
            throw new InvalidDataException("Buffer too small to contain an eCryptfs header.");

        var plaintextSize = BinaryPrimitives.ReadUInt64BigEndian(h.Slice(0, 8));
        var m1 = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(8, 4));
        var m2 = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(12, 4));
        if ((m1 ^ m2) != Magic)
            throw new InvalidDataException("Not a Tesla-encrypted file (bad magic).");

        var cur = PageSize;
        var keyId = BinaryPrimitives.ReadUInt32BigEndian(h.Slice(cur, 4)); cur += 4;
        var publicKey = h.Slice(cur, 65).ToArray(); cur += 65;
        var vin = Encoding.Latin1.GetString(h.Slice(cur, 17)).TrimEnd('\0'); cur += 17;
        var timestamp = BinaryPrimitives.ReadUInt64BigEndian(h.Slice(cur, 8)); cur += 8;
        var wrappedKey = h.Slice(cur, 44).ToArray();

        return new EncryptedHeader
        {
            PlaintextSize = plaintextSize,
            KeyId = keyId,
            PublicKey = publicKey,
            Vin = vin,
            Timestamp = timestamp,
            WrappedKey = wrappedKey
        };
    }

    public static EncryptedHeader ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[HeaderSize];
        fs.ReadExactly(buf, 0, HeaderSize);
        return ParseHeader(buf);
    }

    /// <summary>Per-page IV: MD5( MD5(fek)(16) ‖ ascii(decimal(page)), zero-padded to 32 ).</summary>
    private static byte[] DerivePageIv(byte[] rootIv, int page)
    {
        Span<byte> buf = stackalloc byte[32];
        buf.Clear(); // ascii digits fill from offset 16; the remainder must stay zero
        rootIv.CopyTo(buf);
        var digits = Encoding.ASCII.GetBytes(page.ToString(CultureInfo.InvariantCulture));
        digits.CopyTo(buf.Slice(16));
        return MD5.HashData(buf);
    }

    /// <summary>
    /// Streams <paramref name="srcPath"/> through page-by-page AES-128-CBC decryption
    /// into <paramref name="dstPath"/>, truncating to the header's plaintext size.
    /// Returns bytes written. The body never leaves the machine.
    /// </summary>
    public static long DecryptFile(string srcPath, byte[] fek, string dstPath)
    {
        if (fek.Length != 16)
            throw new ArgumentException($"FEK must be 16 bytes, got {fek.Length}.", nameof(fek));

        using var src = File.OpenRead(srcPath);
        var headerBuf = new byte[HeaderSize];
        src.ReadExactly(headerBuf, 0, HeaderSize);
        var header = ParseHeader(headerBuf);

        var parent = Path.GetDirectoryName(dstPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using var dst = File.Create(dstPath);
        using var aes = Aes.Create();
        aes.Key = fek;

        var rootIv = MD5.HashData(fek);
        var page = new byte[PageSize];
        long written = 0;
        var pageIndex = 0;

        while (true)
        {
            var read = src.ReadAtLeast(page, PageSize, throwOnEndOfStream: false);
            if (read < PageSize)
                break; // clean EOF, or a trailing partial page (not expected on valid files)

            var iv = DerivePageIv(rootIv, pageIndex);
            var plain = aes.DecryptCbc(page, iv, PaddingMode.None);

            var remaining = (long)header.PlaintextSize - written;
            var take = remaining < plain.Length ? (int)remaining : plain.Length;
            if (take > 0)
            {
                dst.Write(plain, 0, take);
                written += take;
            }

            pageIndex++;
            if (written >= (long)header.PlaintextSize)
                break;
        }

        return written;
    }
}
