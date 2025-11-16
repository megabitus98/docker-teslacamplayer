using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Components;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Client.Services;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using System.Reflection;
using System.Net.Http.Json;
using System.Collections.Generic;
using System.Threading;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index : ComponentBase, IAsyncDisposable
{
    private const int EventItemHeight = 60;

    [Inject]
    private HttpClient HttpClient { get; set; }

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private StatusHubClient StatusHubClient { get; set; }

    private Clip[] _clips;
    private Clip[] _filteredclips;
    private HashSet<DateTime> _eventDates;
    private MudDatePicker _datePicker;
    private bool _setDatePickerInitialDate;
    private ElementReference _eventsList;
    private System.Timers.Timer _scrollDebounceTimer;
    private System.Timers.Timer _refreshUiTimer;
    private DateTime _ignoreDatePicked;
    private Clip _activeClip;
    private ClipViewer _clipViewer;
    private bool _showFilter;
    private bool _filterChanged;
    private EventFilterValues _eventFilter = new();
    private TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues _cameraFilter = new();
    private RefreshStatus _refreshStatus = new();
    private bool _enableDelete = true;
    private bool _isExportMode;
    private string _exportFormat = "mp4";
    private string _exportResolution = "original"; // or "1280x720", "1920x1080"
    private string _exportQuality = "medium";
    private bool _exportIncludeTimestamp = true;
    private bool _exportIncludeLabels = true;
    private bool _exportIncludeLocation = true;
    private string _exportJobId;
    private ExportStatus _exportStatus;
    private bool _showExportPanel;
    private IDisposable _refreshStatusSubscription;
    private IDisposable _exportStatusSubscription;
    private readonly SemaphoreSlim _clipsReloadLock = new(1, 1);
    private int _lastRefreshProcessed = -1;
    private DateTime _lastClipsReloadUtc = DateTime.MinValue;
    private bool _seenActiveRefresh;
    private bool _refreshUiRenderPending;

    protected override async Task OnInitializedAsync()
    {
        _scrollDebounceTimer = new(100);
        _scrollDebounceTimer.Elapsed += ScrollDebounceTimerTick;

        // Throttle UI renders for frequent refresh updates
        _refreshUiTimer = new(200);
        _refreshUiTimer.Elapsed += RefreshUiTimerTick;
        _refreshUiTimer.AutoReset = true;
        _refreshUiTimer.Enabled = false;

        _refreshStatusSubscription ??= StatusHubClient.RegisterRefreshHandler(HandleRefreshStatusAsync);
        _exportStatusSubscription ??= StatusHubClient.RegisterExportHandler(HandleExportStatusAsync);

        await StatusHubClient.EnsureConnectedAsync();

        try
        {
            var config = await HttpClient.GetFromNewtonsoftJsonAsync<AppConfig>("Api/GetConfig");
            _enableDelete = config?.EnableDelete ?? true;
        }
        catch
        {
            // If config fetch fails, default to showing delete (backward compatibility)
            _enableDelete = true;
        }

        await RefreshEventsAsync(false);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_setDatePickerInitialDate && _filteredclips?.Any() == true && _datePicker != null)
        {
            _setDatePickerInitialDate = true;

            // Check if there's an eventPath query parameter
            var uri = new Uri(NavigationManager.Uri);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var eventPath = queryParams["eventPath"];

            Clip clipToOpen = null;

            // If eventPath is provided, try to find and open that event
            if (!string.IsNullOrWhiteSpace(eventPath))
            {
                clipToOpen = _clips?.FirstOrDefault(c => string.Equals(c.DirectoryPath, eventPath, StringComparison.OrdinalIgnoreCase));

                // Clear the query parameter from the URL after handling it
                NavigationManager.NavigateTo("/", replace: true);
            }

            // If no specific event found or no query param, use the latest clip
            if (clipToOpen == null)
            {
                clipToOpen = _filteredclips.MaxBy(c => c.EndDate)!;
            }

            await _datePicker.GoToDate(clipToOpen.EndDate);
            await SetActiveClip(clipToOpen);
        }
    }

    private async Task RefreshEventsAsync(bool refreshCache)
    {
        // Initialize with empty arrays to show the list immediately (even if empty)
        // rather than showing the loading screen
        _clips = Array.Empty<Clip>();
        _filteredclips = Array.Empty<Clip>();
        await Task.Delay(10);
        await InvokeAsync(StateHasChanged);

        _setDatePickerInitialDate = false;

        if (refreshCache)
        {
            _refreshStatus = new RefreshStatus { IsRefreshing = true };
            _lastRefreshProcessed = -1;
            _lastClipsReloadUtc = DateTime.MinValue;
            _seenActiveRefresh = false;
            await InvokeAsync(StateHasChanged);
        }

        _clips = await HttpClient.GetFromNewtonsoftJsonAsync<Clip[]>("Api/GetClips?refreshCache=" + refreshCache);

        FilterClips();
        await InvokeAsync(StateHasChanged);

        // If we didn't explicitly request a refresh but server may be indexing in the background (first run), start polling
        if (!refreshCache && (_clips == null || _clips.Length == 0))
        {
            _lastRefreshProcessed = -1;
            _lastClipsReloadUtc = DateTime.MinValue;
            _seenActiveRefresh = false;
        }
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
            var clips = await HttpClient.GetFromNewtonsoftJsonAsync<Clip[]>("Api/GetClips?refreshCache=false");
            await InvokeAsync(() =>
            {
                _clips = clips ?? Array.Empty<Clip>();
                FilterClips();
                StateHasChanged();
            });
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

    private async Task HandleExportStatusAsync(ExportStatus status)
    {
        if (status == null)
            return;

        if (!string.Equals(status.JobId, _exportJobId, StringComparison.OrdinalIgnoreCase))
            return;

        _exportStatus = status;
        await InvokeAsync(StateHasChanged);

        if (status.State is ExportState.Completed or ExportState.Failed or ExportState.Canceled)
        {
            await StatusHubClient.UnsubscribeFromExportAsync(status.JobId);
        }
    }

    private async Task StopExportMonitoringAsync(bool requestRender = true)
    {
        var jobId = _exportJobId;
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            try
            {
                await StatusHubClient.UnsubscribeFromExportAsync(jobId);
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        _exportJobId = null;
        _exportStatus = null;
        if (requestRender)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task OpenDeleteConfirmationDialog()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var dialog = DialogService.Show<ConfirmDeleteDialog>("Confirm Delete", options);
        var result = await dialog.Result;

        if (!result.Cancelled)
        {
            await DeleteEventAsync();
        }
    }

    private async Task ToggleExportMode()
    {
        _isExportMode = !_isExportMode;
        if (!_isExportMode)
        {
            await StopExportMonitoringAsync();
            _showExportPanel = false;
        }
    }

    private void ToggleExportPanel() => _showExportPanel = !_showExportPanel;
    private async Task OpenExportDialog()
    {
        if (!_isExportMode || _activeClip == null || IsExportRunning)
            return;

        var parameters = new DialogParameters
        {
            [nameof(ExportSettingsDialog.Format)] = _exportFormat,
            [nameof(ExportSettingsDialog.Resolution)] = _exportResolution,
            [nameof(ExportSettingsDialog.Quality)] = _exportQuality,
            [nameof(ExportSettingsDialog.IncludeTimestamp)] = _exportIncludeTimestamp,
            [nameof(ExportSettingsDialog.IncludeLabels)] = _exportIncludeLabels,
            [nameof(ExportSettingsDialog.IncludeLocation)] = _exportIncludeLocation,
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseButton = true,
            DisableBackdropClick = false,
            CloseOnEscapeKey = true
        };

        var dlg = DialogService.Show<ExportSettingsDialog>("Export Settings", parameters, options);
        var result = await dlg.Result;
        if (result.Cancelled || result.Data is not ExportSettingsDialog.Result res)
            return;

        _exportFormat = res.Format;
        _exportResolution = res.Resolution;
        _exportQuality = res.Quality;
        _exportIncludeTimestamp = res.IncludeTimestamp;
        _exportIncludeLabels = res.IncludeLabels;
        _exportIncludeLocation = res.IncludeLocation;

        await StartExport();
    }

    private bool IsExportRunning => !string.IsNullOrWhiteSpace(_exportJobId) && _exportStatus is { State: ExportState.Running or ExportState.Pending };

    private (int? w, int? h) ParseResolution()
    {
        if (_exportResolution == "original") return (null, null);
        var parts = _exportResolution.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            return (w, h);
        return (null, null);
    }

    private async Task StartExport()
    {
        if (_activeClip == null) return;

        var (startUtc, endUtc) = _clipViewer.GetSelectedInterval();
        if (endUtc <= startUtc)
            return;

        var (cams, cols) = _clipViewer.GetVisibleCamerasAndColumns();
        if (cams == null || cams.Count == 0)
            return;

        var (w, h) = ParseResolution();
        var clipPath = _activeClip.DirectoryPath;
        if (string.IsNullOrWhiteSpace(clipPath))
            return;

        var request = new ExportRequest
        {
            ClipDirectoryPath = clipPath,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            OrderedCameras = cams.ToList(),
            GridColumns = cols,
            Format = _exportFormat,
            Width = w,
            Height = h,
            Quality = _exportQuality,
            IncludeTimestamp = _exportIncludeTimestamp,
            IncludeCameraLabels = _exportIncludeLabels,
            IncludeLocationOverlay = _exportIncludeLocation
        };

        var resp = await HttpClient.PostAsJsonAsync("Api/StartExport", request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var jobId = body != null && body.TryGetValue("jobId", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(jobId))
            return;

        if (!string.IsNullOrWhiteSpace(_exportJobId) && !string.Equals(_exportJobId, jobId, StringComparison.OrdinalIgnoreCase))
        {
            await StatusHubClient.UnsubscribeFromExportAsync(_exportJobId);
        }

        _exportJobId = jobId;
        _exportStatus = new ExportStatus { JobId = jobId, State = ExportState.Pending };
        await StatusHubClient.SubscribeToExportAsync(jobId);
        await InvokeAsync(StateHasChanged);
        await ShowExportProgressAsync();
    }

    private async Task ShowExportProgressAsync()
    {
        if (string.IsNullOrWhiteSpace(_exportJobId))
            return;

        var parameters = new DialogParameters
        {
            [nameof(ExportProgressDialog.JobId)] = _exportJobId
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
            DisableBackdropClick = false,
            CloseOnEscapeKey = true
        };

        var dlg = DialogService.Show<ExportProgressDialog>("Export Progress", parameters, options);
        await dlg.Result; // wait until user closes
    }

    private async Task OpenExportHistoryDialog()
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Large,
            FullWidth = true,
            CloseButton = true,
            DisableBackdropClick = false,
            CloseOnEscapeKey = true
        };

        var dlg = DialogService.Show<ExportHistoryDialog>("Export History", options);
        var result = await dlg.Result;

        // Check if user clicked "Open Event" and returned an event path
        if (result != null && !result.Canceled && result.Data is string eventPath && !string.IsNullOrWhiteSpace(eventPath))
        {
            // Find the clip by DirectoryPath
            var clip = _clips?.FirstOrDefault(c => string.Equals(c.DirectoryPath, eventPath, StringComparison.OrdinalIgnoreCase));
            if (clip != null)
            {
                // Set the active clip and scroll to it
                await SetActiveClip(clip);
                await ScrollListToActiveClip();
            }
        }
    }

    private async Task DeleteEventAsync()
    {
        if (_activeClip != null && !string.IsNullOrEmpty(_activeClip.DirectoryPath))
        {
            var response = await HttpClient.DeleteAsync($"Api/DeleteEvent?path={Uri.EscapeDataString(_activeClip.DirectoryPath)}");
            if (response.IsSuccessStatusCode)
            {
                await RefreshEventsAsync(true);
            }
        }
    }

    private void FilterClips()
    {
        _filteredclips = (_clips ??= Array.Empty<Clip>())
            .Where(_eventFilter.IsInFilter)
            .ToArray();

        _eventDates = _filteredclips
            .Select(c => c.StartDate.Date)
            .Concat(_filteredclips.Select(c => c.EndDate.Date))
            .Distinct()
            .ToHashSet();
    }

    private async Task ToggleFilter()
    {
        _showFilter = !_showFilter;
        if (_showFilter || !_filterChanged)
            return;

        FilterClips();
        await InvokeAsync(StateHasChanged);
    }

    private void EventFilterValuesChanged(EventFilterValues values)
    {
        _eventFilter = values;
        _filterChanged = true;
    }

    private void CameraFilterValuesChanged(TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues values)
    {
        _cameraFilter = values;
        // No change to list; viewer parameters will update ClipViewer
    }

    private bool IsDateDisabledFunc(DateTime date)
        => _eventDates?.Contains(date) != true;

    private static string[] GetClipIcons(Clip clip)
    {
        // sentry_aware_object_detection
        // user_interaction_honk
        // user_interaction_dashcam_panel_save
        // user_interaction_dashcam_icon_tapped
        // sentry_aware_accel_0.532005

        var baseIcon = clip.Type switch
        {
            ClipType.Recent => Icons.Material.Filled.History,
            ClipType.Saved => Icons.Material.Filled.CameraAlt,
            ClipType.Sentry => Icons.Material.Filled.RadioButtonChecked,
            _ => Icons.Material.Filled.QuestionMark
        };

        if (clip.Type == ClipType.Recent || clip.Type == ClipType.Unknown || clip.Event == null)
            return new[] { baseIcon };

        var secondIcon = clip.Event.Reason switch
        {
            CamEvents.SentryAwareObjectDetection => Icons.Material.Filled.Animation,
            CamEvents.UserInteractionHonk => Icons.Material.Filled.Campaign,
            CamEvents.UserInteractionDashcamPanelSave => Icons.Material.Filled.Archive,
            CamEvents.UserInteractionDashcamIconTapped => Icons.Material.Filled.Archive,
            _ => null
        };

        if (clip.Event.Reason.StartsWith(CamEvents.SentryAwareAccelerationPrefix))
            secondIcon = Icons.Material.Filled.OpenWith;

        return secondIcon == null ? new[] { baseIcon } : new[] { baseIcon, secondIcon };
    }

    private class ScrollToOptions
    {
        public int? Left { get; set; }

        public int? Top { get; set; }

        public string Behavior { get; set; }
    }

    private async Task DatePicked(DateTime? pickedDate)
    {
        if (!pickedDate.HasValue || _ignoreDatePicked == pickedDate)
            return;

        var firstClipAtDate = _filteredclips.FirstOrDefault(c => c.StartDate.Date == pickedDate);
        if (firstClipAtDate == null)
            return;

        await SetActiveClip(firstClipAtDate);
        await ScrollListToActiveClip();
        await Task.Delay(500);
    }

    private async Task ScrollListToActiveClip()
    {
        var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
        var index = Array.IndexOf(_filteredclips, _activeClip);
        var top = (int)(index * EventItemHeight - listBoundingRect.Height / 2 + EventItemHeight / 2);

        await JsRuntime.InvokeVoidAsync("HTMLElement.prototype.scrollTo.call", _eventsList, new ScrollToOptions
        {
            Behavior = "smooth",
            Top = top
        });
    }

    private async Task SetActiveClip(Clip clip)
    {
        _activeClip = clip;
        await _clipViewer.SetClipAsync(_activeClip);
        _ignoreDatePicked = clip.StartDate.Date;
        _datePicker.Date = clip.StartDate.Date;
    }

    private void EventListScrolled()
    {
        if (!_scrollDebounceTimer.Enabled)
            _scrollDebounceTimer.Enabled = true;
    }

    private async void ScrollDebounceTimerTick(object _, ElapsedEventArgs __)
    {
        var scrollTop = await JsRuntime.InvokeAsync<double>("getProperty", _eventsList, "scrollTop");
        var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
        var centerScrollPosition = scrollTop + listBoundingRect.Height / 2 + EventItemHeight / 2;
        var itemIndex = (int)centerScrollPosition / EventItemHeight;
        var atClip = _filteredclips.ElementAt(Math.Min(_filteredclips.Length - 1, itemIndex));

        _ignoreDatePicked = atClip.StartDate.Date;
        await _datePicker.GoToDate(atClip.StartDate.Date);

        _scrollDebounceTimer.Enabled = false;
    }

    private async Task PreviousButtonClicked()
    {
        // Go to an OLDER clip, so start date should be GREATER than current
        var previous = _filteredclips
            .OrderByDescending(c => c.StartDate)
            .FirstOrDefault(c => c.StartDate < _activeClip.StartDate);

        if (previous != null)
        {
            await SetActiveClip(previous);
            await ScrollListToActiveClip();
        }
    }

    private async Task NextButtonClicked()
    {
        // Go to a NEWER clip, so start date should be LESS than current
        var next = _filteredclips
            .OrderBy(c => c.StartDate)
            .FirstOrDefault(c => c.StartDate > _activeClip.StartDate);

        if (next != null)
        {
            await SetActiveClip(next);
            await ScrollListToActiveClip();
        }
    }

    private async Task DatePickerOnMouseWheel(WheelEventArgs e)
    {
        if (e.DeltaY == 0 && e.DeltaX == 0 || !_datePicker.PickerMonth.HasValue)
            return;

        var goToNextMonth = e.DeltaY + e.DeltaX * -1 < 0;
        var targetDate = _datePicker.PickerMonth.Value.AddMonths(goToNextMonth ? 1 : -1);
        var endOfMonth = targetDate.AddMonths(1);

        var clipsInOrAfterTargetMonth = _filteredclips.Any(c => c.StartDate >= targetDate);
        var clipsInOrBeforeTargetMonth = _filteredclips.Any(c => c.StartDate <= endOfMonth);

        if (goToNextMonth && !clipsInOrAfterTargetMonth)
            return;

        if (!goToNextMonth && !clipsInOrBeforeTargetMonth)
            return;

        _ignoreDatePicked = targetDate;
        await _datePicker.GoToDate(targetDate);
    }
    public string AssemblyVersion
    {
        get
        {
            return Assembly.GetExecutingAssembly().GetName().Version.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _scrollDebounceTimer?.Dispose();
            _refreshUiTimer?.Dispose();
        }
        catch
        {
            // ignore disposal errors
        }

        _refreshStatusSubscription?.Dispose();
        _exportStatusSubscription?.Dispose();

        await StopExportMonitoringAsync(false);

        _clipsReloadLock.Dispose();
    }
}
