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
    private object _currentVideoSeiFrames;
    private Dictionary<string, object> _seiCache = new();

    private async Task InitializeSeiParsingAsync()
    {
        try
        {
            _seiParserModule = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./js/dashcam/sei-parser-interop.js");

            // Initialize protobuf schema
            await _seiParserModule.InvokeVoidAsync("initializeProtobuf", "/js/dashcam/dashcam.proto");

            Console.WriteLine("SEI parsing initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SEI parsing initialization failed: {ex.Message}");
        }
    }

    private async Task ParseVideoSeiMetadataAsync(string videoFilePath)
    {
        if (_seiParserModule == null || string.IsNullOrEmpty(videoFilePath))
        {
            return;
        }

        // Check cache first
        if (_seiCache.TryGetValue(videoFilePath, out var cached))
        {
            _currentVideoSeiFrames = cached;
            _seiMetadataAvailable = true;
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            // Fetch video file as ArrayBuffer via helper function
            var arrayBuffer = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "fetchVideoAsArrayBuffer", videoFilePath);

            // Parse SEI metadata
            var result = await _seiParserModule.InvokeAsync<object>(
                "parseVideoSei", arrayBuffer);

            if (result != null)
            {
                _currentVideoSeiFrames = result;
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
        if (_seiHudRef == null || _currentVideoSeiFrames == null || !_showSeiHud)
        {
            return;
        }

        try
        {
            // Get SEI data for current frame based on time
            var seiData = await _seiParserModule.InvokeAsync<object>(
                "getSeiForTime", _currentVideoSeiFrames, currentTimeSeconds);

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
