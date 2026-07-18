using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>
/// Keeps the decrypted-clip cache under the configured size cap by evicting the least-recently-used
/// files (by last access time). Decrypted clips are re-derivable, so eviction is safe.
/// </summary>
public sealed class DecryptedCacheCleanupService : PeriodicFileCleanupService
{
    private readonly ISettingsProvider _settingsProvider;

    public DecryptedCacheCleanupService(ISettingsProvider settingsProvider)
        : base(TimeSpan.FromMinutes(30), "Decrypted cache cleanup failed")
    {
        _settingsProvider = settingsProvider;
    }

    protected override void CleanupOnce()
    {
        var settings = _settingsProvider.Settings;
        var cacheDir = settings.DecryptedCachePath;
        if (string.IsNullOrWhiteSpace(cacheDir) || !Directory.Exists(cacheDir))
            return;

        var capBytes = (long)Math.Max(1, settings.DecryptedCacheMaxGb) * 1024L * 1024L * 1024L;

        var files = Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                try { return new FileInfo(path); }
                catch { return null; }
            })
            .Where(f => f is { Exists: true })
            .OrderBy(f => f!.LastAccessTimeUtc) // least-recently-used first
            .ToList();

        var total = files.Sum(f => f!.Length);
        if (total <= capBytes)
            return;

        Log.Information("Decrypted cache is {TotalMb:F0} MB, over the {CapMb:F0} MB cap; evicting oldest clips.",
            total / 1024d / 1024d, capBytes / 1024d / 1024d);

        foreach (var file in files)
        {
            if (total <= capBytes)
                break;

            try
            {
                var size = file!.Length;
                file.Delete();
                total -= size;
                Log.Debug("Evicted decrypted cache file {Path}", file.FullName);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to evict cache file {Path}", file!.FullName);
            }
        }
    }
}
