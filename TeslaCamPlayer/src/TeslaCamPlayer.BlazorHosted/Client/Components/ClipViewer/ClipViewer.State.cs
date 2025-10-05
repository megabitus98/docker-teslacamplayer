using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private Clip _clip;
    private int _videoLoadedEventCount;
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
    private Tile? _fullscreenTile;
    private GridMode _gridMode = GridMode.Locked;
    private bool _isFullscreenPending;
    private double[] _pendingFullscreenStartRect;
    private ElementReference _gridElement;
    private DotNetObjectReference<ClipViewer> _objRef;
    private (double Start, double End) _exportRange;

    private double TimelineValue
    {
        get => _timelineValue;
        set
        {
            _timelineValue = value;
            if (_isScrubbing)
            {
                _setVideoTimeDebounceTimer.Enabled = true;
            }
        }
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

    private async Task<bool> SetCurrentSegmentVideosAsync()
    {
        if (_currentSegment == null)
        {
            return false;
        }

        await _loadSegmentCts.CancelAsync();
        _loadSegmentCts = new();
        _videoLoadedEventCount = 0;

        var wasPlaying = _isPlaying;
        if (wasPlaying)
        {
            await TogglePlayingAsync(false);
        }

        foreach (var tile in _tiles)
        {
            SetSrcIfChanged(tile.Player, tile.SourceFor(_currentSegment));
        }

        if (_loadSegmentCts.IsCancellationRequested)
        {
            return false;
        }

        await InvokeAsync(StateHasChanged);

        var timeout = Task.Delay(5000);
        var cameraCount = _tiles
            .Select(tile => tile.Player?.Src)
            .Count(src => !string.IsNullOrWhiteSpace(src));

        var completedTask = await Task.WhenAny(Task.Run(async () =>
        {
            while (_videoLoadedEventCount < cameraCount && !_loadSegmentCts.IsCancellationRequested)
            {
                await Task.Delay(10, _loadSegmentCts.Token);
            }

            Console.WriteLine("Loading done");
        }, _loadSegmentCts.Token), timeout);

        if (completedTask == timeout)
        {
            Console.WriteLine("Loading timed out — continuing");
        }

        if (wasPlaying)
        {
            await TogglePlayingAsync(true);
        }

        return !_loadSegmentCts.IsCancellationRequested;
    }

    private async Task ExecuteOnPlayers(Func<VideoPlayer, Task> action)
    {
        foreach (var tile in _tiles)
        {
            var player = tile.Player;
            if (player == null || string.IsNullOrWhiteSpace(player.Src))
            {
                continue;
            }

            try
            {
                await action(player);
            }
            catch
            {
                // intentionally ignored – resilient playback loop
            }
        }
    }

    private static void SetSrcIfChanged(VideoPlayer player, string newSrc)
    {
        if (player == null || player.Src == newSrc)
        {
            return;
        }

        player.Src = newSrc;
    }
}
