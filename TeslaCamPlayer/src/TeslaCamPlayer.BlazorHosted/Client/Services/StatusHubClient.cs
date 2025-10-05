using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Services;

public sealed class StatusHubClient : IAsyncDisposable
{
    private readonly NavigationManager _navigationManager;
    private readonly ILogger<StatusHubClient> _logger;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _handlerGate = new();
    private readonly object _subscriptionGate = new();
    private HubConnection _connection;
    private bool _disposed;

    private readonly List<Func<RefreshStatus, Task>> _refreshHandlers = new();
    private readonly List<Func<ExportStatus, Task>> _exportHandlers = new();
    private readonly Dictionary<string, int> _exportJobSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private int _allExportsSubscriptionCount;

    public StatusHubClient(NavigationManager navigationManager, ILogger<StatusHubClient> logger)
    {
        _navigationManager = navigationManager;
        _logger = logger;
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectionAsync(cancellationToken);
    }

    public IDisposable RegisterRefreshHandler(Func<RefreshStatus, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (_handlerGate)
        {
            _refreshHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_handlerGate)
            {
                _refreshHandlers.Remove(handler);
            }
        });
    }

    public IDisposable RegisterExportHandler(Func<ExportStatus, Task> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (_handlerGate)
        {
            _exportHandlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_handlerGate)
            {
                _exportHandlers.Remove(handler);
            }
        });
    }

    public async Task SubscribeToExportAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        jobId = jobId.Trim();

        bool shouldSubscribe;
        lock (_subscriptionGate)
        {
            _exportJobSubscriptions.TryGetValue(jobId, out var currentCount);
            currentCount++;
            _exportJobSubscriptions[jobId] = currentCount;
            shouldSubscribe = currentCount == 1;
        }

        var connection = await EnsureConnectionAsync(cancellationToken);
        if (shouldSubscribe && connection.State == HubConnectionState.Connected)
        {
            try
            {
                await connection.InvokeAsync("SubscribeToExport", jobId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to export job {JobId}", jobId);
            }
        }
    }

    public async Task UnsubscribeFromExportAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        jobId = jobId.Trim();

        bool shouldUnsubscribe = false;

        lock (_subscriptionGate)
        {
            if (_exportJobSubscriptions.TryGetValue(jobId, out var currentCount) && currentCount > 0)
            {
                currentCount--;
                if (currentCount == 0)
                {
                    _exportJobSubscriptions.Remove(jobId);
                    shouldUnsubscribe = true;
                }
                else
                {
                    _exportJobSubscriptions[jobId] = currentCount;
                }
            }
        }

        if (shouldUnsubscribe && _connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync("UnsubscribeFromExport", jobId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unsubscribe from export job {JobId}", jobId);
            }
        }
    }

    public async Task SubscribeToAllExportsAsync(CancellationToken cancellationToken = default)
    {
        bool shouldSubscribe;
        lock (_subscriptionGate)
        {
            _allExportsSubscriptionCount++;
            shouldSubscribe = _allExportsSubscriptionCount == 1;
        }

        var connection = await EnsureConnectionAsync(cancellationToken);
        if (shouldSubscribe && connection.State == HubConnectionState.Connected)
        {
            try
            {
                await connection.InvokeAsync("SubscribeToAllExports", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to all export updates");
            }
        }
    }

    public async Task UnsubscribeFromAllExportsAsync(CancellationToken cancellationToken = default)
    {
        bool shouldUnsubscribe = false;
        lock (_subscriptionGate)
        {
            if (_allExportsSubscriptionCount > 0)
            {
                _allExportsSubscriptionCount--;
                shouldUnsubscribe = _allExportsSubscriptionCount == 0;
            }
        }

        if (shouldUnsubscribe && _connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync("UnsubscribeFromAllExports", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unsubscribe from all export updates");
            }
        }
    }

    private async Task<HubConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StatusHubClient));
        }

        if (_connection?.State == HubConnectionState.Connected)
        {
            return _connection;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection == null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(_navigationManager.ToAbsoluteUri("/hubs/status"))
                    .WithAutomaticReconnect()
                    .Build();

                _connection.On<RefreshStatus>("RefreshStatusUpdated", async status =>
                {
                    _logger.LogTrace(
                        "Received refresh status. IsRefreshing={IsRefreshing}, Processed={Processed}, Total={Total}",
                        status?.IsRefreshing,
                        status?.Processed,
                        status?.Total);
                    await NotifyRefreshHandlersAsync(status ?? new RefreshStatus());
                });

                _connection.On<ExportStatus>("ExportStatusUpdated", async status =>
                {
                    _logger.LogTrace(
                        "Received export status. JobId={JobId}, State={State}, Percent={Percent:F2}",
                        status?.JobId,
                        status?.State,
                        status?.Percent);
                    if (status != null)
                    {
                        await NotifyExportHandlersAsync(status);
                    }
                });

                _connection.Closed += async error =>
                {
                    if (error != null)
                    {
                        _logger.LogWarning(error, "StatusHub connection closed unexpectedly.");
                    }
                    else
                    {
                        _logger.LogInformation("StatusHub connection closed.");
                    }

                    await Task.CompletedTask;
                };

                _connection.Reconnecting += error =>
                {
                    _logger.LogWarning(error, "StatusHub reconnecting...");
                    return Task.CompletedTask;
                };

                _connection.Reconnected += async connectionId =>
                {
                    _logger.LogInformation("StatusHub reconnected. ConnectionId={ConnectionId}", connectionId);
                    await ResubscribeAsync();
                };
            }

            if (_connection.State != HubConnectionState.Connected)
            {
                await _connection.StartAsync(cancellationToken);
                _logger.LogInformation("StatusHub connected. ConnectionId={ConnectionId}", _connection.ConnectionId);
                await ResubscribeAsync();
            }

            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ResubscribeAsync()
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        List<string> jobIds;
        int allSubscriptionCount;

        lock (_subscriptionGate)
        {
            jobIds = _exportJobSubscriptions.Keys.ToList();
            allSubscriptionCount = _allExportsSubscriptionCount;
        }

        foreach (var jobId in jobIds)
        {
            try
            {
                await _connection.InvokeAsync("SubscribeToExport", jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resubscribe to export job {JobId}", jobId);
            }
        }

        if (allSubscriptionCount > 0)
        {
            try
            {
                await _connection.InvokeAsync("SubscribeToAllExports");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resubscribe to all export updates");
            }
        }
    }

    private async Task NotifyRefreshHandlersAsync(RefreshStatus status)
    {
        List<Func<RefreshStatus, Task>> handlers;
        lock (_handlerGate)
        {
            handlers = _refreshHandlers.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh handler threw an exception.");
            }
        }
    }

    private async Task NotifyExportHandlersAsync(ExportStatus status)
    {
        List<Func<ExportStatus, Task>> handlers;
        lock (_handlerGate)
        {
            handlers = _exportHandlers.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export handler threw an exception.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _connectionGate.WaitAsync();
        try
        {
            if (_connection != null)
            {
                await _connection.StopAsync();
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _connectionGate.Release();
        }

        _connectionGate.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private bool _isDisposed;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _dispose();
        }
    }
}
