using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using System.Timers;
using TeslaCamPlayer.BlazorHosted.Client.Helpers;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using System.Collections.Generic;

namespace TeslaCamPlayer.BlazorHosted.Client.Pages;

public partial class Index
{
    private const int EventItemHeight = 82;

    private int _totalClipCount;
    private bool _isInitialLoading = true;

    private HashSet<DateTime> _eventDates = new();

    private ElementReference _eventsList;

    private async ValueTask<ItemsProviderResult<Clip>> LoadClipsAsync(ItemsProviderRequest request)
    {
        try
        {
            var types = GetSelectedClipTypes();
            var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
            var url = $"Api/GetClipsPaged?skip={request.StartIndex}&take={request.Count}";
            if (!string.IsNullOrEmpty(typesQuery))
            {
                url += "&" + typesQuery;
            }

            var response = await HttpClient.GetFromNewtonsoftJsonAsync<ClipPagedResponse>(url);

            if (response == null)
            {
                return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), 0);
            }

            _totalClipCount = response.TotalCount;

            // Cache loaded clips for scroll position calculation
            for (int i = 0; i < response.Items.Length; i++)
            {
                _loadedClips[request.StartIndex + i] = response.Items[i];
            }

            // Resync _activeClip to the new instance if it appears in this batch,
            // so that reference-equality code (scroll, navigation) stays correct after a refresh.
            if (_activeClip != null)
            {
                var refreshed = Array.Find(response.Items, c => c.DirectoryPath == _activeClip.DirectoryPath);
                if (refreshed != null)
                    _activeClip = refreshed;
            }

            // Set first clip as active if none selected
            if (_activeClip == null && response.Items.Length > 0 && request.StartIndex == 0)
            {
                await InvokeAsync(async () =>
                {
                    await SetActiveClip(response.Items[0]);
                });
            }

