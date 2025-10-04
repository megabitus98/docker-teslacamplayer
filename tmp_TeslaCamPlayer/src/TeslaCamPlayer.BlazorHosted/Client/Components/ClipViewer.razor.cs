using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using Microsoft.AspNetCore.Components.Web;
using System.Diagnostics;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer : ComponentBase, IDisposable
{
    private enum Tile
    {
        Front,
        Back,
        LeftRepeater,
        RightRepeater,
        LeftPillar,
        RightPillar
    }
    private static readonly TimeSpan TimelineScrubTimeout = TimeSpan.FromSeconds(2);

    [Inject]
    public IJSRuntime JsRuntime { get; set; }

    [Parameter]
    public EventCallback PreviousButtonClicked { get; set; }

    [Parameter]
    public EventCallback NextButtonClicked { get; set; }

    private double TimelineValue
    {
        get => _timelineValue;
        set
        {
            _timelineValue = value;
            if (_isScrubbing)
                _setVideoTimeDebounceTimer.Enabled = true;
        }
    }

    private Clip _clip;
    private VideoPlayer _videoPlayerFront;
    private VideoPlayer _videoPlayerLeftRepeater;
    private VideoPlayer _videoPlayerRightRepeater;
    private VideoPlayer _videoPlayerBack;
    private VideoPlayer _videoPlayerLeftBPillar;
    private VideoPlayer _videoPlayerRightBPillar;
    private int _videoLoadedEventCount = 0;
    private bool _isPlaying;
    private ClipVideoSegment _currentSegment;
    private MudSlider<double> _timelineSlider;
    private double _timelineMaxSeconds;
    private double _ignoreTimelineValue;
    private bool _wasPlayingBeforeScrub;
    private bool _isScrubbing;
    private double _timelineValue;
    private System.Timers.Timer _setVideoTimeDebounceTimer;
    private CancellationTokenSource _loadSegmentCts = new();
    private string mainVideoKey;
    private CameraFilterValues _lastAppliedCameraFilter = new();
    private Tile? _fullscreenTile = null; // null = grid view, otherwise one of the Tile enum values
    private DotNetObjectReference<ClipViewer> _objRef;

    [Parameter]
    public TeslaCamPlayer.BlazorHosted.Client.Models.CameraFilterValues CameraFilter { get; set; } = new();

    protected override void OnInitialized()
    {
        _setVideoTimeDebounceTimer = new(500);
        _setVideoTimeDebounceTimer.Elapsed += ScrubVideoDebounceTick;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender)
            return;

        _objRef = DotNetObjectReference.Create(this);

        if (_videoPlayerFront != null)
        {
            _videoPlayerFront.Loaded += () => { _videoLoadedEventCount++; };
        }
        if (_videoPlayerLeftRepeater != null)
        {
            _videoPlayerLeftRepeater.Loaded += () => { _videoLoadedEventCount++; };
        }
        if (_videoPlayerRightRepeater != null)
        {
            _videoPlayerRightRepeater.Loaded += () => { _videoLoadedEventCount++; };
        }
        if (_videoPlayerBack != null)
        {
            _videoPlayerBack.Loaded += () => { _videoLoadedEventCount++; };
        }
        if (_videoPlayerLeftBPillar != null)
        {
            _videoPlayerLeftBPillar.Loaded += () => { _videoLoadedEventCount++; };
        }
        if (_videoPlayerRightBPillar != null)
        {
            _videoPlayerRightBPillar.Loaded += () => { _videoLoadedEventCount++; };
        }
    }

    private static Task AwaitUiUpdate()
        => Task.Delay(100);

    private bool IsFullscreen => _fullscreenTile.HasValue;

    private string GetTileCss(Tile tile)
        => _fullscreenTile == tile ? "is-fullscreen" : null;

    private async Task ToggleFullscreen(Tile tile)
    {
        if (_fullscreenTile == tile)
        {
            await ExitFullscreen();
        }
        else
        {
            await EnterFullscreen(tile);
        }
    }

    private async Task EnterFullscreen(Tile tile)
    {
        _fullscreenTile = tile;
        try { await JsRuntime.InvokeVoidAsync("registerEscHandler", _objRef); } catch { }
        await InvokeAsync(StateHasChanged);
    }

    private async Task ExitFullscreen()
    {
        _fullscreenTile = null;
        try { await JsRuntime.InvokeVoidAsync("unregisterEscHandler"); } catch { }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task ExitFullscreenFromJs()
        => ExitFullscreen();

    private async Task TileKeyDown(KeyboardEventArgs e, Tile tile)
    {
        if (e.Key == "Enter" || e.Key == " ")
        {
            await ToggleFullscreen(tile);
        }
        else if (e.Key == "Escape" && IsFullscreen)
        {
            await ExitFullscreen();
        }
    }

    public async Task SetClipAsync(Clip clip)
    {
        _clip = clip;
        await EnsurePlayersReadyAsync();
        TimelineValue = 0;
        _timelineMaxSeconds = (clip.EndDate - clip.StartDate).TotalSeconds;

        _currentSegment = _clip.Segments.First();
        await SetCurrentSegmentVideosAsync();

        if (clip?.Event?.Camera == null)
        { mainVideoKey = "128D7AB3"; }
        else
        { mainVideoKey = CameraToVideoKey(_clip.Event.Camera); }
    }

    private async Task EnsurePlayersReadyAsync()
    {
        var sw = Stopwatch.StartNew();
        while ((_videoPlayerFront == null ||
                _videoPlayerBack == null ||
                _videoPlayerLeftRepeater == null ||
                _videoPlayerRightRepeater == null ||
                _videoPlayerLeftBPillar == null ||
                _videoPlayerRightBPillar == null) && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { await InvokeAsync(StateHasChanged); } catch { }
            await Task.Delay(10);
        }
    }

    private void SwitchMainVideo(string newMainVideoKey)
    {
        mainVideoKey = newMainVideoKey;
    }

    private string CameraToVideoKey(Cameras camera)
    {
        switch (camera)
        {
            case Cameras.Front:
                return "128D7AB3";
            case Cameras.RightRepeater:
            case Cameras.RightBPillar:
                return "87B15DCA";
            case Cameras.Back:
                return "66EC38D4";
            case Cameras.LeftRepeater:
            case Cameras.LeftBPillar:
                return "D1916B24";
            default:
                return "128D7AB3";
        }
    }


    private string GetVideoClass(string videoKey)
    {
        if (videoKey == mainVideoKey)
            return "video main-video";
        else
        {
            return videoKey switch
            {
                "128D7AB3" => "video small-video top-left-video",
                "66EC38D4" => "video small-video top-right-video",
                "D1916B24" => "video small-video bottom-left-video",
                "87B15DCA" => "video small-video bottom-right-video",
                _ => ""
            };
        }
    }

    private string GetPlayerClass(string videoKey)
    {
        return videoKey == mainVideoKey ? "video main-video" : "video small-video-style";
    }

    private bool IsTileEnabled(Tile tile)
    {
        return tile switch
        {
            Tile.Front => CameraFilter.ShowFront,
            Tile.Back => CameraFilter.ShowBack,
            Tile.LeftRepeater => CameraFilter.ShowLeftRepeater,
            Tile.RightRepeater => CameraFilter.ShowRightRepeater,
            Tile.LeftPillar => CameraFilter.ShowLeftPillar,
            Tile.RightPillar => CameraFilter.ShowRightPillar,
            _ => true
        };
    }

    private bool IsTileVisible(Tile tile)
    {
        bool hasSrc = tile switch
        {
            Tile.Front => !string.IsNullOrWhiteSpace(_videoPlayerFront?.Src),
            Tile.Back => !string.IsNullOrWhiteSpace(_videoPlayerBack?.Src),
            Tile.LeftRepeater => !string.IsNullOrWhiteSpace(_videoPlayerLeftRepeater?.Src),
            Tile.RightRepeater => !string.IsNullOrWhiteSpace(_videoPlayerRightRepeater?.Src),
            Tile.LeftPillar => !string.IsNullOrWhiteSpace(_videoPlayerLeftBPillar?.Src),
            Tile.RightPillar => !string.IsNullOrWhiteSpace(_videoPlayerRightBPillar?.Src),
            _ => false
        };
        return IsTileEnabled(tile) && hasSrc;
    }

    private int VisibleTileCount()
    {
        int count = 0;
        if (IsTileVisible(Tile.LeftPillar)) count++;
        if (IsTileVisible(Tile.Front)) count++;
        if (IsTileVisible(Tile.RightPillar)) count++;
        if (IsTileVisible(Tile.LeftRepeater)) count++;
        if (IsTileVisible(Tile.Back)) count++;
        if (IsTileVisible(Tile.RightRepeater)) count++;
        return count;
    }

    private string GridStyle()
    {
        var visible = VisibleTileCount();
        int cols = visible switch
        {
            >= 5 => 3,
            4 => 2,
            3 => 3,
            2 => 2,
            1 => 1,
            _ => 3
        };

        return $"grid-template-columns: repeat({cols}, minmax(0, 1fr)); grid-auto-rows: 1fr;";
    }

    private string GetCurrentScrubTime()
    {
        if (_clip == null)
        { return ""; }

        var currentTime = _clip.StartDate.AddSeconds(TimelineValue);
        return currentTime.ToString("hh:mm:ss tt");
    }

    private async Task<bool> SetCurrentSegmentVideosAsync()
    {
        if (_currentSegment == null)
            return false;

        await _loadSegmentCts.CancelAsync();
        _loadSegmentCts = new();

        _videoLoadedEventCount = 0;

        var wasPlaying = _isPlaying;
        if (wasPlaying)
            await TogglePlayingAsync(false);

        // Always load sources based on current segment; filtering only affects visibility, not source assignment.
        SetSrcIfChanged(_videoPlayerFront, _currentSegment.CameraFront?.Url);
        SetSrcIfChanged(_videoPlayerBack, _currentSegment.CameraBack?.Url);
        SetSrcIfChanged(_videoPlayerLeftRepeater, _currentSegment.CameraLeftRepeater?.Url);
        SetSrcIfChanged(_videoPlayerRightRepeater, _currentSegment.CameraRightRepeater?.Url);
        SetSrcIfChanged(_videoPlayerLeftBPillar, _currentSegment.CameraLeftBPillar?.Url);
        SetSrcIfChanged(_videoPlayerRightBPillar, _currentSegment.CameraRightBPillar?.Url);

        if (_loadSegmentCts.IsCancellationRequested)
            return false;

        await InvokeAsync(StateHasChanged);

        var timeout = Task.Delay(5000);
        var cameraCount = new[] { _videoPlayerFront.Src, _videoPlayerLeftRepeater.Src, _videoPlayerRightRepeater.Src, _videoPlayerBack.Src, _videoPlayerLeftBPillar.Src, _videoPlayerRightBPillar.Src }
            .Count(s => !string.IsNullOrWhiteSpace(s));
        var completedTask = await Task.WhenAny(Task.Run(async () =>
        {
            while (_videoLoadedEventCount < cameraCount && !_loadSegmentCts.IsCancellationRequested)
                await Task.Delay(10, _loadSegmentCts.Token);

            Console.WriteLine("Loading done");
        }, _loadSegmentCts.Token), timeout);

        // If loading times out, continue anyway to avoid stalled playback; players may still be able to start.
        if (completedTask == timeout)
            Console.WriteLine("Loading timed out — continuing");

        if (wasPlaying)
            await TogglePlayingAsync(true);

        return !_loadSegmentCts.IsCancellationRequested;
    }

    private async Task ExecuteOnPlayers(Func<VideoPlayer, Task> action)
    {
        try
        {
            if (_videoPlayerFront != null) await action(_videoPlayerFront);
            if (_videoPlayerBack != null) await action(_videoPlayerBack);
            if (_videoPlayerLeftRepeater != null) await action(_videoPlayerLeftRepeater);
            if (_videoPlayerRightRepeater != null) await action(_videoPlayerRightRepeater);
            if (_videoPlayerLeftBPillar != null) await action(_videoPlayerLeftBPillar);
            if (_videoPlayerRightBPillar != null) await action(_videoPlayerRightBPillar);
        }
        catch
        {
            // ignore
        }
    }

    private Task TogglePlayingAsync(bool? play = null)
    {
        play ??= !_isPlaying;
        _isPlaying = play.Value;
        return ExecuteOnPlayers(async p => await (play.Value ? p.PlayAsync() : p.PauseAsync()));
    }

    private Task PlayPauseClicked()
        => TogglePlayingAsync();

    private async Task VideoEnded()
    {
        if (_currentSegment == _clip.Segments.Last())
            return;

        await TogglePlayingAsync(false);

        var nextSegment = _clip.Segments
            .OrderBy(s => s.StartDate)
            .SkipWhile(s => s != _currentSegment)
            .Skip(1)
            .FirstOrDefault()
            ?? _clip.Segments.FirstOrDefault();

        if (nextSegment == null)
        {
            await TogglePlayingAsync(false);
            return;
        }

        _currentSegment = nextSegment;
        await SetCurrentSegmentVideosAsync();
        await AwaitUiUpdate();
        await TogglePlayingAsync(true);
    }

    private async Task ActiveVideoTimeUpdate()
    {
        if (_currentSegment == null)
            return;

        if (_isScrubbing)
            return;

        var player = GetActiveTimeSourcePlayer();
        if (player == null)
            return;

        var seconds = await player.GetTimeAsync();
        var currentTime = _currentSegment.StartDate.AddSeconds(seconds);
        var secondsSinceClipStart = (currentTime - _clip.StartDate).TotalSeconds;

        _ignoreTimelineValue = secondsSinceClipStart;
        TimelineValue = secondsSinceClipStart;
    }

    private VideoPlayer GetActiveTimeSourcePlayer()
    {
        // Prefer a stable ordering for time source in 6-up grid
        if (!string.IsNullOrWhiteSpace(_videoPlayerFront?.Src)) return _videoPlayerFront;
        if (!string.IsNullOrWhiteSpace(_videoPlayerBack?.Src)) return _videoPlayerBack;
        if (!string.IsNullOrWhiteSpace(_videoPlayerLeftRepeater?.Src)) return _videoPlayerLeftRepeater;
        if (!string.IsNullOrWhiteSpace(_videoPlayerRightRepeater?.Src)) return _videoPlayerRightRepeater;
        if (!string.IsNullOrWhiteSpace(_videoPlayerLeftBPillar?.Src)) return _videoPlayerLeftBPillar;
        if (!string.IsNullOrWhiteSpace(_videoPlayerRightBPillar?.Src)) return _videoPlayerRightBPillar;
        return null;
    }

    private string GetLeftCameraUrl()
    {
        var allowRepeater = CameraFilter.ShowLeftRepeater;
        var allowPillar = CameraFilter.ShowLeftPillar;

        if (!allowRepeater && !allowPillar)
            return null;

        // Prefer pillar when both are selected, else whichever is allowed
        if (allowPillar && _currentSegment.CameraLeftBPillar?.Url != null)
            return _currentSegment.CameraLeftBPillar.Url;
        if (allowRepeater && _currentSegment.CameraLeftRepeater?.Url != null)
            return _currentSegment.CameraLeftRepeater.Url;

        // Fallback to the other if preferred is missing
        if (allowRepeater)
            return _currentSegment.CameraLeftRepeater?.Url;
        if (allowPillar)
            return _currentSegment.CameraLeftBPillar?.Url;

        return null;
    }

    private string GetRightCameraUrl()
    {
        var allowRepeater = CameraFilter.ShowRightRepeater;
        var allowPillar = CameraFilter.ShowRightPillar;

        if (!allowRepeater && !allowPillar)
            return null;

        if (allowPillar && _currentSegment.CameraRightBPillar?.Url != null)
            return _currentSegment.CameraRightBPillar.Url;
        if (allowRepeater && _currentSegment.CameraRightRepeater?.Url != null)
            return _currentSegment.CameraRightRepeater.Url;

        if (allowRepeater)
            return _currentSegment.CameraRightRepeater?.Url;
        if (allowPillar)
            return _currentSegment.CameraRightBPillar?.Url;

        return null;
    }

    protected override async Task OnParametersSetAsync()
    {
        // When filter changes, only update UI state to avoid interrupting playback
        if (_clip != null && _currentSegment != null)
        {
            if (_lastAppliedCameraFilter.ShowFront != CameraFilter.ShowFront ||
                _lastAppliedCameraFilter.ShowBack != CameraFilter.ShowBack ||
                _lastAppliedCameraFilter.ShowLeftRepeater != CameraFilter.ShowLeftRepeater ||
                _lastAppliedCameraFilter.ShowLeftPillar != CameraFilter.ShowLeftPillar ||
                _lastAppliedCameraFilter.ShowRightRepeater != CameraFilter.ShowRightRepeater ||
                _lastAppliedCameraFilter.ShowRightPillar != CameraFilter.ShowRightPillar)
            {
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
        }
    }

    private static void SetSrcIfChanged(VideoPlayer player, string newSrc)
    {
        if (player == null) return;
        if (player.Src == newSrc) return;
        player.Src = newSrc;
    }

    private async Task TimelineSliderPointerDown()
    {
        _isScrubbing = true;
        _wasPlayingBeforeScrub = _isPlaying;
        await TogglePlayingAsync(false);

        // Allow value change event to trigger, then scrub before user releases mouse click
        await AwaitUiUpdate();
        await ScrubToSliderTime();
    }

    private async Task TimelineSliderPointerUp()
    {
        Console.WriteLine("Pointer up");
        await ScrubToSliderTime();
        _isScrubbing = false;

        if (!_isPlaying && _wasPlayingBeforeScrub)
            await TogglePlayingAsync(true);
    }

    private async void ScrubVideoDebounceTick(object _, ElapsedEventArgs __)
        => await ScrubToSliderTime();

    private async Task ScrubToSliderTime()
    {
        _setVideoTimeDebounceTimer.Enabled = false;

        if (!_isScrubbing)
            return;

        try
        {
            var scrubToDate = _clip.StartDate.AddSeconds(TimelineValue);
            var segment = _clip.SegmentAtDate(scrubToDate)
                ?? _clip.Segments.Where(s => s.StartDate > scrubToDate).MinBy(s => s.StartDate);

            if (segment == null)
                return;

            if (segment != _currentSegment)
            {
                _currentSegment = segment;
                if (!await SetCurrentSegmentVideosAsync())
                    return;
            }

            var secondsIntoSegment = (scrubToDate - segment.StartDate).TotalSeconds;
            await ExecuteOnPlayers(async p => await p.SetTimeAsync(secondsIntoSegment));
        }
        catch
        {
            // ignore, happens sometimes
        }
    }

    private async void JumpToEventMarker()
    {
        if (_clip?.Event?.Timestamp == null)
            return;

        var eventTimeSeconds = (_clip.Event.Timestamp - _clip.StartDate).TotalSeconds - 5;
        eventTimeSeconds = Math.Max(eventTimeSeconds, 0);

        _isScrubbing = true;
        TimelineValue = eventTimeSeconds;
        await ScrubToSliderTime();
        _isScrubbing = false;

        await TogglePlayingAsync(true);
    }

    private double DateTimeToTimelinePercentage(DateTime dateTime)
    {
        var percentage = Math.Round(dateTime.Subtract(_clip.StartDate).TotalSeconds / _clip.TotalSeconds * 100, 2);
        return Math.Clamp(percentage, 0, 100);
    }

    private string SegmentStartMarkerStyle(ClipVideoSegment segment)
    {
        var percentage = DateTimeToTimelinePercentage(segment.StartDate);
        return $"left: {percentage}%";
    }

    private string EventMarkerStyle()
    {
        if (_clip?.Event?.Timestamp == null)
            return "display: none";

        var percentage = DateTimeToTimelinePercentage(_clip.Event.Timestamp);
        return $"left: {percentage}%";
    }

    public void Dispose()
    {
        try { JsRuntime?.InvokeVoidAsync("unregisterEscHandler"); } catch { }
        try { _objRef?.Dispose(); } catch { }
    }
}
