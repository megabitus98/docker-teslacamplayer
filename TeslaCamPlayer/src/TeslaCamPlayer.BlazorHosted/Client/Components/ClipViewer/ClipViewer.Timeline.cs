using System;
using System.Linq;
using System.Threading.Tasks;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private string GetCurrentScrubTime()
    {
        if (_clip == null)
        {
            return string.Empty;
        }

        var currentTime = _clip.StartDate.AddSeconds(TimelineValue);
        return currentTime.ToString("hh:mm:ss tt");
    }

    private async Task TimelineSliderPointerDown()
    {
        _isScrubbing = true;
        _wasPlayingBeforeScrub = _isPlaying;
        await TogglePlayingAsync(false);

        await AwaitUiUpdate();
        await ScrubToSliderTime();
    }

    private async Task TimelineSliderPointerUp()
    {
        Console.WriteLine("Pointer up");
        await ScrubToSliderTime();
        _isScrubbing = false;

        if (!_isPlaying && _wasPlayingBeforeScrub)
        {
            await TogglePlayingAsync(true);
        }
    }

    private async Task ScrubToSliderTime()
    {
        _setVideoTimeDebounceTimer.Enabled = false;

        if (!_isScrubbing)
        {
            return;
        }

        try
        {
            var scrubToDate = _clip.StartDate.AddSeconds(TimelineValue);
            var segment = _clip.SegmentAtDate(scrubToDate)
                ?? _clip.Segments.Where(s => s.StartDate > scrubToDate).MinBy(s => s.StartDate);

            if (segment == null)
            {
                return;
            }

            if (segment != _currentSegment)
            {
                _currentSegment = segment;
                if (!await SetCurrentSegmentVideosAsync())
                {
                    return;
                }
            }

            var secondsIntoSegment = (scrubToDate - segment.StartDate).TotalSeconds;
            await ExecuteOnPlayers(async player => await player.SetTimeAsync(secondsIntoSegment));
        }
        catch
        {
            // occasionally triggered when players reset during scrubbing
        }
    }

    private async void JumpToEventMarker()
    {
        if (_clip?.Event?.Timestamp == null)
        {
            return;
        }

        var eventTimeSeconds = (_clip.Event.Timestamp - _clip.StartDate).TotalSeconds - 5;
        eventTimeSeconds = Math.Max(eventTimeSeconds, 0);

        _isScrubbing = true;
        TimelineValue = eventTimeSeconds;
        await ScrubToSliderTime();
        _isScrubbing = false;

        await TogglePlayingAsync(true);
    }
}
