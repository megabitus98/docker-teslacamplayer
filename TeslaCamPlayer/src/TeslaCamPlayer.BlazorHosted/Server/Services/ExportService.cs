using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class ExportService : IExportService
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly IClipsService _clipsService;

    private readonly ConcurrentDictionary<string, ExportStatus> _status = new();
    private readonly ConcurrentDictionary<string, string> _outputs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    public ExportService(ISettingsProvider settingsProvider, IClipsService clipsService)
    {
        _settingsProvider = settingsProvider;
        _clipsService = clipsService;
    }

    public Task<string> StartExportAsync(ExportRequest request)
    {
        var jobId = Guid.NewGuid().ToString("N");
        _status[jobId] = new ExportStatus
        {
            JobId = jobId,
            State = ExportState.Pending,
            Percent = 0
        };

        var cts = new CancellationTokenSource();
        _cancellations[jobId] = cts;

        _ = Task.Run(async () => await RunExportAsync(jobId, request, cts.Token));
        return Task.FromResult(jobId);
    }

    public ExportStatus GetStatus(string jobId)
    {
        return _status.TryGetValue(jobId, out var st) ? st : null;
    }

    public bool TryGetOutputPath(string jobId, out string path)
    {
        return _outputs.TryGetValue(jobId, out path);
    }

    public bool Cancel(string jobId)
    {
        if (_cancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task RunExportAsync(string jobId, ExportRequest request, CancellationToken cancel)
    {
        try
        {
            if (_status.TryGetValue(jobId, out var current))
            {
                current.State = ExportState.Running;
                _status[jobId] = current;
            }

            // Validate request
            if (request.EndTimeUtc <= request.StartTimeUtc)
                throw new InvalidOperationException("End time must be after start time.");

            var clip = (await _clipsService.GetClipsAsync(false))
                .FirstOrDefault(c => string.Equals(c.DirectoryPath, request.ClipDirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (clip == null)
                throw new InvalidOperationException("Clip not found.");

            // Ensure selection is within clip bounds
            var start = request.StartTimeUtc;
            var end = request.EndTimeUtc;
            if (start < clip.StartDate) start = clip.StartDate;
            if (end > clip.EndDate) end = clip.EndDate;
            if (end <= start)
                throw new InvalidOperationException("Selected interval is outside clip range.");

            // Build per-camera lists of segment parts
            var byCamera = new Dictionary<Cameras, List<(string path, double start, double duration)>>();
            foreach (var cam in request.OrderedCameras)
            {
                byCamera[cam] = new();
            }

            foreach (var seg in clip.Segments)
            {
                var segStart = seg.StartDate;
                var segEnd = seg.EndDate;
                var overlapStart = segStart > start ? segStart : start;
                var overlapEnd = segEnd < end ? segEnd : end;
                if (overlapEnd <= overlapStart)
                    continue;

                foreach (var cam in request.OrderedCameras)
                {
                    var vf = CameraToFile(seg, cam);
                    if (vf == null) continue;
                    var startOffset = (overlapStart - segStart).TotalSeconds;
                    var dur = (overlapEnd - overlapStart).TotalSeconds;
                    byCamera[cam].Add((vf.FilePath, startOffset, dur));
                }
            }

            // Build ffmpeg command
            var exportDir = _settingsProvider.Settings.ExportRootPath;
            Directory.CreateDirectory(exportDir);

            var ext = SanitizeFormat(request.Format);
            var outputFile = Path.Combine(exportDir, jobId + "." + ext);

            // Build argv tokens for ffmpeg. Use ArgumentList to avoid shell quoting issues.
            var argv = new List<string>();
            argv.Add("-y");
            argv.Add("-hide_banner");
            argv.Add("-nostdin");
            argv.Add("-progress");
            argv.Add("pipe:1");

            // Inputs: for each camera part, add -ss -t -i file
            var inputIndexMap = new Dictionary<(Cameras cam, int partIndex), int>();
            var globalInputIndex = 0;
            foreach (var cam in request.OrderedCameras)
            {
                var parts = byCamera[cam];
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    argv.Add("-ss");
                    argv.Add(FormatTimeArg(p.start));
                    argv.Add("-t");
                    argv.Add(FormatTimeArg(p.duration));
                    argv.Add("-i");
                    argv.Add(p.path);
                    inputIndexMap[(cam, i)] = globalInputIndex++;
                }
            }

            // Filtergraph
            var filter = new StringBuilder();
            var camOutputs = new List<string>();

            // Determine output resolution and grid
            var visibleCamCount = request.OrderedCameras.Count;
            var cols = Math.Max(1, request.GridColumns);
            var rows = (int)Math.Ceiling((double)visibleCamCount / cols);

            int outW = request.Width ?? 1920;
            int outH = request.Height ?? 1080;

            int cellW = outW / cols;
            int cellH = outH / rows;

            var labelFont = ":fontcolor=white:fontsize=20:box=1:boxcolor=black@0.4:x=10:y=8";

            foreach (var cam in request.OrderedCameras)
            {
                var parts = byCamera[cam];
                if (parts.Count == 0)
                {
                    // create a solid black tile if no input available to keep layout consistent
                    var dur = (end - start).TotalSeconds;
                    var nullLbl = $"color=c=black:size={cellW}x{cellH}:duration={FormatTimeArg(dur)}[color_{cam}]";
                    filter.Append(nullLbl).Append(';');
                    camOutputs.Add($"[color_{cam}]");
                    continue;
                }

                // Concat parts for this camera
                var inputs = new List<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    var idx = inputIndexMap[(cam, i)];
                    inputs.Add($"[{idx}:v]");
                }

                var concatOut = $"[{cam}_concat]";
                if (inputs.Count == 1)
                {
                    filter.Append(string.Join(string.Empty, inputs)).Append("setpts=PTS-STARTPTS").Append(concatOut).Append(';');
                }
                else
                {
                    filter.Append(string.Join(string.Empty, inputs))
                          .Append($"concat=n={inputs.Count}:v=1:a=0")
                          .Append(concatOut)
                          .Append(';');
                }

                var scaled = $"[{cam}_scaled]";
                filter.Append(concatOut)
                      .Append($"scale={cellW}:{cellH}:force_original_aspect_ratio=decrease,pad={cellW}:{cellH}:(ow-iw)/2:(oh-ih)/2")
                      .Append(scaled)
                      .Append(';');

                var final = scaled;
                if (request.IncludeCameraLabels)
                {
                    var labelText = CameraLabel(cam);
                    var labeled = $"[{cam}_labeled]";
                    filter.Append(scaled)
                          .Append($"drawtext=text='{EscapeDrawText(labelText)}'{labelFont}")
                          .Append(labeled)
                          .Append(';');
                    final = labeled;
                }

                camOutputs.Add(final);
            }

            // xstack layout positions
            if (camOutputs.Count > 1)
            {
                var layouts = new List<string>();
                for (int i = 0; i < camOutputs.Count; i++)
                {
                    int r = i / cols;
                    int c = i % cols;
                    int x = c * cellW;
                    int y = r * cellH;
                    layouts.Add($"{x}_{y}");
                }

                filter.Append(string.Join(string.Empty, camOutputs))
                      .Append($"xstack=inputs={camOutputs.Count}:layout={string.Join('|', layouts)}[stacked]");
            }
            else
            {
                filter.Append(camOutputs[0]).Append("copy[stacked]");
            }

            // Optional overlays (location bottom-left, timestamp bottom-right)
            string finalLabel = "stacked";

            if (request.IncludeLocationOverlay)
            {
                string BuildLocationText()
                {
                    try
                    {
                        var evt = clip.Event;
                        if (evt == null) return null;
                        var city = (evt.City ?? string.Empty).Trim();
                        var latStr = (evt.EstLat ?? string.Empty).Trim();
                        var lonStr = (evt.EstLon ?? string.Empty).Trim();
                        string coords = null;
                        if (!string.IsNullOrWhiteSpace(latStr) && !string.IsNullOrWhiteSpace(lonStr))
                        {
                            if (double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat) &&
                                double.TryParse(lonStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                                coords = $"{lat:0.#####}, {lon:0.#####}";
                            else
                                coords = $"{latStr}, {lonStr}";
                        }
                        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(coords)) return $"{city} ({coords})";
                        if (!string.IsNullOrWhiteSpace(city)) return city;
                        if (!string.IsNullOrWhiteSpace(coords)) return coords;
                        return null;
                    }
                    catch { return null; }
                }

                var locationText = BuildLocationText();
                if (!string.IsNullOrWhiteSpace(locationText))
                {
                    var geo = "[geo]";
                    var locFont = ":fontcolor=white:fontsize=24:box=1:boxcolor=black@0.4";
                    filter.Append(';')
                          .Append('[').Append(finalLabel).Append(']')
                          .Append($"drawtext=text='{EscapeDrawText(locationText)}'{locFont}:x=10:y=h-th-10")
                          .Append(geo);
                    finalLabel = "geo";
                }
            }

            // Timestamp overlay on final output if requested
            if (request.IncludeTimestamp)
            {
                var startEpoch = new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeSeconds();
                var ts = "[ts]";
                var tsDrawText = $@"setpts=PTS-STARTPTS,drawtext=text='%{{pts\:localtime\:{startEpoch}\:%Y-%m-%d %X}}':fontcolor=white:fontsize=24:box=1:boxcolor=black@0.4:x=w-tw-10:y=h-th-10";
                filter.Append(';')
                      .Append('[').Append(finalLabel).Append(']')
                      .Append(tsDrawText)
                      .Append(ts);
                finalLabel = "ts";
            }

            argv.Add("-filter_complex");
            argv.Add(filter.ToString());

            // map final
            argv.Add("-map"); argv.Add($"[{finalLabel}]");

            // No audio
            argv.Add("-an");

            // Codec / container options
            AddCodecArgs(argv, request);

            // Embed metadata: creation_time and simple title/comment with event time
            try
            {
                var eventTime = clip.Event?.Timestamp ?? request.StartTimeUtc;
                var utc = eventTime.ToUniversalTime().ToString("o");
                argv.Add("-metadata"); argv.Add($"title=TeslaCamPlayer Export");
                argv.Add("-metadata"); argv.Add($"comment=EventTimeUTC={utc}");
                argv.Add("-metadata"); argv.Add($"creation_time={utc}");
            }
            catch { }

            argv.Add(outputFile);

            var totalSeconds = (end - start).TotalSeconds;

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in argv)
            {
                psi.ArgumentList.Add(a);
            }

            string QuoteLog(string s)
                => string.IsNullOrEmpty(s) ? s : (s.Any(char.IsWhiteSpace) ? $"\"{s}\"" : s);

            Log.Information("Starting export {JobId}: ffmpeg {Args}", jobId, string.Join(' ', argv.Select(QuoteLog)));

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var sw = Stopwatch.StartNew();
            proc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                try
                {
                    // Parse progress lines like: out_time_ms=...
                    var line = e.Data.Trim();
                    if (line.StartsWith("out_time_ms="))
                    {
                        var msStr = line.Substring("out_time_ms=".Length);
                        if (double.TryParse(msStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var outMs))
                        {
                            var sec = outMs / 1000000.0;
                            var pct = Math.Clamp(totalSeconds > 0 ? (sec / totalSeconds) * 100.0 : 0, 0, 100);
                            var eta = totalSeconds > 0 ? TimeSpan.FromSeconds(Math.Max(0, totalSeconds - sec)) : (TimeSpan?)null;
                            _status[jobId] = new ExportStatus
                            {
                                JobId = jobId,
                                State = ExportState.Running,
                                Percent = pct,
                                Eta = eta
                            };
                        }
                    }
                }
                catch { }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                // Keep for debugging visibility
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Log.Debug("ffmpeg[{JobId}] {Line}", jobId, e.Data);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (cancel.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }))
            {
                await proc.WaitForExitAsync();
            }

            if (cancel.IsCancellationRequested)
            {
                _status[jobId] = new ExportStatus { JobId = jobId, State = ExportState.Canceled, Percent = 0 };
                SafeDelete(outputFile);
                return;
            }

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {proc.ExitCode}");

            var url = BuildDownloadUrl(outputFile);
            _outputs[jobId] = outputFile;
            _status[jobId] = new ExportStatus
            {
                JobId = jobId,
                State = ExportState.Completed,
                Percent = 100,
                OutputUrl = url
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export {JobId} failed", jobId);
            _status[jobId] = new ExportStatus
            {
                JobId = jobId,
                State = ExportState.Failed,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (_cancellations.TryRemove(jobId, out var c)) c.Dispose();
        }
    }

    private static string CameraLabel(Cameras cam)
        => cam switch
        {
            Cameras.Front => "Front",
            Cameras.Back => "Back",
            Cameras.LeftRepeater => "Left Repeater",
            Cameras.RightRepeater => "Right Repeater",
            Cameras.LeftBPillar => "Left Pillar",
            Cameras.RightBPillar => "Right Pillar",
            _ => cam.ToString()
        };

    private static string EscapePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        if (p.Contains(' ')) return $"\"{p}\"";
        return p;
    }

    private static string EscapeDrawText(string text)
        => text.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'");

    private static string FormatTimeArg(double seconds)
        => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string SanitizeFormat(string fmt)
    {
        fmt = (fmt ?? "").Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp4" => "mp4",
            "mov" => "mov",
            _ => "mp4"
        };
    }

    private static void AddCodecArgs(List<string> args, ExportRequest request)
    {
        var fmt = SanitizeFormat(request.Format);
        switch (fmt)
        {
            case "mp4":
            case "mov":
                args.Add("-c:v"); args.Add("libx264");
                args.Add("-pix_fmt"); args.Add("yuv420p");
                var (preset, crf) = QualityToPresetCrf(request.Quality);
                args.Add("-preset"); args.Add(preset);
                args.Add("-crf"); args.Add(crf);
                if (fmt == "mp4")
                {
                    args.Add("-movflags"); args.Add("+faststart");
                }
                break;
        }
    }

    private static (string preset, string crf) QualityToPresetCrf(string q)
    {
        switch ((q ?? "").ToLowerInvariant())
        {
            case "high": return ("slow", "17");
            case "low": return ("fast", "24");
            default: return ("medium", "20");
        }
    }

    private static string QualityToQscale(string q)
        => (q ?? "").ToLowerInvariant() switch
        {
            "high" => "3",
            "low" => "7",
            _ => "5"
        };

    private string BuildDownloadUrl(string outputFile)
    {
        try
        {
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var full = Path.GetFullPath(outputFile);
            var root = Path.GetFullPath(_settingsProvider.Settings.ExportRootPath);
            if (full.StartsWith(Path.GetFullPath(Path.Combine(wwwroot, "exports"))))
            {
                return "/exports/" + Path.GetFileName(outputFile);
            }

            // Fallback to API served download
            return $"/Api/ExportFile?path={Uri.EscapeDataString(full)}";
        }
        catch
        {
            return "/" + Path.GetFileName(outputFile);
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static VideoFile CameraToFile(ClipVideoSegment seg, Cameras cam)
        => cam switch
        {
            Cameras.Front => seg.CameraFront,
            Cameras.Back => seg.CameraBack,
            Cameras.LeftRepeater => seg.CameraLeftRepeater,
            Cameras.RightRepeater => seg.CameraRightRepeater,
            Cameras.LeftBPillar => seg.CameraLeftBPillar,
            Cameras.RightBPillar => seg.CameraRightBPillar,
            _ => null
        };
}
