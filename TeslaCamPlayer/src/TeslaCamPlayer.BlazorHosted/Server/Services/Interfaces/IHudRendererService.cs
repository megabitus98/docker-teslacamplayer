using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IHudRendererService
{
    /// <summary>
    /// Renders HUD frames to a directory as PNG files for FFmpeg consumption
    /// </summary>
    /// <param name="seiMessages">SEI telemetry messages</param>
    /// <param name="outputDirectory">Directory to write PNG frames to</param>
    /// <param name="width">Frame width in pixels</param>
    /// <param name="height">Frame height in pixels</param>
    /// <param name="frameRate">Frame rate (FPS)</param>
    /// <param name="useMph">Use MPH for speed display (false = km/h)</param>
    /// <param name="locationStreetCity">Street and city text from event.json</param>
    /// <param name="fallbackLat">Fallback GPS latitude from event.json</param>
    /// <param name="fallbackLon">Fallback GPS longitude from event.json</param>
    /// <param name="renderLocationOverlay">Whether to render the location overlay (city/GPS)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the output directory</returns>
    Task<string> RenderHudFramesToDirectoryAsync(
        List<SeiMetadata> seiMessages,
        string outputDirectory,
        int width,
        int height,
        double frameRate,
        bool useMph,
        string locationStreetCity,
        double? fallbackLat,
        double? fallbackLon,
        bool renderLocationOverlay,
        CancellationToken cancellationToken);
}
