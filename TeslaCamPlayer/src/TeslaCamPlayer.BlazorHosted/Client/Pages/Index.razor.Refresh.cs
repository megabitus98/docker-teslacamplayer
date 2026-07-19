using System;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index
{
    private RefreshStatus _refreshStatus = new();

    private int _lastRefreshProcessed = -1;
    private DateTime _lastClipsReloadUtc = DateTime.MinValue;
    private bool _seenActiveRefresh;
    private bool _refreshUiRenderPending;

    private async Task RefreshEventsAsync(bool refreshCache)
    {
        _setDatePickerInitialDate = false;
        _loadedClips.Clear();
        _activeClip = null;

        if (refreshCache)
        {
            _refreshStatus = new RefreshStatus { IsRefreshing = true };
            _lastRefreshProcessed = -1;
            _lastClipsReloadUtc = DateTime.MinValue;
            _seenActiveRefresh = false;

            // Trigger server-side refresh via old API (this starts background indexing)
            _ = HttpClient.GetFromNewtonsoftJsonAsync<Clip[]>("Api/GetClips?refreshCache=true");
        }

        // Reload available dates and refresh the virtualized list
        await LoadAvailableDatesAsync();

        if (_virtualizer != null)
        {
            await _virtualizer.RefreshDataAsync();
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRefreshStatusAsync(RefreshStatus status)
    {
        status ??= new RefreshStatus();

        var now = DateTime.UtcNow;
        var shouldReloadClips = false;

        if (status.IsRefreshing)
        {
            _seenActiveRefresh = true;
        }

        if (status.Processed != _lastRefreshProcessed)
        {
            if ((now - _lastClipsReloadUtc).TotalMilliseconds >= 500)
            {
                shouldReloadClips = true;
                _lastClipsReloadUtc = now;
            }

            _lastRefreshProcessed = status.Processed;
        }

        _refreshStatus = status;
        _refreshUiRenderPending = true;

        if (status.IsRefreshing)
        {
            // Ensure periodic UI updates while indexing
            if (_refreshUiTimer != null && !_refreshUiTimer.Enabled)
            {
                _refreshUiTimer.Enabled = true;
            }
        }

        if (shouldReloadClips)
        {
            _ = ReloadClipsAsync();
        }

        if (!status.IsRefreshing && _seenActiveRefresh && (status.Total > 0 || status.Processed > 0))
        {
            _seenActiveRefresh = false;
            _ = ReloadClipsAsync();
        }

        if (!status.IsRefreshing)
        {
            // Stop throttling and force a final render to show the last values immediately
            if (_refreshUiTimer != null)
            {
                _refreshUiTimer.Enabled = false;
            }
            await InvokeAsync(StateHasChanged);
        }
    }

    private async void RefreshUiTimerTick(object _, ElapsedEventArgs __)
    {
        if (!_refreshUiRenderPending)
        {
            return;
        }

        _refreshUiRenderPending = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ReloadClipsAsync()
    {
        if (!await _clipsReloadLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            // Reload available dates
            await LoadAvailableDatesAsync();

            // Refresh the virtualized list to pick up new data
            if (_virtualizer != null)
            {
                _loadedClips.Clear();
                await _virtualizer.RefreshDataAsync();
            }

            await InvokeAsync(StateHasChanged);
        }
        catch
        {
            // Transient failures are ignored; subsequent updates will resync
        }
        finally
        {
            _clipsReloadLock.Release();
        }
    }
}
