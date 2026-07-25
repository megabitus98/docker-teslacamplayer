using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private bool IsFullscreen => _fullscreenTile.HasValue;

    private string GetTileCss(Tile tile)
    {
        if (_fullscreenTile != tile)
        {
            return null;
        }

        return _isFullscreenPending ? "is-fullscreen is-transition-pending" : "is-fullscreen";
    }

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
        double[] startRect = null;

        if (_tileLookup.TryGetValue(tile, out var initialDefinition))
        {
            var initialRef = initialDefinition.ElementRef;
            if (!initialRef.Equals(default(ElementReference)))
            {
                try
                {
                    startRect = await JsRuntime.InvokeAsync<double[]>("clipViewer.captureTileRect", initialRef);
                }
                catch
                {
                    startRect = null;
                }
            }
        }

        _fullscreenTile = tile;
        _isFullscreenPending = true;
        _pendingFullscreenStartRect = startRect;
        try { await JsRuntime.InvokeVoidAsync("registerEscHandler", _objRef); } catch { }
        await InvokeAsync(StateHasChanged);
        await AwaitUiUpdate();

        if (_tileLookup.TryGetValue(tile, out var definition))
        {
            var tileRef = definition.ElementRef;
            if (!tileRef.Equals(default(ElementReference)))
            {
                try
                {
                    await JsRuntime.InvokeAsync<bool>("clipViewer.animateFullscreenEnter", _gridElement, tileRef, _pendingFullscreenStartRect);
                }
                catch
                {
                    // ignored — fall back to instantaneous layout update
                }
            }
        }

        _isFullscreenPending = false;
        _pendingFullscreenStartRect = null;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ExitFullscreen()
    {
        if (!_fullscreenTile.HasValue)
        {
            return;
        }

        try { await JsRuntime.InvokeVoidAsync("unregisterEscHandler"); } catch { }
        var tileKey = _fullscreenTile.Value;

        if (_tileLookup.TryGetValue(tileKey, out var definition))
        {
            var tileRef = definition.ElementRef;
            if (!tileRef.Equals(default(ElementReference)))
            {
                try
                {
                    await JsRuntime.InvokeAsync<bool>("clipViewer.animateFullscreenExit", _gridElement, tileRef);
                }
                catch
                {
                    // ignored
                }
            }
        }

        _fullscreenTile = null;
        _isFullscreenPending = false;
        await InvokeAsync(StateHasChanged);
        await AwaitUiUpdate();
    }

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
}
