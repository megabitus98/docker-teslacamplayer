using System.Threading;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class RefreshProgressService : IRefreshProgressService
{
    private readonly object _lock = new();
    private RefreshStatus _status = new();

    public void Start(int total)
    {
        lock (_lock)
        {
            _status = new RefreshStatus
            {
                IsRefreshing = true,
                Total = total,
                Processed = 0
            };
        }
    }

    public void Increment()
    {
        lock (_lock)
        {
            if (_status.IsRefreshing)
            {
                _status.Processed++;
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _status.IsRefreshing = false;
        }
    }

    public RefreshStatus GetStatus()
    {
        lock (_lock)
        {
            return new RefreshStatus
            {
                IsRefreshing = _status.IsRefreshing,
                Processed = _status.Processed,
                Total = _status.Total
            };
        }
    }
}

