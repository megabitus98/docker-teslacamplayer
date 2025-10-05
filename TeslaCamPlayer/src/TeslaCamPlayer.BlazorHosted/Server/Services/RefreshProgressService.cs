using System.Threading;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Hubs;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class RefreshProgressService : IRefreshProgressService
{
    private readonly object _lock = new();
    private RefreshStatus _status = new();
    private readonly IHubContext<StatusHub> _hubContext;

    public RefreshProgressService(IHubContext<StatusHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public void Start(int total)
    {
        RefreshStatus snapshot;
        lock (_lock)
        {
            _status = new RefreshStatus
            {
                IsRefreshing = true,
                Total = total,
                Processed = 0
            };
            snapshot = CloneStatusUnsafe();
        }

        BroadcastStatus(snapshot, "start");
    }

    public void Increment()
    {
        RefreshStatus snapshot = null;
        lock (_lock)
        {
            if (_status.IsRefreshing)
            {
                _status.Processed++;
                snapshot = CloneStatusUnsafe();
            }
        }

        if (snapshot != null)
        {
            BroadcastStatus(snapshot, "increment");
        }
    }

    public void Complete()
    {
        RefreshStatus snapshot;
        lock (_lock)
        {
            _status.IsRefreshing = false;
            snapshot = CloneStatusUnsafe();
        }

        BroadcastStatus(snapshot, "complete");
    }

    public RefreshStatus GetStatus()
    {
        lock (_lock)
        {
            return CloneStatusUnsafe();
        }
    }

    private RefreshStatus CloneStatusUnsafe()
        => new()
        {
            IsRefreshing = _status.IsRefreshing,
            Processed = _status.Processed,
            Total = _status.Total
        };

    private void BroadcastStatus(RefreshStatus status, string reason)
    {
        if (status == null)
        {
            return;
        }

        if (reason is "start" or "complete")
        {
            Log.Information(
                "Broadcasting refresh status {Reason}. Processed={Processed}, Total={Total}, IsRefreshing={IsRefreshing}",
                reason,
                status.Processed,
                status.Total,
                status.IsRefreshing);
        }
        else
        {
            Log.Debug(
                "Broadcasting refresh status update. Processed={Processed}, Total={Total}, IsRefreshing={IsRefreshing}",
                status.Processed,
                status.Total,
                status.IsRefreshing);
        }

        var sendTask = _hubContext.Clients.All.SendAsync("RefreshStatusUpdated", status);
        _ = sendTask.ContinueWith(
            t => Log.Error(t.Exception, "Failed to broadcast refresh status {Reason}.", reason),
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
