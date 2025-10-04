using System;
using System.Linq;
using System.Threading.Tasks;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private Task TogglePlayingAsync(bool? play = null)
    {
        play ??= !_isPlaying;
        _isPlaying = play.Value;
        return ExecuteOnPlayers(async player => await (play.Value ? player.PlayAsync() : player.PauseAsync()));
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
        {
            return;
        }

        var targetTimelineSeconds = Math.Clamp(TimelineValue + offsetSeconds, 0, _timelineMaxSeconds);
        await SeekToTimelineSecondsAsync(targetTimelineSeconds);
    }

    private async Task SeekToTimelineSecondsAsync(double targetTimelineSeconds)
    {
        if (_clip == null)
        {
            return;
        }

        targetTimelineSeconds = Math.Clamp(targetTimelineSeconds, 0, _timelineMaxSeconds);
        var scrubToDate = _clip.StartDate.AddSeconds(targetTimelineSeconds);
        var segment = _clip.SegmentAtDate(scrubToDate)
            ?? _clip.Segments.Where(s => s.StartDate > scrubToDate).MinBy(s => s.StartDate);

        if (segment == null)
        {
            return;
        }

        var wasPlaying = _isPlaying;
        if (wasPlaying)
        {
            await TogglePlayingAsync(false);
        }

        if (segment != _currentSegment)
        {
            _currentSegment = segment;
            if (!await SetCurrentSegmentVideosAsync())
            {
                if (wasPlaying)
                {
                    await TogglePlayingAsync(true);
                }

                return;
            }
        }

        var secondsIntoSegment = (scrubToDate - segment.StartDate).TotalSeconds;
        await ExecuteOnPlayers(async player => await player.SetTimeAsync(secondsIntoSegment));

        TimelineValue = targetTimelineSeconds;
        _ignoreTimelineValue = targetTimelineSeconds;

        if (wasPlaying)
        {
            await TogglePlayingAsync(true);
        }
    }

    private async Task VideoEnded()
    {
        if (_currentSegment == _clip.Segments.Last())
        {
            return;
        }

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
        if (_currentSegment == null || _isScrubbing)
        {
            return;
        }

        var player = GetActiveTimeSourcePlayer();
        if (player == null)
        {
            return;
        }

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
                {
                    return player;
                }
            }
        }

        return null;
    }
}
