using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
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
