using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class ExportCleanupService : BackgroundService
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public ExportCleanupService(ISettingsProvider settingsProvider)
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CleanupOnce();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Export cleanup failed");
            }

            try { await Task.Delay(_interval, stoppingToken); } catch { }
        }
    }

    private void CleanupOnce()
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
