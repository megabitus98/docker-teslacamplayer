using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Linq;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer : ComponentBase, IDisposable
{
    [Inject]
    public IJSRuntime JsRuntime { get; set; }

    [Parameter]
    public EventCallback PreviousButtonClicked { get; set; }

    [Parameter]
    public EventCallback NextButtonClicked { get; set; }

    [Parameter]
    public CameraFilterValues CameraFilter { get; set; } = new();

    [Parameter]
    public bool IsExportMode { get; set; }

    protected override void OnInitialized()
    {
        _setVideoTimeDebounceTimer = new(500);
        _setVideoTimeDebounceTimer.Elapsed += ScrubVideoDebounceTick;
        _ = InitializeSeiParsingAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);
        foreach (var tile in _tiles)
        {
            if (tile.Player == null)
            {
                continue;
            }

            tile.Player.Loaded += () => { _videoLoadedEventCount++; };
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_clip == null || _currentSegment == null)
        {
            return;
        }

        if (_lastAppliedCameraFilter.ShowFront == CameraFilter.ShowFront &&
            _lastAppliedCameraFilter.ShowBack == CameraFilter.ShowBack &&
            _lastAppliedCameraFilter.ShowLeftRepeater == CameraFilter.ShowLeftRepeater &&
            _lastAppliedCameraFilter.ShowLeftPillar == CameraFilter.ShowLeftPillar &&
            _lastAppliedCameraFilter.ShowRightRepeater == CameraFilter.ShowRightRepeater &&
            _lastAppliedCameraFilter.ShowRightPillar == CameraFilter.ShowRightPillar)
        {
            return;
        }

        _lastAppliedCameraFilter = new CameraFilterValues
        {
            ShowFront = CameraFilter.ShowFront,
            ShowBack = CameraFilter.ShowBack,
            ShowLeftRepeater = CameraFilter.ShowLeftRepeater,
            ShowLeftPillar = CameraFilter.ShowLeftPillar,
            ShowRightRepeater = CameraFilter.ShowRightRepeater,
            ShowRightPillar = CameraFilter.ShowRightPillar
        };

        await InvokeAsync(StateHasChanged);
    }

    public async Task SetClipAsync(Clip clip)
    {
        _clip = clip;
        await EnsurePlayersReadyAsync();
        TimelineValue = 0;
        _timelineMaxSeconds = (clip.EndDate - clip.StartDate).TotalSeconds;
        _exportRange = (0, _timelineMaxSeconds);

        _currentSegment = _clip.Segments.First();
        await SetCurrentSegmentVideosAsync();
    }

    [JSInvokable]
    public Task ExitFullscreenFromJs()
        => ExitFullscreen();

    public void Dispose()
    {
        try { JsRuntime?.InvokeVoidAsync("unregisterEscHandler"); } catch { }
        try { _objRef?.Dispose(); } catch { }
        try { _seiHudRef?.DisposeAsync(); } catch { }
        try { _seiParserModule?.DisposeAsync(); } catch { }
    }

    private async void ScrubVideoDebounceTick(object _, ElapsedEventArgs __)
        => await ScrubToSliderTime();

    private static Task AwaitUiUpdate()
        => Task.CompletedTask;

    // Export helpers
    public (DateTime StartUtc, DateTime EndUtc) GetSelectedInterval()
    {
        var start = _clip.StartDate.AddSeconds(Math.Max(0, Math.Min(_exportRange.Start, _exportRange.End)));
        var end = _clip.StartDate.AddSeconds(Math.Min(_timelineMaxSeconds, Math.Max(_exportRange.Start, _exportRange.End)));
        return (start, end);
    }

    public (IReadOnlyList<Cameras> OrderedCameras, int Columns) GetVisibleCamerasAndColumns()
    {
        var cams = _tiles
            .Where(t => IsTileVisible(t.Tile))
            .Select(t => t.Tile switch
            {
                Tile.Front => Cameras.Front,
                Tile.Back => Cameras.Back,
                Tile.LeftRepeater => Cameras.LeftRepeater,
                Tile.RightRepeater => Cameras.RightRepeater,
                Tile.LeftPillar => Cameras.LeftBPillar,
                Tile.RightPillar => Cameras.RightBPillar,
                _ => Cameras.Unknown
            })
            .Where(c => c != Cameras.Unknown)
            .ToList();

        var visible = cams.Count;
        int cols = visible switch
        {
            >= 5 => 3,
            4 => 2,
            3 => 3,
            2 => 2,
            1 => 1,
            _ => 3
        };
        return (cams, cols);
    }

    private string ExportStartDisplay()
        => _clip == null ? string.Empty : _clip.StartDate.AddSeconds(_exportRange.Start).ToString("hh:mm:ss tt");

    private string ExportEndDisplay()
        => _clip == null ? string.Empty : _clip.StartDate.AddSeconds(_exportRange.End).ToString("hh:mm:ss tt");

    private string ExportDurationDisplay()
    {
        var dur = TimeSpan.FromSeconds(Math.Max(0, _exportRange.End - _exportRange.Start));
        return dur.ToString();
    }

    private Task OnExportStartChanged(double val)
    {
        var start = Math.Clamp(val, 0, _timelineMaxSeconds);
        var end = Math.Max(start, _exportRange.End);
        _exportRange = (start, end);
        return Task.CompletedTask;
    }

    private Task OnExportEndChanged(double val)
    {
        var end = Math.Clamp(val, 0, _timelineMaxSeconds);
        var start = Math.Min(_exportRange.Start, end);
        _exportRange = (start, end);
        return Task.CompletedTask;
    }

    private string ExportRangeHighlightStyle()
    {
        if (_timelineMaxSeconds <= 0) return "display:none;";

        var startPercent = (_exportRange.Start / _timelineMaxSeconds) * 100;
        var endPercent = (_exportRange.End / _timelineMaxSeconds) * 100;
        var width = endPercent - startPercent;

        return $"left: {startPercent:F2}%; width: {width:F2}%;";
    }

    private string ExportStartMarkerStyle()
    {
        if (_timelineMaxSeconds <= 0) return "display:none;";

        var percent = (_exportRange.Start / _timelineMaxSeconds) * 100;
        return $"left: {percent:F2}%;";
    }

    private string ExportEndMarkerStyle()
    {
        if (_timelineMaxSeconds <= 0) return "display:none;";

        var percent = (_exportRange.End / _timelineMaxSeconds) * 100;
        return $"left: {percent:F2}%;";
    }

    private void OnExportStartMarkerPointerDown(Microsoft.AspNetCore.Components.Web.PointerEventArgs e)
    {
        _draggingMarker = DragMarker.Start;
    }

    private void OnExportEndMarkerPointerDown(Microsoft.AspNetCore.Components.Web.PointerEventArgs e)
    {
        _draggingMarker = DragMarker.End;
    }

    private async Task OnSliderContainerPointerMove(Microsoft.AspNetCore.Components.Web.PointerEventArgs e)
    {
        if (_draggingMarker == DragMarker.None || _timelineMaxSeconds <= 0)
        {
            return;
        }

        // Get the bounding rect of the slider container using JS
        var rect = await JsRuntime.InvokeAsync<BoundingRect>("getElementBoundingRect", _sliderContainerRef);
        if (rect == null || rect.Width <= 0)
        {
            return;
        }

        // Calculate the percentage based on mouse position
        var relativeX = e.ClientX - rect.Left;
        var percent = Math.Clamp(relativeX / rect.Width, 0, 1);
        var newValue = percent * _timelineMaxSeconds;

        // Update the appropriate marker
        if (_draggingMarker == DragMarker.Start)
        {
            await OnExportStartChanged(newValue);
        }
        else if (_draggingMarker == DragMarker.End)
        {
            await OnExportEndChanged(newValue);
        }

        await InvokeAsync(StateHasChanged);
    }

    private Task OnSliderContainerPointerUp(Microsoft.AspNetCore.Components.Web.PointerEventArgs e)
    {
        _draggingMarker = DragMarker.None;
        return Task.CompletedTask;
    }

    private class BoundingRect
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
