using Microsoft.Extensions.Hosting;
using Serilog;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

/// <summary>
/// Shared skeleton for periodic file-cleanup hosted services: run <see cref="CleanupOnce"/>,
/// log failures, sleep for the interval, repeat until shutdown.
/// </summary>
public abstract class PeriodicFileCleanupService : BackgroundService
{
    private readonly TimeSpan _interval;
    private readonly string _failureLogMessage;

    protected PeriodicFileCleanupService(TimeSpan interval, string failureLogMessage)
    {
        _interval = interval;
        _failureLogMessage = failureLogMessage;
    }

    protected abstract void CleanupOnce();

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
                Log.Warning(ex, _failureLogMessage);
            }

            try { await Task.Delay(_interval, stoppingToken); } catch { }
        }
    }
}
