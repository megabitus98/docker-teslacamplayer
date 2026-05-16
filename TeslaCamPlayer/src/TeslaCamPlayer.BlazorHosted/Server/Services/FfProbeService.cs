using Serilog;
using System.Diagnostics;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public abstract class FfProbeService : IFfProbeService
{
    protected abstract string ExePath { get; }


    public async Task<TimeSpan?> GetVideoFileDurationAsync(string videoFilePath)
    {
        try
        {

            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ExePath)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    Arguments = videoFilePath
                }
            };

            process.Start();
            await process.WaitForExitAsync();
            var output = await process.StandardError.ReadToEndAsync();
            return Helpers.ParseFfProbeOutputHelper.GetDuration(output);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to get video file duration for {Path}", videoFilePath);
            return null;
        }
    }
}

public class FfProbeServiceWindows : FfProbeService
{
    protected override string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "lib", "ffprobe.exe");
}

public class FfProbeServiceDocker : FfProbeService
{
    protected override string ExePath { get; } = "ffprobe";
}

public class FfProbeServiceLinux : FfProbeService
{
    protected override string ExePath { get; } = "ffprobe";
}

/// <summary>
/// Tries native MP4 mvhd parsing first (no process spawn). Falls back to ffprobe for files
/// the parser cannot read (non-MP4 containers, missing moov, malformed atoms).
/// </summary>
public sealed class HybridDurationProbeService : IFfProbeService
{
    private readonly FfProbeService _fallback;

    public HybridDurationProbeService(FfProbeService fallback)
    {
        _fallback = fallback;
    }

    public async Task<TimeSpan?> GetVideoFileDurationAsync(string videoFilePath)
    {
        var native = await Mp4DurationReader.TryReadDurationAsync(videoFilePath).ConfigureAwait(false);
        if (native.HasValue)
            return native;

        Log.Debug("Native MP4 duration parse miss for {Path}, falling back to ffprobe.", videoFilePath);
        return await _fallback.GetVideoFileDurationAsync(videoFilePath).ConfigureAwait(false);
    }
}