            return new ItemsProviderResult<Clip>(response.Items, response.TotalCount);
        }
        catch (OperationCanceledException)
        {
            return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), _totalClipCount);
        }
        catch
        {
            return new ItemsProviderResult<Clip>(Array.Empty<Clip>(), _totalClipCount);
        }
    }

    private ClipType[] GetSelectedClipTypes()
    {
        var types = new List<ClipType>();

        if (_eventFilter.Recent)
            types.Add(ClipType.Recent);
        if (_eventFilter.DashcamHonk || _eventFilter.DashcamSaved || _eventFilter.DashcamOther)
            types.Add(ClipType.Saved);
        if (_eventFilter.SentryObjectDetection || _eventFilter.SentryAccelerationDetection || _eventFilter.SentryOther)
            types.Add(ClipType.Sentry);

        // If all or none are selected, return empty (no filter)
        if (types.Count == 0 || types.Count == 3)
            return Array.Empty<ClipType>();

        return types.ToArray();
    }

    private async Task InitializeClipsAsync()
    {
        _isInitialLoading = true;
        _setDatePickerInitialDate = false;
        _loadedClips.Clear();

        await LoadAvailableDatesAsync();

        _isInitialLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private async Task LoadAvailableDatesAsync()
    {
        try
        {
            var types = GetSelectedClipTypes();
            var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
            var url = "Api/GetAvailableDates";
            if (!string.IsNullOrEmpty(typesQuery))
            {
                url += "?" + typesQuery;
            }

            var dates = await HttpClient.GetFromNewtonsoftJsonAsync<DateTime[]>(url);
            _eventDates = dates?.ToHashSet() ?? new HashSet<DateTime>();
        }
        catch
        {
            _eventDates = new HashSet<DateTime>();
        }
    }

    private bool IsDateDisabledFunc(DateTime date)
        => _eventDates?.Contains(date) != true;

    private class ScrollToOptions
    {
        public int? Left { get; set; }

        public int? Top { get; set; }

        public string Behavior { get; set; }
    }

    private async Task DatePicked(DateTime? pickedDate)
    {
        if (!pickedDate.HasValue || _ignoreDatePicked == pickedDate)
            return;

        // Find the first loaded clip at this date (lowest index = newest event on that date in DESC list)
        var firstClipAtDate = _loadedClips
            .Where(kvp => kvp.Value.StartDate.Date == pickedDate)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();
        if (firstClipAtDate != null)
        {
            await SetActiveClip(firstClipAtDate);
            await ScrollListToActiveClip();
        }
        else
        {
            // Clip not loaded yet — ask server for its index and scroll there
            try
            {
                var types = GetSelectedClipTypes();
                var typesQuery = types.Length > 0 ? string.Join("&", types.Select(t => $"types={t}")) : "";
                var url = $"Api/GetClipIndexByDate?date={pickedDate.Value:yyyy-MM-dd}";
                if (!string.IsNullOrEmpty(typesQuery))
                    url += "&" + typesQuery;

                var index = await HttpClient.GetFromNewtonsoftJsonAsync<int>(url);
                if (index >= 0 && index < _totalClipCount)
                {
                    var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
                    var top = (int)(index * EventItemHeight - listBoundingRect.Height / 2 + EventItemHeight / 2);
                    await JsRuntime.InvokeVoidAsync("HTMLElement.prototype.scrollTo.call", _eventsList,
                        new ScrollToOptions { Behavior = "smooth", Top = Math.Max(0, top) });
                }
            }
            catch
            {
                // If the API call fails, ignore — calendar already shows the date
            }
        }
    }

    private async Task ScrollListToActiveClip()
    {
        if (_activeClip == null)
            return;

        // Find index in loaded clips
        var activePath = _activeClip?.DirectoryPath;
        var index = _loadedClips.FirstOrDefault(kvp => kvp.Value.DirectoryPath == activePath).Key;
        if (index == 0 && _loadedClips.Count > 0 && _loadedClips.Values.First().DirectoryPath != activePath)
        {
            // Active clip not found in loaded clips, can't scroll to it
            return;
        }

        var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
        var top = (int)(index * EventItemHeight - listBoundingRect.Height / 2 + EventItemHeight / 2);

        await JsRuntime.InvokeVoidAsync("HTMLElement.prototype.scrollTo.call", _eventsList, new ScrollToOptions
        {
            Behavior = "smooth",
            Top = top
        });
    }

    private void EventListScrolled()
    {
        if (!_scrollDebounceTimer.Enabled)
            _scrollDebounceTimer.Enabled = true;
    }

    private async void ScrollDebounceTimerTick(object _, ElapsedEventArgs __)
    {
        try
        {
            var scrollTop = await JsRuntime.InvokeAsync<double>("getProperty", _eventsList, "scrollTop");
            var listBoundingRect = await _eventsList.MudGetBoundingClientRectAsync();
            var centerScrollPosition = scrollTop + listBoundingRect.Height / 2;
            var itemIndex = (int)(centerScrollPosition / EventItemHeight);

            // Try to find the clip at this index from loaded clips
            if (_loadedClips.TryGetValue(Math.Min(_totalClipCount - 1, itemIndex), out var atClip))
            {
                _ignoreDatePicked = atClip.StartDate.Date;
                await _datePicker.GoToDate(atClip.StartDate.Date);
            }
        }
        catch
        {
            // Ignore errors during debounce
        }
        finally
        {
            _scrollDebounceTimer.Enabled = false;
        }
    }

    private async Task DatePickerOnMouseWheel(WheelEventArgs e)
    {
        if (e.DeltaY == 0 && e.DeltaX == 0 || !_datePicker.PickerMonth.HasValue)
            return;

        var goToNextMonth = e.DeltaY + e.DeltaX * -1 < 0;
        var targetDate = _datePicker.PickerMonth.Value.AddMonths(goToNextMonth ? 1 : -1);
        var endOfMonth = targetDate.AddMonths(1);

        // Check if there are dates in the target month range using available dates
        var hasClipsInOrAfterTargetMonth = _eventDates.Any(d => d >= targetDate);
        var hasClipsInOrBeforeTargetMonth = _eventDates.Any(d => d <= endOfMonth);

        if (goToNextMonth && !hasClipsInOrAfterTargetMonth)
            return;

        if (!goToNextMonth && !hasClipsInOrBeforeTargetMonth)
            return;

        _ignoreDatePicked = targetDate;
        await _datePicker.GoToDate(targetDate);
    }
}
