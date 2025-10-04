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
}
