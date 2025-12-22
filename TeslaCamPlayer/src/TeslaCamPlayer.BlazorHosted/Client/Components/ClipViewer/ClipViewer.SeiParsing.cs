using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private SeiHud _seiHudRef;
    private bool _showSeiHud = true;
    private bool _seiMetadataAvailable = false;
    private IJSObjectReference _seiParserModule;
    private string _currentSeiHandle;
    private Dictionary<string, string> _seiCache = new();
    private Task _seiInitTask = Task.CompletedTask;

    private async Task InitializeSeiParsingAsync()
    {
        if (_seiParserModule != null)
        {
            return;
        }

        try
        {
            _seiParserModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./js/dashcam/sei-parser-interop.js");

            // Initialize protobuf schema
            await _seiParserModule.InvokeVoidAsync("initializeProtobuf");

            Console.WriteLine("SEI parsing initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SEI parsing initialization failed: {ex.Message}");
        }
    }

    private async Task ParseVideoSeiMetadataAsync(string videoFilePath)
    {
        if (string.IsNullOrEmpty(videoFilePath))
        {
            return;
        }

        await _seiInitTask;

        if (_seiParserModule == null)
        {
            return;
        }

        // Check cache first
        if (_seiCache.TryGetValue(videoFilePath, out var cached))
        {
            _currentSeiHandle = cached;
            _seiMetadataAvailable = true;
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            // Parse SEI metadata directly in JS (handles fetch + decode)
            var result = await _seiParserModule.InvokeAsync<string>(
                "parseVideoSeiFromUrl", videoFilePath);

            if (result != null)
            {
                _currentSeiHandle = result;
                _seiCache[videoFilePath] = result;
                _seiMetadataAvailable = true;
                await InvokeAsync(StateHasChanged);

                Console.WriteLine($"SEI metadata parsed successfully for {videoFilePath}");
            }
            else
            {
                Console.WriteLine($"No SEI metadata found in {videoFilePath}");
                _seiMetadataAvailable = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SEI parsing failed for {videoFilePath}: {ex.Message}");
            _seiMetadataAvailable = false;
        }
    }

    private async Task UpdateHudWithCurrentFrameAsync(double currentTimeSeconds)
    {
        if (_seiHudRef == null || string.IsNullOrEmpty(_currentSeiHandle) || !_showSeiHud)
        {
            return;
        }

        try
        {
            // Get SEI data for current frame based on time
            var seiData = await _seiParserModule.InvokeAsync<object>(
                "getSeiForTime", _currentSeiHandle, currentTimeSeconds);

            if (seiData != null)
            {
                await _seiHudRef.UpdateSeiDataAsync(seiData);
            }
        }
        catch { }
    }

    private Task ToggleSeiHud()
    {
        _showSeiHud = !_showSeiHud;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private Task OnSeiHudVisibilityChanged(bool visible)
    {
        _showSeiHud = visible;
        StateHasChanged();
        return Task.CompletedTask;
    }
}
