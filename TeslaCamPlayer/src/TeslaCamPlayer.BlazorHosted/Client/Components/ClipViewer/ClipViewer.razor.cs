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
    }

    private async void ScrubVideoDebounceTick(object _, ElapsedEventArgs __)
        => await ScrubToSliderTime();

    private static Task AwaitUiUpdate()
        => Task.Delay(100);

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
}
