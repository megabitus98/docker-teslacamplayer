using Microsoft.AspNetCore.SignalR;
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
using TeslaCamPlayer.BlazorHosted.Server.Hubs;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class ExportService : IExportService
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly IClipsService _clipsService;
    private readonly IHubContext<StatusHub> _hubContext;
    private readonly ISeiParserService _seiParser;
    private readonly IHudRendererService _hudRenderer;
    private readonly IMp4TimingService _mp4Timing;

    private readonly ConcurrentDictionary<string, ExportStatus> _status = new();
    private readonly ConcurrentDictionary<string, string> _outputs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    private static ExportStatus CloneStatus(ExportStatus status)
        => status == null
            ? null
            : new ExportStatus
            {
                JobId = status.JobId,
                State = status.State,
                Percent = status.Percent,
                Eta = status.Eta,
                OutputUrl = status.OutputUrl,
                ErrorMessage = status.ErrorMessage
            };

    private void BroadcastStatus(string jobId, ExportStatus status, string reason)
    {
        if (string.IsNullOrWhiteSpace(jobId) || status == null)
        {
            return;
        }

        status.JobId ??= jobId;

        var snapshot = CloneStatus(status);
        snapshot.JobId = jobId;
        _status[jobId] = snapshot;

        if (string.Equals(reason, "progress", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug(
                "Broadcasting export progress. JobId={JobId}, Percent={Percent:F2}, Eta={Eta}",
                jobId,
                snapshot.Percent,
                snapshot.Eta);
        }
        else
        {
            Log.Information(
                "Broadcasting export status change. JobId={JobId}, State={State}, Percent={Percent:F2}, Reason={Reason}",
                jobId,
                snapshot.State,
                snapshot.Percent,
                reason);
        }

        var jobGroup = StatusHub.GetExportGroupName(jobId);
        var allGroup = StatusHub.AllExportsGroupName;

        var broadcastTask = Task.WhenAll(
            _hubContext.Clients.Group(jobGroup).SendAsync("ExportStatusUpdated", snapshot),
            _hubContext.Clients.Group(allGroup).SendAsync("ExportStatusUpdated", snapshot));

        _ = broadcastTask.ContinueWith(
            t => Log.Error(t.Exception, "Failed to broadcast export status update. JobId={JobId}, Reason={Reason}", jobId, reason),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public ExportService(ISettingsProvider settingsProvider, IClipsService clipsService, IHubContext<StatusHub> hubContext, ISeiParserService seiParser, IHudRendererService hudRenderer, IMp4TimingService mp4Timing)
    {
        _settingsProvider = settingsProvider;
        _clipsService = clipsService;
        _hubContext = hubContext;
        _seiParser = seiParser;
        _hudRenderer = hudRenderer;
        _mp4Timing = mp4Timing;
    }

    public Task<string> StartExportAsync(ExportRequest request)
    {
        var jobId = Guid.NewGuid().ToString("N");
        BroadcastStatus(jobId, new ExportStatus
        {
            JobId = jobId,
            State = ExportState.Pending,
            Percent = 0
        }, "pending");

        var cts = new CancellationTokenSource();
        _cancellations[jobId] = cts;

        _ = Task.Run(async () => await RunExportAsync(jobId, request, cts.Token));
        return Task.FromResult(jobId);
    }

    public ExportStatus GetStatus(string jobId)
    {
        return _status.TryGetValue(jobId, out var st) ? CloneStatus(st) : null;
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
        string srtPath = null;
        string hudFramesDir = null;
        try
        {
            if (_status.TryGetValue(jobId, out var current))
            {
                current.State = ExportState.Running;
                BroadcastStatus(jobId, current, "running");
            }
            else
            {
                BroadcastStatus(jobId, new ExportStatus
                {
                    JobId = jobId,
                    State = ExportState.Running,
                    Percent = 0
                }, "running");
            }

            // Validate request
            if (request.EndTimeUtc <= request.StartTimeUtc)
                throw new InvalidOperationException("End time must be after start time.");

            var clip = (await _clipsService.GetClipsAsync(false))
                .FirstOrDefault(c => string.Equals(c.DirectoryPath, request.ClipDirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (clip == null)
                throw new InvalidOperationException("Clip not found.");

            var locationDescription = clip.Event?.GetLocationDescription();

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
                    argv.Add("-accurate_seek");
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
                      .Append($"xstack=inputs={camOutputs.Count}:layout={string.Join('|', layouts)}[stacked_tmp]");
            }
            else
            {
                filter.Append(camOutputs[0]).Append("copy[stacked_tmp]");
            }

            // Force constant frame rate for precise sync
            filter.Append(';')
                  .Append("[stacked_tmp]")
                  .Append("fps=30,setpts=N/(30*TB)")
                  .Append("[stacked]");

            // Optional overlays (location bottom-left, timestamp bottom-right)
            string finalLabel = "stacked";

            if (request.IncludeLocationOverlay)
            {
                var locationText = locationDescription;
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

            // SEI HUD overlay (if requested)
            if (request.IncludeSeiHud)
            {
                // Find front camera segment info for SEI extraction using MP4 frame timing
                if (byCamera.ContainsKey(Cameras.Front) && byCamera[Cameras.Front].Count > 0)
                {
                    const double seiFrameRate = 30.0; // HUD rendering frame rate
                    var seiTimeline = new List<(double timeSeconds, SeiMetadata message)>();
                    var frontSegments = byCamera[Cameras.Front];
                    var exportDurationSeconds = (end - start).TotalSeconds;

                    Log.Information(
                        "SEI HUD sync: Processing {SegmentCount} front camera segments using MP4 frame timing",
                        frontSegments.Count);

                    double cumulativeExportSeconds = 0;
                    ulong? lastFrameSeqNo = null;
                    double? lastLat = null, lastLon = null;
                    float? lastSpeed = null;
                    int segmentIndex = 0;

                    foreach (var segment in frontSegments)
                    {
                        // Extract MP4 frame timing metadata
                        var timeline = await _mp4Timing.GetFrameTimelineAsync(segment.path);
                        if (timeline == null)
                        {
                            Log.Warning("Failed to extract MP4 timing for {Path}, skipping SEI extraction", segment.path);
                            segmentIndex++;
                            continue;
                        }

                        // Extract ALL SEI messages from this segment
                        var allMessages = _seiParser.ExtractSeiMessages(segment.path);
                        if (allMessages.Count == 0)
                        {
                            Log.Warning("No SEI metadata found in front segment {Path}", segment.path);
                            segmentIndex++;
                            continue;
                        }

                        // Validate timeline matches SEI message count
                        if (timeline.FrameCount != allMessages.Count)
                        {
                            Log.Warning(
                                "MP4 frame count ({FrameCount}) != SEI message count ({SeiCount}) for {Path}. Using min for safety.",
                                timeline.FrameCount, allMessages.Count, segment.path);

                            // Trim SEI messages if MP4 has fewer frames
                            if (timeline.FrameCount < allMessages.Count)
                            {
                                allMessages = allMessages.GetRange(0, timeline.FrameCount);
                            }
                        }

                        var startMs = segment.start * 1000.0;
                        var endMs = (segment.start + segment.duration) * 1000.0;
                        var startFrameIndex = timeline.FindFrameIndexForMs(startMs);
                        var endFrameIndex = timeline.FindFrameIndexForMs(endMs);

                        if (startFrameIndex < 0 || endFrameIndex < 0)
                        {
                            Log.Warning(
                                "Frame indices not found for SEI extraction: start={StartMs:F2}ms end={EndMs:F2}ms for {Path}",
                                startMs, endMs, segment.path);
                            segmentIndex++;
                            continue;
                        }

                        startFrameIndex = Math.Max(0, startFrameIndex);
                        endFrameIndex = Math.Min(
                            Math.Min(endFrameIndex, timeline.FrameCount - 1),
                            allMessages.Count - 1);

                        if (endFrameIndex < startFrameIndex)
                        {
                            Log.Warning("Invalid frame range for SEI extraction: [{Start}..{End}] for {Path}",
                                startFrameIndex, endFrameIndex, segment.path);
                            segmentIndex++;
                            continue;
                        }

                        var segmentFrameCount = endFrameIndex - startFrameIndex + 1;
                        var segmentSeiMessages = allMessages.GetRange(startFrameIndex, segmentFrameCount);
                        var framesAdded = 0;

                        for (int i = 0; i < segmentSeiMessages.Count; i++)
                        {
                            var globalFrameIndex = startFrameIndex + i;
                            if (globalFrameIndex >= timeline.FrameStartsMs.Length)
                            {
                                break;
                            }

                            var frameStartMs = timeline.FrameStartsMs[globalFrameIndex];
                            var exportRelativeSeconds = cumulativeExportSeconds + (Math.Max(0, frameStartMs - startMs) / 1000.0);
                            seiTimeline.Add((exportRelativeSeconds, segmentSeiMessages[i]));
                            framesAdded++;
                        }

                        if (framesAdded != segmentFrameCount)
                        {
                            Log.Warning(
                                "SEI frame mismatch for {Path}: expected {Expected} frames from timeline, added {Added}",
                                segment.path,
                                segmentFrameCount,
                                framesAdded);
                        }

                        // Diagnostic: Check SEI continuity across segment boundaries
                        if (segmentSeiMessages.Count > 0)
                        {
                            var firstSei = segmentSeiMessages[0];
                            var lastSei = segmentSeiMessages[segmentSeiMessages.Count - 1];

                            if (lastFrameSeqNo.HasValue)
                            {
                                var seqGap = (long)(firstSei.FrameSeqNo - lastFrameSeqNo.Value);
                                var expectedGap = 1L; // Should increment by 1

                                if (seqGap != expectedGap)
                                {
                                    Log.Warning(
                                        "SEI boundary discontinuity: Segment {SegmentIndex}, Expected FrameSeqNo={Expected}, Actual={Actual}, Gap={Gap}",
                                        segmentIndex,
                                        lastFrameSeqNo.Value + 1,
                                        firstSei.FrameSeqNo,
                                        seqGap);
                                }

                                // Check for backward GPS movement (would indicate wrong SEI data)
                                if (lastLat.HasValue && lastLon.HasValue)
                                {
                                    var latDiff = Math.Abs(firstSei.LatitudeDeg - lastLat.Value);
                                    var lonDiff = Math.Abs(firstSei.LongitudeDeg - lastLon.Value);

                                    // Rough distance calculation (degrees to km approximation)
                                    var distanceKm = Math.Sqrt(latDiff * latDiff + lonDiff * lonDiff) * 111.0;

                                    if (distanceKm > 1.0) // More than 1km jump at boundary
                                    {
                                        Log.Warning(
                                            "Large GPS jump at boundary: {Distance:F3}km from ({LastLat:F6},{LastLon:F6}) to ({CurrLat:F6},{CurrLon:F6})",
                                            distanceKm,
                                            lastLat.Value, lastLon.Value,
                                            firstSei.LatitudeDeg, firstSei.LongitudeDeg);
                                    }
                                }

                                // Check for speed backward jump
                                if (lastSpeed.HasValue && firstSei.VehicleSpeedMps < lastSpeed.Value - 5.0f)
                                {
                                    Log.Warning(
                                        "Speed backward jump at boundary: {LastSpeed:F1} m/s → {CurrSpeed:F1} m/s",
                                        lastSpeed.Value,
                                        firstSei.VehicleSpeedMps);
                                }
                            }

                            lastFrameSeqNo = lastSei.FrameSeqNo;
                            lastLat = lastSei.LatitudeDeg;
                            lastLon = lastSei.LongitudeDeg;
                            lastSpeed = lastSei.VehicleSpeedMps;
                        }

                        Log.Information(
                            "SEI segment {SegmentIndex}: File={FileName}, FileRelativeTime=[{Start:F2}s + {Duration:F2}s], ExportPosition={ExportPos:F2}s, SEI extracted={Count}, FrameSeqNo=[{FirstSeq}..{LastSeq}], Speed=[{FirstSpeed:F1}..{LastSpeed:F1}] m/s",
                            segmentIndex,
                            Path.GetFileName(segment.path),
                            segment.start,
                            segment.duration,
                            cumulativeExportSeconds,
                            segmentSeiMessages.Count,
                            segmentSeiMessages.Count > 0 ? segmentSeiMessages[0].FrameSeqNo : 0,
                            segmentSeiMessages.Count > 0 ? segmentSeiMessages[segmentSeiMessages.Count - 1].FrameSeqNo : 0,
                            segmentSeiMessages.Count > 0 ? segmentSeiMessages[0].VehicleSpeedMps : 0,
                            segmentSeiMessages.Count > 0 ? segmentSeiMessages[segmentSeiMessages.Count - 1].VehicleSpeedMps : 0);

                        cumulativeExportSeconds += segment.duration;
                        segmentIndex++;
                    }

                    if (seiTimeline.Count > 0)
                    {
                        // Resample SEI timeline to match export FPS so HUD duration matches video duration
                        var resampledSeiMessages = ResampleSeiMessages(seiTimeline, seiFrameRate, exportDurationSeconds);
                        var timelineDurationSeconds = Math.Max(0, seiTimeline[seiTimeline.Count - 1].timeSeconds - seiTimeline[0].timeSeconds);
                        var resampledDurationSeconds = resampledSeiMessages.Count / seiFrameRate;

                        Log.Information(
                            "SEI HUD sync complete: Segments={SegmentCount}, Raw frames={RawCount}, Expected duration={ExpectedDuration:F2}s, Timeline duration={TimelineDuration:F2}s, Resampled duration={ResampledDuration:F2}s",
                            frontSegments.Count,
                            seiTimeline.Count,
                            exportDurationSeconds,
                            timelineDurationSeconds,
                            resampledDurationSeconds);

                        // Render HUD frames to temp directory
                        hudFramesDir = Path.Combine(exportDir, $"{jobId}_hud_frames");
                        var useMph = _settingsProvider.Settings.SpeedUnit == "mph";

                        await _hudRenderer.RenderHudFramesToDirectoryAsync(
                            resampledSeiMessages,
                            hudFramesDir,
                            outW,
                            outH,
                            seiFrameRate,
                            useMph,
                            cancel);

                        // Add HUD frames as FFmpeg input
                        var hudInputIndex = globalInputIndex;
                        argv.Add("-framerate");
                        argv.Add(seiFrameRate.ToString("0.##", CultureInfo.InvariantCulture));
                        argv.Add("-i");
                        argv.Add(Path.Combine(hudFramesDir, "frame_%06d.png"));
                        globalInputIndex++;

                        // Prepare HUD stream with precise timing
                        var hudSyncOut = "[hud_sync]";
                        filter.Append(';')
                              .Append($"[{hudInputIndex}:v]")
                              .Append("fps=30,setpts=N/(30*TB)")
                              .Append(hudSyncOut);

                        // Overlay HUD on video using overlay filter with shortest option
                        var hudOverlayOut = "[hud_overlay]";
                        filter.Append(';')
                              .Append('[').Append(finalLabel).Append(']')
                              .Append(hudSyncOut)
                              .Append("overlay=0:0:shortest=1")
                              .Append(hudOverlayOut);
                        finalLabel = "hud_overlay";

                        Log.Information("Graphical SEI HUD overlay added to export {JobId} ({Count} frames)", jobId, resampledSeiMessages.Count);
                    }
                    else
                    {
                        Log.Warning("No SEI metadata found in front camera for export {JobId}", jobId);
                    }
                }
                else
                {
                    Log.Warning("SEI HUD requested but no front camera available for export {JobId}", jobId);
                }
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

                // Build comment with EventTimeUTC, Location, and EventPath
                var commentParts = new List<string> { $"EventTimeUTC={utc}" };
                if (!string.IsNullOrWhiteSpace(locationDescription))
                {
                    commentParts.Add($"Location={locationDescription}");
                }
                if (!string.IsNullOrWhiteSpace(request.ClipDirectoryPath))
                {
                    commentParts.Add($"EventPath={request.ClipDirectoryPath}");
                }
                argv.Add("-metadata"); argv.Add($"comment={string.Join("; ", commentParts)}");

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
                            BroadcastStatus(jobId, new ExportStatus
                            {
                                JobId = jobId,
                                State = ExportState.Running,
                                Percent = pct,
                                Eta = eta
                            }, "progress");
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
                BroadcastStatus(jobId, new ExportStatus { JobId = jobId, State = ExportState.Canceled, Percent = 0 }, "canceled");
                SafeDelete(outputFile);
                return;
            }

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {proc.ExitCode}");

            var url = BuildDownloadUrl(outputFile);
            _outputs[jobId] = outputFile;
            BroadcastStatus(jobId, new ExportStatus
            {
                JobId = jobId,
                State = ExportState.Completed,
                Percent = 100,
                OutputUrl = url
            }, "completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export {JobId} failed", jobId);
            BroadcastStatus(jobId, new ExportStatus
            {
                JobId = jobId,
                State = ExportState.Failed,
                ErrorMessage = ex.Message
            }, "failed");
        }
        finally
        {
            if (_cancellations.TryRemove(jobId, out var c)) c.Dispose();

            // Cleanup temporary SRT file
            if (!string.IsNullOrEmpty(srtPath) && File.Exists(srtPath))
            {
                SafeDelete(srtPath);
            }

            // Cleanup HUD frames directory
            if (!string.IsNullOrEmpty(hudFramesDir) && Directory.Exists(hudFramesDir))
            {
                try
                {
                    Directory.Delete(hudFramesDir, recursive: true);
                    Log.Debug("Cleaned up HUD frames directory: {Dir}", hudFramesDir);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to clean up HUD frames directory: {Dir}", hudFramesDir);
                }
            }
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

    private static List<SeiMetadata> ResampleSeiMessages(
        List<(double timeSeconds, SeiMetadata message)> timeline,
        double targetFrameRate,
        double expectedDurationSeconds)
    {
        var result = new List<SeiMetadata>();

        if (timeline == null || timeline.Count == 0 || targetFrameRate <= 0)
        {
            return result;
        }

        timeline.Sort((a, b) => a.timeSeconds.CompareTo(b.timeSeconds));

        var frameCount = Math.Max(1, (int)Math.Ceiling(expectedDurationSeconds * targetFrameRate));
        var frameDuration = 1.0 / targetFrameRate;

        int idx = 0;
        for (int i = 0; i < frameCount; i++)
        {
            var targetTime = i * frameDuration;

            while (idx + 1 < timeline.Count && timeline[idx + 1].timeSeconds <= targetTime)
            {
                idx++;
            }

            var chosen = timeline[idx].message;

            // If current entry is null, try to grab the next non-null message
            if (chosen == null)
            {
                for (int j = idx + 1; j < timeline.Count; j++)
                {
                    if (timeline[j].message != null)
                    {
                        chosen = timeline[j].message;
                        break;
                    }
                }
            }

            result.Add(chosen);
        }

        return result;
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
