using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class SeiHudFilterBuilder
{
    private readonly ISettingsProvider _settingsProvider;

    public SeiHudFilterBuilder(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    public string GenerateSeiSubtitleFile(
        List<SeiMetadata> messages,
        double frameRate,
        string outputPath)
    {
        if (messages == null || messages.Count == 0)
        {
            Log.Warning("No SEI messages to generate subtitle file");
            return null;
        }

        var srt = new StringBuilder();
        var validMessageIndex = 0;

        for (int i = 0; i < messages.Count; i++)
        {
            var sei = messages[i];
            if (sei == null) continue;

            validMessageIndex++;
            var startTime = TimeSpan.FromSeconds(i / frameRate);
            var endTime = TimeSpan.FromSeconds((i + 1) / frameRate);

            // SRT subtitle entry
            srt.AppendLine(validMessageIndex.ToString());
            srt.AppendLine($"{FormatSrtTime(startTime)} --> {FormatSrtTime(endTime)}");

            // Format telemetry data
            var speedUnit = _settingsProvider.Settings.SpeedUnit;
            var useMph = speedUnit == "mph";
            var speedMph = sei.VehicleSpeedMps * 2.23694;
            var speed = useMph ? speedMph : speedMph * 1.60934;
            var unit = useMph ? "mph" : "km/h";
            srt.AppendLine($"Speed: {speed:F1} {unit}");
            srt.AppendLine($"Gear: {FormatGear(sei.GearState)}");

            if (sei.AutopilotState != SeiMetadata.Types.AutopilotState.None)
            {
                srt.AppendLine($"Autopilot: {FormatAutopilot(sei.AutopilotState)}");
            }

            srt.AppendLine($"Steering: {sei.SteeringWheelAngle:F1}°");

            if (sei.BrakeApplied)
            {
                srt.AppendLine("[BRAKE]");
            }

            // Blank line between entries
            srt.AppendLine();
        }

        try
        {
            File.WriteAllText(outputPath, srt.ToString());
            Log.Information("Generated SEI subtitle file: {Path} with {Count} entries",
                outputPath, validMessageIndex);
            return outputPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write SRT subtitle file to {Path}", outputPath);
            return null;
        }
    }

    private string FormatSrtTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
    }

    private string FormatGear(SeiMetadata.Types.Gear gear)
    {
        return gear switch
        {
            SeiMetadata.Types.Gear.Drive => "D",
            SeiMetadata.Types.Gear.Reverse => "R",
            SeiMetadata.Types.Gear.Park => "P",
            SeiMetadata.Types.Gear.Neutral => "N",
            _ => "?"
        };
    }

    private string FormatAutopilot(SeiMetadata.Types.AutopilotState state)
    {
        return state switch
        {
            SeiMetadata.Types.AutopilotState.SelfDriving => "FSD",
            SeiMetadata.Types.AutopilotState.Autosteer => "Autosteer",
            SeiMetadata.Types.AutopilotState.Tacc => "TACC",
            _ => "Off"
        };
    }
}
