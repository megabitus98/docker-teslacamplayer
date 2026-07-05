using System.Collections.Concurrent;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>
/// Decrypts encrypted TeslaCam clips on demand into a plaintext cache that mirrors the source tree,
/// so the rest of the app (ffprobe, serving, export) works on ordinary MP4s. Fetches FEKs in one
/// batch per event and decrypts locally.
/// </summary>
public sealed class ClipDecryptionService : IClipDecryptionService
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly ITeslaKeyService _keyService;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _eventLocks = new(StringComparer.OrdinalIgnoreCase);

    public ClipDecryptionService(ISettingsProvider settingsProvider, ITeslaKeyService keyService)
    {
        _settingsProvider = settingsProvider;
        _keyService = keyService;
    }

    private string CacheRoot => Path.GetFullPath(_settingsProvider.Settings.DecryptedCachePath);
    private string ClipsRoot => Path.GetFullPath(_settingsProvider.Settings.ClipsRootPath);

    public bool IsCachePath(string fullPath)
    {
        var cacheRoot = CacheRoot;
        var normalized = Path.GetFullPath(fullPath);
        return normalized.StartsWith(EnsureTrailingSeparator(cacheRoot), StringComparison.OrdinalIgnoreCase);
    }

    public string GetCachePathFor(string sourcePath)
    {
        var root = ClipsRoot;
        var full = Path.GetFullPath(sourcePath);
        var rel = Path.GetRelativePath(root, full);
        return Path.GetFullPath(Path.Combine(CacheRoot, rel));
    }

    public async Task<DecryptEventResult> EnsureEventDecryptedAsync(string eventDir, CancellationToken cancellationToken = default)
    {
        var decryptedBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<FekError>();

        if (!Directory.Exists(eventDir))
            return new DecryptEventResult(DecryptStatus.NothingToDecrypt, decryptedBySource, errors);

        // Parse headers once; classify each cloud-keyed encrypted clip as cached or needs-decrypt.
        var pending = new List<(string Source, EncryptedHeader Header, string Cache)>();
        foreach (var file in Directory.EnumerateFiles(eventDir, "*.mp4"))
        {
            if (!EcryptfsDecryptor.IsEncryptedFile(file))
                continue;

            EncryptedHeader header;
            try
            {
                header = EcryptfsDecryptor.ReadHeader(file);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to parse encrypted header for {Path}", file);
                continue;
            }

            if (!header.HasCloudKey)
                continue; // event.json/thumb.png-style console-only files can't be decrypted off-vehicle

            var cachePath = GetCachePathFor(file);
            if (IsCachedValid(cachePath, header.PlaintextSize))
            {
                TouchAccessTime(cachePath);
                decryptedBySource[file] = cachePath;
            }
            else
            {
                pending.Add((file, header, cachePath));
            }
        }

        if (pending.Count == 0)
        {
            return new DecryptEventResult(
                decryptedBySource.Count > 0 ? DecryptStatus.Success : DecryptStatus.NothingToDecrypt,
                decryptedBySource,
                errors);
        }

        var gate = _eventLocks.GetOrAdd(Path.GetFullPath(eventDir), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the lock: a concurrent request may have decrypted these already.
            var toDecrypt = pending.Where(p => !IsCachedValid(p.Cache, p.Header.PlaintextSize)).ToList();
            foreach (var already in pending.Where(p => IsCachedValid(p.Cache, p.Header.PlaintextSize)))
            {
                TouchAccessTime(already.Cache);
                decryptedBySource[already.Source] = already.Cache;
            }

            if (toDecrypt.Count > 0)
            {
                var requests = toDecrypt.Select(p => new FekRequest(p.Source, p.Header)).ToList();
                var batch = await _keyService.FetchFeksAsync(requests, cancellationToken);
                errors.AddRange(batch.Errors);

                foreach (var (source, header, cache) in toDecrypt)
                {
                    if (!batch.Keys.TryGetValue(source, out var fek))
                    {
                        if (batch.Errors.All(e => e.Id != source))
                            errors.Add(new FekError(source, "No key returned."));
                        continue;
                    }

                    try
                    {
                        DecryptToCache(source, fek, cache);
                        decryptedBySource[source] = cache;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to decrypt {Source}", source);
                        errors.Add(new FekError(source, ex.Message));
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }

        var status = errors.Count == 0 ? DecryptStatus.Success : DecryptStatus.PartialErrors;
        return new DecryptEventResult(status, decryptedBySource, errors);
    }

    private static void DecryptToCache(string source, byte[] fek, string cachePath)
    {
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            EcryptfsDecryptor.DecryptFile(source, fek, tmp);
            File.Move(tmp, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                TryDelete(tmp);
        }
    }

    private static bool IsCachedValid(string cachePath, ulong plaintextSize)
    {
        try
        {
            var info = new FileInfo(cachePath);
            return info.Exists && info.Length == (long)plaintextSize;
        }
        catch
        {
            return false;
        }
    }

    private static void TouchAccessTime(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // best-effort LRU hint; ignore failures
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
