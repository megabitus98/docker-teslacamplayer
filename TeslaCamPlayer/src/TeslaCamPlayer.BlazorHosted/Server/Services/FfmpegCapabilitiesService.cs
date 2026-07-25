using System.Diagnostics;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class FfmpegCapabilitiesService : IFfmpegCapabilities
{
    private readonly Lazy<string> _preferred = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public string GetPreferredEncoder() => _preferred.Value;

    private static string Probe()
    {
        foreach (var encoder in ExportEncoder.CandidatesInPriorityOrder)
        {
            // Presence in `ffmpeg -encoders` is not trusted — Ubuntu's build lists h264_nvenc
            // on GPU-less machines — so each candidate runs a real 3-frame null encode.
            if (encoder == ExportEncoder.Software || ProbeEncoder(encoder))
            {
                Log.Information("Export encoder selected: {Encoder}", encoder);
                return encoder;
            }

            Log.Debug("Export encoder probe failed: {Encoder}", encoder);
        }

        return ExportEncoder.Software;
    }

    private static bool ProbeEncoder(string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            foreach (var a in ExportEncoder.GlobalArgs(encoder))
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("color=black:size=64x64:rate=30");
            var suffix = ExportEncoder.FilterSuffix(encoder);
            if (suffix.Length > 0)
            {
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(suffix.TrimStart(','));
            }
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add(encoder);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
