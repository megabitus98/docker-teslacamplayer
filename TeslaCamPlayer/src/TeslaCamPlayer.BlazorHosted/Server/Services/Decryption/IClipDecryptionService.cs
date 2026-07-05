namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

public enum DecryptStatus
{
    /// <summary>Every cloud-keyed clip in the event is decrypted and cached.</summary>
    Success,

    /// <summary>The folder held no encrypted, cloud-decryptable clips.</summary>
    NothingToDecrypt,

    /// <summary>Some clips decrypted, but at least one failed (e.g. ownership error).</summary>
    PartialErrors
}

/// <summary>Result of decrypting one event folder: source clip path → decrypted cache path.</summary>
public sealed record DecryptEventResult(
    DecryptStatus Status,
    IReadOnlyDictionary<string, string> DecryptedBySource,
    IReadOnlyList<FekError> Errors);

public interface IClipDecryptionService
{
    /// <summary>
    /// Ensures every encrypted, cloud-decryptable clip in <paramref name="eventDir"/> is decrypted
    /// into the cache (mirroring the source tree), reusing already-cached copies. Concurrent calls
    /// for the same folder are coalesced. Throws <see cref="TeslaNotConnectedException"/> /
    /// <see cref="TeslaRefreshFailedException"/> when auth is unavailable.
    /// </summary>
    Task<DecryptEventResult> EnsureEventDecryptedAsync(string eventDir, CancellationToken cancellationToken = default);

    /// <summary>Maps a source clip path to its decrypted cache path (whether or not it exists yet).</summary>
    string GetCachePathFor(string sourcePath);

    /// <summary>True when the path is inside the decrypted cache directory (used to allow serving it).</summary>
    bool IsCachePath(string fullPath);
}
