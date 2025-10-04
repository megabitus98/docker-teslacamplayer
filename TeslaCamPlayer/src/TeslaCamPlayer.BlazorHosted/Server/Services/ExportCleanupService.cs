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
    private readonly string _exportDir;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly Func<TimeSpan> _keepForProvider;

    public ExportCleanupService(ISettingsProvider settingsProvider)
    {
        _exportDir = settingsProvider.Settings.ExportRootPath;
        _keepForProvider = () => TimeSpan.FromHours(Math.Max(1, settingsProvider.Settings.ExportRetentionHours));
        try { Directory.CreateDirectory(_exportDir); } catch { }
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
        if (!Directory.Exists(_exportDir)) return;
        var now = DateTime.UtcNow;
        foreach (var file in Directory.EnumerateFiles(_exportDir))
        {
            try
            {
                var info = new FileInfo(file);
                var age = now - info.CreationTimeUtc;
                var keepFor = _keepForProvider();
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
