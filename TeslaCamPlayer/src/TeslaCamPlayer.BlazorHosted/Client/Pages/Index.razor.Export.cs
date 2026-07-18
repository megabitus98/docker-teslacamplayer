using System;
using MudBlazor;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using System.Collections.Generic;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index
{
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
}
