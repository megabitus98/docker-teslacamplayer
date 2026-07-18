namespace TeslaCamPlayer.BlazorHosted.Server.Models;

public class Settings
{
    public string ClipsRootPath { get; set; }
    public string CacheFilePath { get; set; }
    public string CacheDatabasePath { get; set; }
    public bool EnableDelete { get; set; } = true;
    public string SpeedUnit { get; set; } = "kmh";
    public string ExportRootPath { get; set; }
    public int ExportRetentionHours { get; set; } = 24;
    public int IndexingBatchSize { get; set; } = 1000;
    public int IndexingMinBatchSize { get; set; } = 250;
    public double IndexingMaxMemoryUtilization { get; set; } = 0.85;
    public int IndexingMemoryRecoveryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Tesla OAuth refresh token (client_id=dashcam, scope includes offline_access) used to
    /// decrypt encrypted TeslaCam clips. Secret: masked in the WebUI, never logged. Rotates
    /// on every refresh — the auth service persists the new value back here.
    /// </summary>
    public string TeslaRefreshToken { get; set; }

    /// <summary>
    /// Optional pre-obtained Tesla access token (bearer). Used as-is until it expires (~8h) with no
    /// auto-refresh — a quick-start alternative to the refresh token. Secret: masked in the WebUI.
    /// </summary>
    public string TeslaAccessToken { get; set; }

    /// <summary>Folder where decrypted copies of encrypted clips are cached (mirrors the source tree).</summary>
    public string DecryptedCachePath { get; set; }

    /// <summary>Maximum size of the decrypted-clip cache in gigabytes before LRU eviction kicks in.</summary>
    public int DecryptedCacheMaxGb { get; set; } = 10;

    /// <summary>
    /// Shallow copy. Exact because every property is a value type or string — if you add a
    /// mutable reference-type property (array/list/object), this must deep-copy it.
    /// </summary>
    public Settings Clone() => (Settings)MemberwiseClone();
}
