namespace TeslaCamPlayer.BlazorHosted.Server.Models;

public class Settings
{
    public string ClipsRootPath { get; set; }
    public string CacheFilePath { get; set; }
    public bool EnableDelete { get; set; } = true;
    public string ExportRootPath { get; set; }
    public int ExportRetentionHours { get; set; } = 24;
}
