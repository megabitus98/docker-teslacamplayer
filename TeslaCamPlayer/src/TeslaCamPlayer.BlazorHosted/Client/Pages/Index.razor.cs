using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Components;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using System.Reflection;
using System.Net.Http.Json;
using System.Collections.Generic;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index : ComponentBase
{
    private const int EventItemHeight = 60;

    [Inject]
    private HttpClient HttpClient { get; set; }

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    private Clip[] _clips;
    private Clip[] _filteredclips;
    private HashSet<DateTime> _eventDates;
    private MudDatePicker _datePicker;
    private bool _setDatePickerInitialDate;
    private ElementReference _eventsList;
    private System.Timers.Timer _scrollDebounceTimer;
    private DateTime _ignoreDatePicked;
    private Clip _activeClip;
    private ClipViewer _clipViewer;
    private bool _showFilter;
    private bool _filterChanged;
    private EventFilterValues _eventFilter = new();
    private TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues _cameraFilter = new();
    private RefreshStatus _refreshStatus = new();
    private CancellationTokenSource _refreshStatusCts;
    private bool _enableDelete = true;
    private bool _isExportMode;
    private string _exportFormat = "mp4";
    private string _exportResolution = "original"; // or "1280x720", "1920x1080"
    private string _exportQuality = "medium";
    private bool _exportIncludeTimestamp = true;
    private bool _exportIncludeLabels = true;
    private string _exportJobId;
    private ExportStatus _exportStatus;
    private CancellationTokenSource _exportPollCts;
    private bool _showExportPanel;

    protected override async Task OnInitializedAsync()
    {
        _scrollDebounceTimer = new(100);
        _scrollDebounceTimer.Elapsed += ScrollDebounceTimerTick;

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
            var latestClip = _filteredclips.MaxBy(c => c.EndDate)!;
            await _datePicker.GoToDate(latestClip.EndDate);
            await SetActiveClip(latestClip);
        }
    }

    private async Task RefreshEventsAsync(bool refreshCache)
    {
        _filteredclips = null;
        _clips = null;
        await Task.Delay(10);
        await InvokeAsync(StateHasChanged);

        _setDatePickerInitialDate = false;
        if (refreshCache)
        {
            StartRefreshStatusPolling();
        }

        try
        {
            _clips = await HttpClient.GetFromNewtonsoftJsonAsync<Clip[]>("Api/GetClips?refreshCache=" + refreshCache);
        }
        finally
        {
            if (refreshCache)
            {
                StopRefreshStatusPolling();
            }
        }

        FilterClips();
    }

    private void StartRefreshStatusPolling()
    {
        StopRefreshStatusPolling();
        _refreshStatus = new();
        _refreshStatusCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_refreshStatusCts.IsCancellationRequested)
                {
                    var status = await HttpClient.GetFromNewtonsoftJsonAsync<RefreshStatus>("Api/GetRefreshStatus");
                    _refreshStatus = status ?? new RefreshStatus();
                    await InvokeAsync(StateHasChanged);
                    await Task.Delay(300, _refreshStatusCts.Token);
                }
            }
            catch
            {
                // ignore polling errors/cancellation
            }
        }, _refreshStatusCts.Token);
    }

    private void StopRefreshStatusPolling()
    {
        try
        {
            _refreshStatusCts?.Cancel();
        }
        catch { }
        finally
        {
            _refreshStatusCts?.Dispose();
            _refreshStatusCts = null;
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

    private void ToggleExportMode()
    {
        _isExportMode = !_isExportMode;
        if (!_isExportMode)
        {
            StopExportPolling();
            _exportJobId = null;
            _exportStatus = null;
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
        var request = new ExportRequest
        {
            ClipDirectoryPath = _activeClip.DirectoryPath,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            OrderedCameras = cams.ToList(),
            GridColumns = cols,
            Format = _exportFormat,
            Width = w,
            Height = h,
            Quality = _exportQuality,
            IncludeTimestamp = _exportIncludeTimestamp,
            IncludeCameraLabels = _exportIncludeLabels
        };

        var resp = await HttpClient.PostAsJsonAsync("Api/StartExport", request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        _exportJobId = body?["jobId"];
        _exportStatus = null;
        StartExportPolling();
        await ShowExportProgressAsync();
    }

    private void StartExportPolling()
    {
        StopExportPolling();
        if (string.IsNullOrWhiteSpace(_exportJobId)) return;
        _exportPollCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_exportPollCts.IsCancellationRequested && !string.IsNullOrWhiteSpace(_exportJobId))
                {
                    var st = await HttpClient.GetFromNewtonsoftJsonAsync<ExportStatus>($"Api/ExportStatus?jobId={Uri.EscapeDataString(_exportJobId)}");
                    _exportStatus = st;
                    await InvokeAsync(StateHasChanged);
                    if (st == null || st.State == ExportState.Completed || st.State == ExportState.Failed || st.State == ExportState.Canceled)
                        break;
                    await Task.Delay(500, _exportPollCts.Token);
                }
            }
            catch { }
        }, _exportPollCts.Token);
    }

    private void StopExportPolling()
    {
        try { _exportPollCts?.Cancel(); } catch { }
        try { _exportPollCts?.Dispose(); } catch { }
        _exportPollCts = null;
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
        await dlg.Result;
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
        => !_eventDates.Contains(date);

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
}
