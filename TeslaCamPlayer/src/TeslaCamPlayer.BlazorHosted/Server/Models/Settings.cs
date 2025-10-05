namespace TeslaCamPlayer.BlazorHosted.Server.Models;

public class Settings
{
    public string ClipsRootPath { get; set; }
    public string CacheFilePath { get; set; }
    public string CacheDatabasePath { get; set; }
    public bool EnableDelete { get; set; } = true;
    public string ExportRootPath { get; set; }
    public int ExportRetentionHours { get; set; } = 24;
    public int IndexingBatchSize { get; set; } = 1000;
    public int IndexingMinBatchSize { get; set; } = 250;
    public double IndexingMaxMemoryUtilization { get; set; } = 0.85;
    public int IndexingMemoryRecoveryDelaySeconds { get; set; } = 5;
}
