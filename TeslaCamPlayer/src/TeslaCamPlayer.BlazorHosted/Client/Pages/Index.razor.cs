using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
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
    [Inject]
    private HttpClient HttpClient { get; set; }

    [Inject]
    private IJSRuntime JsRuntime { get; set; }

    [Inject]
    private StatusHubClient StatusHubClient { get; set; }

    // Virtualize component reference for paginated loading
    private Virtualize<Clip> _virtualizer;

    // Cache of loaded clips for scroll position calculation and navigation
    private Dictionary<int, Clip> _loadedClips = new();

    private MudDatePicker _datePicker;
    private bool _setDatePickerInitialDate;

    private System.Timers.Timer _scrollDebounceTimer;
    private System.Timers.Timer _refreshUiTimer;
    private DateTime _ignoreDatePicked;
    private Clip _activeClip;

    private ClipViewer _clipViewer;
    private bool _showFilter;
    private bool _filterChanged;
    private EventFilterValues _eventFilter = new();
    private TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues _cameraFilter = new();

    private bool _enableDelete = true;
    private string _speedUnit = "kmh";
    private AppSettingsResponse _appSettings;
    private bool _pendingSetupDialog;

    private IDisposable _refreshStatusSubscription;
    private IDisposable _exportStatusSubscription;
    private readonly SemaphoreSlim _clipsReloadLock = new(1, 1);

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
