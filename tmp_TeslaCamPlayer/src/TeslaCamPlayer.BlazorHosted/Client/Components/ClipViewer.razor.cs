using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Collections.Generic;
using System.Linq;
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
    private sealed class TileDefinition
    {
        private readonly Func<ClipVideoSegment, VideoFile> _segmentSelector;
        private readonly Func<CameraFilterValues, bool> _isEnabledPredicate;

        public TileDefinition(
            Tile tile,
            string label,
            string dataCamera,
            string videoKey,
            Func<ClipVideoSegment, VideoFile> segmentSelector,
            Func<CameraFilterValues, bool> isEnabledPredicate)
        {
            Tile = tile;
            Label = label;
            DataCamera = dataCamera;
            VideoKey = videoKey;
            _segmentSelector = segmentSelector;
            _isEnabledPredicate = isEnabledPredicate;
        }

        public Tile Tile { get; }
        public string Label { get; }
        public string DataCamera { get; }
        public string VideoKey { get; }
        public VideoPlayer Player { get; set; }

        public string SourceFor(ClipVideoSegment segment)
            => _segmentSelector?.Invoke(segment)?.Url;

        public bool IsEnabled(CameraFilterValues filter)
            => _isEnabledPredicate?.Invoke(filter) ?? true;
    }

    private readonly TileDefinition[] _tiles;
    private readonly Dictionary<Tile, TileDefinition> _tileLookup;
    private static readonly Tile[] TimeSourcePriority = new[]
    {
        Tile.Front,
        Tile.Back,
        Tile.LeftRepeater,
        Tile.RightRepeater,
        Tile.LeftPillar,
        Tile.RightPillar
    };

    public ClipViewer()
    {
        _tiles = new[]
        {
            new TileDefinition(Tile.LeftPillar, "Left Pillar", "left-pillar", "L-BPILLAR", segment => segment?.CameraLeftBPillar, filter => filter.ShowLeftPillar),
            new TileDefinition(Tile.Front, "Front", "front", "128D7AB3", segment => segment?.CameraFront, filter => filter.ShowFront),
            new TileDefinition(Tile.RightPillar, "Right Pillar", "right-pillar", "R-BPILLAR", segment => segment?.CameraRightBPillar, filter => filter.ShowRightPillar),
            new TileDefinition(Tile.LeftRepeater, "Left Repeater", "left-repeater", "D1916B24", segment => segment?.CameraLeftRepeater, filter => filter.ShowLeftRepeater),
            new TileDefinition(Tile.Back, "Back", "back", "66EC38D4", segment => segment?.CameraBack, filter => filter.ShowBack),
            new TileDefinition(Tile.RightRepeater, "Right Repeater", "right-repeater", "87B15DCA", segment => segment?.CameraRightRepeater, filter => filter.ShowRightRepeater)
        };

        _tileLookup = _tiles.ToDictionary(t => t.Tile);
    }

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
    private int _videoLoadedEventCount = 0;
    private bool _isPlaying;
    private ClipVideoSegment _currentSegment;
    private double _timelineMaxSeconds;
    private double _ignoreTimelineValue;
    private bool _wasPlayingBeforeScrub;
    private bool _isScrubbing;
    private double _timelineValue;
    private System.Timers.Timer _setVideoTimeDebounceTimer;
    private CancellationTokenSource _loadSegmentCts = new();
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
        foreach (var tile in _tiles)
        {
            if (tile.Player != null)
            {
                tile.Player.Loaded += () => { _videoLoadedEventCount++; };
            }
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
    }

    private async Task EnsurePlayersReadyAsync()
    {
        var sw = Stopwatch.StartNew();
        while (_tiles.Any(t => t.Player == null) && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { await InvokeAsync(StateHasChanged); } catch { }
            await Task.Delay(10);
        }
    }

    private bool IsTileVisible(Tile tile)
    {
        if (!_tileLookup.TryGetValue(tile, out var definition))
            return false;

        var hasSrc = !string.IsNullOrWhiteSpace(definition.Player?.Src);
        return definition.IsEnabled(CameraFilter) && hasSrc;
    }

    private int VisibleTileCount()
    {
        return _tiles.Count(tile => IsTileVisible(tile.Tile));
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

        // Use minmax(0, 1fr) so rows can shrink within the container without forcing overflow
        return $"grid-template-columns: repeat({cols}, minmax(0, 1fr)); grid-auto-rows: minmax(0, 1fr);";
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
        foreach (var tile in _tiles)
        {
            SetSrcIfChanged(tile.Player, tile.SourceFor(_currentSegment));
        }

        if (_loadSegmentCts.IsCancellationRequested)
            return false;

        await InvokeAsync(StateHasChanged);

        var timeout = Task.Delay(5000);
        var cameraCount = _tiles
            .Select(tile => tile.Player?.Src)
            .Count(src => !string.IsNullOrWhiteSpace(src));
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
            foreach (var tile in _tiles)
            {
                if (tile.Player != null)
                    await action(tile.Player);
            }
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

    private Task SkipBackwardTenSeconds()
        => SkipBySecondsAsync(-10);

    private Task SkipForwardTenSeconds()
        => SkipBySecondsAsync(10);

    private async Task SkipBySecondsAsync(double offsetSeconds)
    {
        if (_clip == null)
            return;

        var targetTimelineSeconds = Math.Clamp(TimelineValue + offsetSeconds, 0, _timelineMaxSeconds);
        await SeekToTimelineSecondsAsync(targetTimelineSeconds);
    }

    private async Task SeekToTimelineSecondsAsync(double targetTimelineSeconds)
    {
        if (_clip == null)
            return;

        targetTimelineSeconds = Math.Clamp(targetTimelineSeconds, 0, _timelineMaxSeconds);
        var scrubToDate = _clip.StartDate.AddSeconds(targetTimelineSeconds);
        var segment = _clip.SegmentAtDate(scrubToDate)
            ?? _clip.Segments.Where(s => s.StartDate > scrubToDate).MinBy(s => s.StartDate);

        if (segment == null)
            return;

        var wasPlaying = _isPlaying;
        if (wasPlaying)
            await TogglePlayingAsync(false);

        if (segment != _currentSegment)
        {
            _currentSegment = segment;
            if (!await SetCurrentSegmentVideosAsync())
            {
                if (wasPlaying)
                    await TogglePlayingAsync(true);
                return;
            }
        }

        var secondsIntoSegment = (scrubToDate - segment.StartDate).TotalSeconds;
        await ExecuteOnPlayers(async p => await p.SetTimeAsync(secondsIntoSegment));

        TimelineValue = targetTimelineSeconds;
        _ignoreTimelineValue = targetTimelineSeconds;

        if (wasPlaying)
            await TogglePlayingAsync(true);
    }

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
        foreach (var tile in TimeSourcePriority)
        {
            if (_tileLookup.TryGetValue(tile, out var definition))
            {
                var player = definition.Player;
                if (!string.IsNullOrWhiteSpace(player?.Src))
                    return player;
            }
        }

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
