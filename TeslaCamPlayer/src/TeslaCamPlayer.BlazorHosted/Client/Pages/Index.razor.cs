using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Components;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Client.Services;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index : ComponentBase, IAsyncDisposable
{
    private const int EventItemHeight = 82;

    [Inject]
    private HttpClient HttpClient { get; set; }

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private StatusHubClient StatusHubClient { get; set; }

    // Virtualize component reference for paginated loading
    private Virtualize<Clip> _virtualizer;
    private int _totalClipCount;
    private bool _isInitialLoading = true;

    // Cache of loaded clips for scroll position calculation and navigation
    private Dictionary<int, Clip> _loadedClips = new();

    private HashSet<DateTime> _eventDates = new();
    private MudDatePicker _datePicker;
    private bool _setDatePickerInitialDate;
    private ElementReference _eventsList;
    private System.Timers.Timer _scrollDebounceTimer;
    private System.Timers.Timer _refreshUiTimer;
    private DateTime _ignoreDatePicked;
    private Clip _activeClip;
    private bool _isDecrypting;
    private ClipViewer _clipViewer;
    private bool _showFilter;
    private bool _filterChanged;
    private EventFilterValues _eventFilter = new();
    private TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues _cameraFilter = new();
    private RefreshStatus _refreshStatus = new();
    private bool _enableDelete = true;
    private string _speedUnit = "kmh";
    private AppSettingsResponse _appSettings;
    private bool _pendingSetupDialog;
    private bool _isExportMode;
    private string _exportFormat = "mp4";
    private string _exportResolution = "original"; // or "1280x720", "1920x1080"
    private string _exportQuality = "medium";
    private bool _exportIncludeTimestamp = true;
    private bool _exportIncludeLabels = true;
    private bool _exportIncludeLocation = true;
    private bool _exportIncludeSeiHud = true;
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

        await LoadConfigAsync();
        await LoadAppSettingsAsync();
        _pendingSetupDialog = _appSettings?.NeedsSetup == true;

        await InitializeClipsAsync();
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var config = await HttpClient.GetFromNewtonsoftJsonAsync<AppConfig>("Api/GetConfig");
            _enableDelete = config?.EnableDelete ?? true;
            _speedUnit = config?.SpeedUnit ?? "kmh";
        }
        catch
        {
            // If config fetch fails, default to showing delete (backward compatibility)
            _enableDelete = true;
            _speedUnit = "kmh";
        }
    }

    private async Task LoadAppSettingsAsync()
    {
        try
        {
            _appSettings = await HttpClient.GetFromNewtonsoftJsonAsync<AppSettingsResponse>("Api/GetAppSettings");
        }
        catch
        {
            _appSettings = null;
        }
    }

    private async ValueTask<ItemsProviderResult<Clip>> LoadClipsAsync(ItemsProviderRequest request)
    {
        try
        {
            var types = GetSelectedClipTypes();
            var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
            var url = $"Api/GetClipsPaged?skip={request.StartIndex}&take={request.Count}";
            if (!string.IsNullOrEmpty(typesQuery))
            {
                url += "&" + typesQuery;
            }

            var response = await HttpClient.GetFromNewtonsoftJsonAsync<ClipPagedResponse>(url);

            if (response == null)
            {
                return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), 0);
            }

            _totalClipCount = response.TotalCount;

            // Cache loaded clips for scroll position calculation
            for (int i = 0; i < response.Items.Length; i++)
            {
                _loadedClips[request.StartIndex + i] = response.Items[i];
            }

            // Resync _activeClip to the new instance if it appears in this batch,
            // so that reference-equality code (scroll, navigation) stays correct after a refresh.
            if (_activeClip != null)
            {
                var refreshed = Array.Find(response.Items, c => c.DirectoryPath == _activeClip.DirectoryPath);
                if (refreshed != null)
                    _activeClip = refreshed;
            }

            // Set first clip as active if none selected
            if (_activeClip == null && response.Items.Length > 0 && request.StartIndex == 0)
            {
                await InvokeAsync(async () =>
                {
                    await SetActiveClip(response.Items[0]);
                });
            }

            return new ItemsProviderResult<Clip>(response.Items, response.TotalCount);
        }
        catch (OperationCanceledException)
        {
            return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), _totalClipCount);
        }
        catch
        {
            return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), _totalClipCount);
        }
    }

    private ClipType[] GetSelectedClipTypes()
    {
        var types = new List<ClipType>();

        if (_eventFilter.Recent)
            types.Add(ClipType.Recent);
        if (_eventFilter.DashcamHonk || _eventFilter.DashcamSaved || _eventFilter.DashcamOther)
            types.Add(ClipType.Saved);
        if (_eventFilter.SentryObjectDetection || _eventFilter.SentryAccelerationDetection || _eventFilter.SentryOther)
            types.Add(ClipType.Sentry);

        // If all or none are selected, return empty (no filter)
        if (types.Count == 0 || types.Count == 3)
            return Array.Empty<ClipType>();

        return types.ToArray();
    }

    private async Task InitializeClipsAsync()
    {
        _isInitialLoading = true;
        _setDatePickerInitialDate = false;
        _loadedClips.Clear();

        await LoadAvailableDatesAsync();

        _isInitialLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadAvailableDatesAsync()
    {
        try
        {
            var types = GetSelectedClipTypes();
            var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
            var url = "Api/GetAvailableDates";
            if (!string.IsNullOrEmpty(typesQuery))
            {
                url += "?" + typesQuery;
            }

            var dates = await HttpClient.GetFromNewtonsoftJsonAsync<DateTime[]>(url);
            _eventDates = dates?.ToHashSet() ?? new HashSet<DateTime>();
        }
        catch
        {
            _eventDates = new HashSet<DateTime>();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _pendingSetupDialog)
        {
            _pendingSetupDialog = false;
            await OpenSettingsDialogAsync(isRequiredSetup: true);
        }

        if (!_setDatePickerInitialDate && _activeClip != null && _datePicker != null)
        {
            _setDatePickerInitialDate = true;

            // Check if there's an eventPath query parameter
            var uri = new Uri(NavigationManager.Uri);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var eventPath = queryParams["eventPath"];

            // If eventPath is provided, try to find and open that event
            if (!string.IsNullOrWhiteSpace(eventPath))
            {
                var clipToOpen = _loadedClips.Values.FirstOrDefault(c => string.Equals(c.DirectoryPath, eventPath, StringComparison.OrdinalIgnoreCase));
                if (clipToOpen != null)
                {
                    await SetActiveClip(clipToOpen);
                }
                // Clear the query parameter from the URL after handling it
                NavigationManager.NavigateTo("/", replace: true);
            }

            if (_activeClip != null)
            {
                await _datePicker.GoToDate(_activeClip.EndDate);
            }
        }
    }

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
            [nameof(ExportSettingsDialog.IncludeSeiHud)] = _exportIncludeSeiHud,
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
        _exportIncludeSeiHud = res.IncludeSeiHud;

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
            IncludeLocationOverlay = _exportIncludeLocation,
            IncludeSeiHud = _exportIncludeSeiHud
        };

        var resp = await HttpClient.PostAsNewtonsoftJsonAsync("Api/StartExport", request);
        resp.EnsureSuccessStatusCode();
        var body = await resp.ReadFromNewtonsoftJsonAsync<Dictionary<string, string>>();
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

    private async Task OpenSettingsDialog()
        => await OpenSettingsDialogAsync(isRequiredSetup: false);

    private async Task OpenSettingsDialogAsync(bool isRequiredSetup)
    {
        await LoadAppSettingsAsync();
        if (_appSettings == null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            [nameof(SettingsDialog.Settings)] = _appSettings,
            [nameof(SettingsDialog.IsRequiredSetup)] = isRequiredSetup
        };

        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Small,
            FullWidth = true,
            CloseButton = !isRequiredSetup,
            DisableBackdropClick = isRequiredSetup,
            CloseOnEscapeKey = !isRequiredSetup
        };

        var dlg = DialogService.Show<SettingsDialog>("Settings", parameters, options);
        var result = await dlg.Result;
        if (result == null || result.Canceled || result.Data is not SaveAppSettingsResponse saveResult)
        {
            return;
        }

        _appSettings = saveResult.Settings;
        await LoadConfigAsync();

        if (saveResult.RequiresClipRefresh)
        {
            await RefreshEventsAsync(true);
        }
        else
        {
            await InvokeAsync(StateHasChanged);
        }
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
            // Find the clip by DirectoryPath in loaded clips
            var clip = _loadedClips.Values.FirstOrDefault(c => string.Equals(c.DirectoryPath, eventPath, StringComparison.OrdinalIgnoreCase));
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

    private async Task ToggleFilter()
    {
        _showFilter = !_showFilter;
        if (_showFilter || !_filterChanged)
            return;

        // When filter closes and has changed, refresh data with new filter
        _filterChanged = false;
        _loadedClips.Clear();
        _activeClip = null;

        await LoadAvailableDatesAsync();

        if (_virtualizer != null)
        {
            await _virtualizer.RefreshDataAsync();
        }

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
        // Handle null clip
        if (clip == null)
            return new[] { Icons.Material.Filled.QuestionMark };

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

        if (!string.IsNullOrEmpty(clip.Event.Reason) && clip.Event.Reason.StartsWith(CamEvents.SentryAwareAccelerationPrefix))
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

        // Find the first loaded clip at this date (lowest index = newest event on that date in DESC list)
        var firstClipAtDate = _loadedClips
            .Where(kvp => kvp.Value.StartDate.Date == pickedDate)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();
        if (firstClipAtDate != null)
        {
            await SetActiveClip(firstClipAtDate);
            await ScrollListToActiveClip();
        }
        else
        {
            // Clip not loaded yet — ask server for its index and scroll there
            try
            {
                var types = GetSelectedClipTypes();
                var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
                var url = $"Api/GetClipIndexByDate?date={pickedDate.Value:yyyy-MM-dd}";
                if (!string.IsNullOrEmpty(typesQuery))
                    url += "&" + typesQuery;

                var index = await HttpClient.GetFromNewtonsoftJsonAsync<int>(url);
                if (index >= 0 && index < _totalClipCount)
                {
                    var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
                    var top = (int)(index * EventItemHeight - listBoundingRect.Height / 2 + EventItemHeight / 2);
                    await JsRuntime.InvokeVoidAsync("HTMLElement.prototype.scrollTo.call", _eventsList,
                        new ScrollToOptions { Behavior = "smooth", Top = Math.Max(0, top) });
                }
            }
            catch
            {
                // If the API call fails, ignore — calendar already shows the date
            }
        }
    }

    private async Task ScrollListToActiveClip()
    {
        if (_activeClip == null)
            return;

        // Find index in loaded clips
        var activePath = _activeClip?.DirectoryPath;
        var index = _loadedClips.FirstOrDefault(kvp => kvp.Value.DirectoryPath == activePath).Key;
        if (index == 0 && _loadedClips.Count > 0 && _loadedClips.Values.First().DirectoryPath != activePath)
        {
            // Active clip not found in loaded clips, can't scroll to it
            return;
        }

        var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
        var top = (int)(index * EventItemHeight - listBoundingRect.Height / 2 + EventItemHeight / 2);

        await JsRuntime.InvokeVoidAsync("HTMLElement.prototype.scrollTo.call", _eventsList, new ScrollToOptions
        {
            Behavior = "smooth",
            Top = top
        });
    }

    private async Task SetActiveClip(Clip clip)
    {
        if (clip is { IsEncrypted: true })
        {
            var prepared = await PrepareEncryptedClipAsync(clip);
            if (prepared == null)
                return; // needs connect / failed — keep current selection

            clip = prepared;
        }

        _activeClip = clip;
        await _clipViewer.SetClipAsync(_activeClip);
        _ignoreDatePicked = clip.StartDate.Date;
        _datePicker.Date = clip.StartDate.Date;
    }

    private async Task<Clip> PrepareEncryptedClipAsync(Clip clip)
    {
        if (string.IsNullOrWhiteSpace(clip.DirectoryPath))
            return null;

        var status = await GetTeslaStatusAsync();
        if (status is not { Connected: true })
        {
            var openSettings = await DialogService.ShowMessageBox(
                "Encrypted clips",
                status is { HasToken: true }
                    ? "Your Tesla session has expired. Reconnect your account in Settings to decrypt these clips."
                    : "These clips are encrypted. Connect your Tesla account in Settings to unlock them.",
                yesText: "Open Settings", cancelText: "Not now");

            if (openSettings == true)
                await OpenSettingsDialog();

            return null;
        }

        _isDecrypting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var response = await HttpClient.PostAsync(
                $"Api/PrepareEvent?path={Uri.EscapeDataString(clip.DirectoryPath)}", null);
            var result = await response.ReadFromNewtonsoftJsonAsync<PrepareEventResponse>();

            if (result is { Success: true, Clip: not null })
            {
                ReplaceLoadedClip(result.Clip);
                return result.Clip;
            }

            var message = result?.ErrorCode switch
            {
                "NotConnected" => "Connect your Tesla account in Settings to decrypt these clips.",
                "RefreshFailed" => "Your Tesla session has expired. Reconnect your account in Settings.",
                _ => "Could not decrypt this event. Its clips may only be readable inside the vehicle."
            };
            await DialogService.ShowMessageBox("Decryption failed", message);
            return null;
        }
        catch
        {
            await DialogService.ShowMessageBox("Decryption failed", "Something went wrong while decrypting this event.");
            return null;
        }
        finally
        {
            _isDecrypting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<TeslaConnectionStatus> GetTeslaStatusAsync()
    {
        try
        {
            return await HttpClient.GetFromNewtonsoftJsonAsync<TeslaConnectionStatus>("Api/TeslaStatus");
        }
        catch
        {
            return null;
        }
    }

    // Swap the freshly decrypted clip into the loaded list so its lock badge clears without a full reload.
    private void ReplaceLoadedClip(Clip decrypted)
    {
        if (decrypted?.DirectoryPath == null)
            return;

        foreach (var key in _loadedClips.Keys.ToList())
        {
            if (string.Equals(_loadedClips[key].DirectoryPath, decrypted.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                _loadedClips[key] = decrypted;
        }
    }

    private void EventListScrolled()
    {
        if (!_scrollDebounceTimer.Enabled)
            _scrollDebounceTimer.Enabled = true;
    }

    private async void ScrollDebounceTimerTick(object _, ElapsedEventArgs __)
    {
        try
        {
            var scrollTop = await JsRuntime.InvokeAsync<double>("getProperty", _eventsList, "scrollTop");
            var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
            var centerScrollPosition = scrollTop + listBoundingRect.Height / 2;
            var itemIndex = (int)(centerScrollPosition / EventItemHeight);

            // Try to find the clip at this index from loaded clips
            if (_loadedClips.TryGetValue(Math.Min(_totalClipCount - 1, itemIndex), out var atClip))
            {
                _ignoreDatePicked = atClip.StartDate.Date;
                await _datePicker.GoToDate(atClip.StartDate.Date);
            }
        }
        catch
        {
            // Ignore errors during debounce
        }
        finally
        {
            _scrollDebounceTimer.Enabled = false;
        }
    }

    private async Task PreviousButtonClicked()
    {
        if (_activeClip == null)
            return;

        // Find current index and go to next one (older = higher index since sorted by date desc)
        var activePathPrev = _activeClip?.DirectoryPath;
        var currentIndex = _loadedClips.FirstOrDefault(kvp => kvp.Value.DirectoryPath == activePathPrev).Key;
        var nextIndex = currentIndex + 1;

        if (_loadedClips.TryGetValue(nextIndex, out var previous))
        {
            await SetActiveClip(previous);
            await ScrollListToActiveClip();
        }
    }

    private async Task NextButtonClicked()
    {
        if (_activeClip == null)
            return;

        // Find current index and go to previous one (newer = lower index since sorted by date desc)
        var activePathNext = _activeClip?.DirectoryPath;
        var currentIndex = _loadedClips.FirstOrDefault(kvp => kvp.Value.DirectoryPath == activePathNext).Key;
        var prevIndex = currentIndex - 1;

        if (prevIndex >= 0 && _loadedClips.TryGetValue(prevIndex, out var next))
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

        // Check if there are dates in the target month range using available dates
        var hasClipsInOrAfterTargetMonth = _eventDates.Any(d => d >= targetDate);
        var hasClipsInOrBeforeTargetMonth = _eventDates.Any(d => d <= endOfMonth);

        if (goToNextMonth && !hasClipsInOrAfterTargetMonth)
            return;

        if (!goToNextMonth && !hasClipsInOrBeforeTargetMonth)
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
