using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class HudRendererService : IHudRendererService
{
    private const string PythonExecutable = "python3";
    private const string HudRendererScript = "/app/teslacamplayer/lib/hud_renderer.py";

    public async Task<string> RenderHudFramesToDirectoryAsync(
        List<SeiMetadata> seiMessages,
        string outputDirectory,
        int width,
        int height,
        double frameRate,
        bool useMph,
        CancellationToken cancellationToken)
    {
        if (seiMessages == null || seiMessages.Count == 0)
        {
            Log.Warning("No SEI messages to render");
            return null;
        }

        Log.Information(
            "Starting HUD rendering to directory: {Count} frames, {Width}x{Height}, {FrameRate} FPS, useMph={UseMph}",
            seiMessages.Count,
            width,
            height,
            frameRate,
            useMph);

        // Create output directory
        Directory.CreateDirectory(outputDirectory);

        // Create temp directory for SEI JSON
        var tempDir = Path.Combine(Path.GetTempPath(), $"hud-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string seiJsonPath = null;
        Process process = null;

        try
        {
            // Serialize SEI messages to JSON file
            seiJsonPath = Path.Combine(tempDir, "sei-messages.json");
            var seiJson = SerializeSeiMessages(seiMessages);
            await File.WriteAllTextAsync(seiJsonPath, seiJson, cancellationToken);
            Log.Debug("Wrote SEI JSON to: {Path}", seiJsonPath);

            // Build Python command arguments
            var args = BuildPythonArgumentsForDirectory(seiJsonPath, outputDirectory, width, height, frameRate, useMph);
            Log.Debug("Python arguments: {Args}", args);

            // Start Python process
            var psi = new ProcessStartInfo
            {
                FileName = PythonExecutable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = psi };

            // Capture stderr for progress/errors
            var stderrBuffer = new StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    stderrBuffer.AppendLine(e.Data);
                    Log.Debug("[HUD Renderer] {Message}", e.Data);
                }
            };

            Log.Information("Starting hud_renderer.py process");
            process.Start();
            process.BeginErrorReadLine();

            // Wait for process to complete
            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                var stderr = stderrBuffer.ToString();
                Log.Error(
                    "HUD renderer process exited with code {ExitCode}. Stderr: {Stderr}",
                    exitCode,
                    stderr);
                throw new Exception($"HUD renderer failed with exit code {exitCode}");
            }

            Log.Information("HUD rendering completed successfully");
            return outputDirectory;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("HUD rendering was cancelled");
            process?.Kill(true);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HUD rendering failed");
            process?.Kill(true);
            throw;
        }
        finally
        {
            process?.Dispose();

            // Clean up temp files
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                    Log.Debug("Cleaned up temp directory: {TempDir}", tempDir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clean up temp directory: {TempDir}", tempDir);
            }
        }
    }

    public async Task RenderHudFramesToPipeAsync(
        List<SeiMetadata> seiMessages,
        Stream outputStream,
        int width,
        int height,
        double frameRate,
        bool useMph,
        CancellationToken cancellationToken)
    {
        if (seiMessages == null || seiMessages.Count == 0)
        {
            Log.Warning("No SEI messages to render");
            return;
        }

        Log.Information(
            "Starting HUD rendering: {Count} frames, {Width}x{Height}, {FrameRate} FPS, useMph={UseMph}",
            seiMessages.Count,
            width,
            height,
            frameRate,
            useMph);

        // Create temp directory for SEI JSON
        var tempDir = Path.Combine(Path.GetTempPath(), $"hud-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string seiJsonPath = null;
        Process process = null;

        try
        {
            // Serialize SEI messages to JSON file
            seiJsonPath = Path.Combine(tempDir, "sei-messages.json");
            var seiJson = SerializeSeiMessages(seiMessages);
            await File.WriteAllTextAsync(seiJsonPath, seiJson, cancellationToken);
            Log.Debug("Wrote SEI JSON to: {Path}", seiJsonPath);

            // Build Python command arguments
            var args = BuildPythonArguments(seiJsonPath, width, height, frameRate, useMph);
            Log.Debug("Python arguments: {Args}", args);

            // Start Python process
            var psi = new ProcessStartInfo
            {
                FileName = PythonExecutable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = new Process { StartInfo = psi };

            // Capture stderr for progress/errors
            var stderrBuffer = new StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    stderrBuffer.AppendLine(e.Data);
                    Log.Debug("[HUD Renderer] {Message}", e.Data);
                }
            };

            Log.Information("Starting hud_renderer.py process");
            process.Start();
            process.BeginErrorReadLine();

            // Copy stdout (frame data) to output stream
            var bufferSize = width * height * 4; // RGBA = 4 bytes/pixel
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(
                outputStream,
                bufferSize,
                cancellationToken);

            // Wait for process to complete
            var processTask = process.WaitForExitAsync(cancellationToken);

            await Task.WhenAll(copyTask, processTask);

            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                var stderr = stderrBuffer.ToString();
                Log.Error(
                    "HUD renderer process exited with code {ExitCode}. Stderr: {Stderr}",
                    exitCode,
                    stderr);
                throw new Exception($"HUD renderer failed with exit code {exitCode}");
            }

            Log.Information("HUD rendering completed successfully");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("HUD rendering was cancelled");
            process?.Kill(true);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HUD rendering failed");
            process?.Kill(true);
            throw;
        }
        finally
        {
            process?.Dispose();

            // Clean up temp files
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                    Log.Debug("Cleaned up temp directory: {TempDir}", tempDir);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to clean up temp directory: {TempDir}", tempDir);
            }
        }
    }

    private string BuildPythonArgumentsForDirectory(
        string seiJsonPath,
        string outputDirectory,
        int width,
        int height,
        double frameRate,
        bool useMph)
    {
        var sb = new StringBuilder();

        sb.Append($"\"{HudRendererScript}\" ");
        sb.Append($"--sei-json \"{seiJsonPath}\" ");
        sb.Append($"--output-dir \"{outputDirectory}\" ");
        sb.Append($"--width {width} ");
        sb.Append($"--height {height} ");
        sb.Append($"--framerate {frameRate.ToString("0.##", CultureInfo.InvariantCulture)} ");
        if (useMph)
        {
            sb.Append("--use-mph");
        }

        return sb.ToString();
    }

    private string BuildPythonArguments(
        string seiJsonPath,
        int width,
        int height,
        double frameRate,
        bool useMph)
    {
        var sb = new StringBuilder();

        sb.Append($"\"{HudRendererScript}\" ");
        sb.Append($"--sei-json \"{seiJsonPath}\" ");
        sb.Append($"--width {width} ");
        sb.Append($"--height {height} ");
        sb.Append($"--framerate {frameRate.ToString("0.##", CultureInfo.InvariantCulture)} ");
        if (useMph)
        {
            sb.Append("--use-mph ");
        }
        sb.Append("--pipe");

        return sb.ToString();
    }

    private string SerializeSeiMessages(List<SeiMetadata> messages)
    {
        // Convert SeiMetadata objects to JSON-friendly format
        var jsonArray = new List<object>();

        foreach (var msg in messages)
        {
            if (msg == null)
            {
                jsonArray.Add(null);
                continue;
            }

            // Convert to camelCase properties for Python compatibility
            var throttlePct = msg.AcceleratorPedalPosition;
            if (throttlePct <= 1.2f)
            {
                throttlePct *= 100f; // some payloads report 0-1 range
            }
            throttlePct = Math.Clamp(throttlePct, 0f, 100f);

            var obj = new
            {
                vehicleSpeedMps = msg.VehicleSpeedMps,
                steeringWheelAngle = msg.SteeringWheelAngle,
                gearState = msg.GearState.ToString(),
                brakeApplied = msg.BrakeApplied,
                throttlePct,
                leftBlinkerOn = msg.BlinkerOnLeft,
                rightBlinkerOn = msg.BlinkerOnRight,
                autopilotState = msg.AutopilotState.ToString(),
                latitude = msg.LatitudeDeg,
                longitude = msg.LongitudeDeg,
                heading = NormalizeHeading(msg.HeadingDeg)
            };

            jsonArray.Add(obj);
        }

        return JsonSerializer.Serialize(jsonArray, new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }

    private static double NormalizeHeading(double headingDeg)
    {
        var normalized = headingDeg % 360.0;
        if (normalized < 0)
        {
            normalized += 360.0;
        }

        return normalized;
    }
}
