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
        await ScrubToSliderTime();
        _isScrubbing = false;

        if (!_isPlaying && _wasPlayingBeforeScrub)
        {
            await TogglePlayingAsync(true);
        }
    }

    private static readonly string[] SliderNavigationKeys =
        { "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown" };

    /// <summary>
    /// Keyboard changes the bound value without any pointer event, and the debounced seek is only armed
    /// while <see cref="_isScrubbing"/> is set — so without this the thumb moves and the video never
    /// follows. Mirrors <see cref="TimelineSliderPointerUp"/> for the keys the range input handles.
    /// </summary>
    private async Task TimelineSliderKeyUp(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (!SliderNavigationKeys.Contains(e.Key))
        {
            return;
        }

        _isScrubbing = true;
        await ScrubToSliderTime();
        _isScrubbing = false;
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
