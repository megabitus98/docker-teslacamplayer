using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class ExportCleanupService : PeriodicFileCleanupService
{
    private readonly ISettingsProvider _settingsProvider;

    public ExportCleanupService(ISettingsProvider settingsProvider)
        : base(TimeSpan.FromHours(1), "Export cleanup failed")
    {
        _settingsProvider = settingsProvider;

        var retentionHours = _settingsProvider.Settings.ExportRetentionHours;
        if (retentionHours <= 0)
        {
            Log.Information("Export cleanup disabled; ExportRetentionHours is {Hours}.", retentionHours);
        }
        else
        {
            Log.Information("Export cleanup retention set to {Hours} hour(s).", retentionHours);
        }
    }

    protected override void CleanupOnce()
    {
        var settings = _settingsProvider.Settings;
        var exportDir = settings.ExportRootPath;
        if (string.IsNullOrWhiteSpace(exportDir)) return;
        if (!Directory.Exists(exportDir)) return;

        var retentionHours = settings.ExportRetentionHours;
        if (retentionHours <= 0) return;

        var now = DateTime.UtcNow;
        var keepFor = TimeSpan.FromHours(Math.Max(1, retentionHours));
        foreach (var file in Directory.EnumerateFiles(exportDir))
        {
            try
            {
                var info = new FileInfo(file);
                var age = now - info.CreationTimeUtc;
                if (age > keepFor)
                {
                    info.Delete();
                    Log.Information("Deleted old export: {Path}", file);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to delete export {Path}", file);
            }
        }
    }
}
