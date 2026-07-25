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
    public string SpeedUnit { get; set; } = "kmh";

    [Parameter]
    public string TimeFormat { get; set; } = TimeFormats.DefaultTimeFormat;

    [Parameter]
    public string DateFormat { get; set; } = TimeFormats.DefaultDateFormat;

    [Parameter]
    public bool IsExportMode { get; set; }

    protected override void OnInitialized()
    {
        _setVideoTimeDebounceTimer = new(500);
        _setVideoTimeDebounceTimer.Elapsed += ScrubVideoDebounceTick;
        _seiInitTask = InitializeSeiParsingAsync();
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

        if (_lastAppliedCameraFilter == CameraFilter)
        {
            return;
        }

        // `with { }` copies: CameraFilter is mutated in place, so aliasing it would freeze the check.
        _lastAppliedCameraFilter = CameraFilter with { };

        await InvokeAsync(StateHasChanged);
    }

    private Tile? _triggerTile;

    public async Task SetClipAsync(Clip clip)
    {
        _clip = clip;
        _triggerTile = clip?.Event.TriggerTileCamera() switch
        {
            Cameras.Front => Tile.Front,
            Cameras.Back => Tile.Back,
            Cameras.LeftRepeater => Tile.LeftRepeater,
            Cameras.RightRepeater => Tile.RightRepeater,
            Cameras.LeftBPillar => Tile.LeftPillar,
            Cameras.RightBPillar => Tile.RightPillar,
            _ => null
        };
        await EnsurePlayersReadyAsync();
        TimelineValue = 0;
        _timelineMaxSeconds = (clip.EndDate - clip.StartDate).TotalSeconds;
        _exportRange = (0, _timelineMaxSeconds);
        _exportIntervals.Clear();

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

    /// <summary>
    /// The committed intervals, or the live marker range when none have been added — so the
    /// single-range flow keeps working without the user having to press "Add".
    /// </summary>
    public IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> GetSelectedIntervals()
    {
        var ranges = _exportIntervals.Count > 0 ? _exportIntervals : new List<(double, double)> { _exportRange };
        return ranges.Select(ToClipTime).ToList();
    }

    private (DateTime StartUtc, DateTime EndUtc) ToClipTime((double Start, double End) range)
    {
        var start = _clip.StartDate.AddSeconds(Math.Max(0, Math.Min(range.Start, range.End)));
        var end = _clip.StartDate.AddSeconds(Math.Min(_timelineMaxSeconds, Math.Max(range.Start, range.End)));
        return (start, end);
    }

    private void AddCurrentInterval()
    {
        var start = Math.Max(0, Math.Min(_exportRange.Start, _exportRange.End));
        var end = Math.Min(_timelineMaxSeconds, Math.Max(_exportRange.Start, _exportRange.End));
        if (end - start <= 0.01)
            return;

        _exportIntervals.Add((start, end));
        MergeIntervals();
    }

    private void RemoveInterval(int index)
    {
        if (index >= 0 && index < _exportIntervals.Count)
            _exportIntervals.RemoveAt(index);
    }

    /// <summary>Sorts and merges overlapping or touching intervals so the bands and the total read true.</summary>
    private void MergeIntervals()
    {
        var sorted = _exportIntervals.OrderBy(i => i.Start).ToList();
        _exportIntervals.Clear();

        foreach (var interval in sorted)
        {
            if (_exportIntervals.Count > 0 && interval.Start <= _exportIntervals[^1].End)
            {
                var last = _exportIntervals[^1];
                _exportIntervals[^1] = (last.Start, Math.Max(last.End, interval.End));
                continue;
            }

            _exportIntervals.Add(interval);
        }
    }

    private string IntervalLabel((double Start, double End) interval)
    {
        var (start, end) = ToClipTime(interval);
        var pattern = TimeFormats.TimePattern(TimeFormat);
        return $"{start.ToString(pattern)} → {end.ToString(pattern)}";
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
        => _clip == null ? string.Empty : _clip.StartDate.AddSeconds(_exportRange.Start).ToString(TimeFormats.TimePattern(TimeFormat));

    private string ExportEndDisplay()
        => _clip == null ? string.Empty : _clip.StartDate.AddSeconds(_exportRange.End).ToString(TimeFormats.TimePattern(TimeFormat));

    private string ExportDurationDisplay()
    {
        var seconds = _exportIntervals.Count > 0
            ? _exportIntervals.Sum(i => i.End - i.Start)
            : _exportRange.End - _exportRange.Start;
        return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString();
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
        => BandStyle(_exportRange.Start, _exportRange.End);

    private string BandStyle(double start, double end)
    {
        if (_timelineMaxSeconds <= 0) return "display:none;";

        var startPercent = (start / _timelineMaxSeconds) * 100;
        var endPercent = (end / _timelineMaxSeconds) * 100;
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
        // Nothing captures the pointer, so releasing outside the strip never reaches
        // OnSliderContainerPointerUp and the marker would follow the next button-less hover.
        // Clearing before the position is computed means no jump on re-entry.
        if (e.Buttons == 0)
        {
            _draggingMarker = DragMarker.None;
            return;
        }

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
