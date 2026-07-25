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
using TeslaCamPlayer.BlazorHosted.Server.Helpers;
using TeslaCamPlayer.BlazorHosted.Server.Hubs;
using TeslaCamPlayer.BlazorHosted.Server.Models;
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

    private void SetState(string jobId, ExportState state, double percent = 0, string outputUrl = null, string errorMessage = null, TimeSpan? eta = null, string reason = null)
        => BroadcastStatus(jobId, new ExportStatus
        {
            JobId = jobId,
            State = state,
            Percent = percent,
            Eta = eta,
            OutputUrl = outputUrl,
            ErrorMessage = errorMessage
        }, reason ?? state.ToString().ToLowerInvariant());

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
        SetState(jobId, ExportState.Pending);

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

    // ponytail: metadata read stays sync (raw ffprobe, see TryReadExportMetadata) — Task shape kept for the interface.
    public Task<List<ExportItem>> ListExportsAsync()
    {
        var items = new List<ExportItem>();
        var root = _settingsProvider.Settings.ExportRootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Task.FromResult(items);

        foreach (var path in Directory.EnumerateFiles(root))
        {
            try
            {
                var fi = new FileInfo(path);
                var jobId = Path.GetFileNameWithoutExtension(fi.Name);
                // In-progress or failed ffmpeg output — never a valid download.
                if (jobId.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                var st = GetStatus(jobId) ?? new ExportStatus
                {
                    JobId = jobId,
                    State = ExportState.Completed,
                    Percent = 100
                };

                var (location, eventPath) = TryReadExportMetadata(fi.FullName);
                items.Add(new ExportItem
                {
                    FileName = fi.Name,
                    Url = BuildDownloadUrl(path),
                    SizeBytes = fi.Length,
                    CreatedUtc = fi.CreationTimeUtc,
                    JobId = jobId,
                    Status = st,
                    Location = location,
                    EventPath = eventPath
                });
            }
            catch { }
        }

        // Sort newest first
        items.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return Task.FromResult(items);
    }

    public bool DeleteExport(string jobId, out string error)
    {
        var exportRoot = Path.GetFullPath(_settingsProvider.Settings.ExportRootPath);
        if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
        {
            error = "Export directory not found";
            return false;
        }

        // Find the export file by jobId
        var files = Directory.EnumerateFiles(exportRoot)
            .Where(f => Path.GetFileNameWithoutExtension(f).Equals(jobId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!files.Any())
        {
            error = "Export file not found";
            return false;
        }

        // Delete all matching files (should typically be just one)
        foreach (var file in files)
        {
            File.Delete(file);
            Log.Information("Deleted export file: {File}", file);
        }

        error = null;
        return true;
    }

    private async Task RunExportAsync(string jobId, ExportRequest request, CancellationToken cancel)
    {
        string hudFramesDir = null;
        string tempOutputFile = null;
        try
        {
            // At this point the stored status is always the Pending one (Percent=0, no Eta/Url/Error),
            // so a fresh Running status broadcasts an identical payload to the old mutate-and-rebroadcast.
            SetState(jobId, ExportState.Running);

            // Validate request. A request carries either an explicit list of intervals or the legacy
            // single StartTimeUtc..EndTimeUtc range; both funnel through the same list below.
            var requestedIntervals = request.Intervals?.Count > 0
                ? request.Intervals
                : new List<ExportInterval> { new() { StartTimeUtc = request.StartTimeUtc, EndTimeUtc = request.EndTimeUtc } };

            if (requestedIntervals.All(i => i.EndTimeUtc <= i.StartTimeUtc))
                throw new InvalidOperationException("End time must be after start time.");

            var clip = (await _clipsService.GetClipsAsync(false))
                .FirstOrDefault(c => string.Equals(c.DirectoryPath, request.ClipDirectoryPath, StringComparison.OrdinalIgnoreCase));

            if (clip == null)
                throw new InvalidOperationException("Clip not found.");

            // Encrypted events index with encrypted /media paths and zero-duration segments; feeding
            // those to ffmpeg yields a black video. Decrypt on demand (cached) and use the rebuilt
            // clip with /config/decrypted paths and real durations, same as playback does.
            if (clip.IsEncrypted)
            {
                clip = await _clipsService.PrepareEncryptedEventAsync(clip.DirectoryPath, cancel);
                if (clip == null)
                    throw new InvalidOperationException("Could not decrypt this event's clips for export.");
            }

            var locationDescription = clip.Event?.GetLocationDescription();

            // Extract location data for HUD renderer
            var locationStreetCity = clip.Event?.GetStreetAndCity();
            double? eventLat = null;
            double? eventLon = null;

            if (clip.Event != null)
            {
                if (double.TryParse(clip.Event.EstLat, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat))
                    eventLat = lat;
                if (double.TryParse(clip.Event.EstLon, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon))
                    eventLon = lon;
            }

            // Clamp to clip bounds, drop empties, sort and merge overlaps.
            var intervals = ExportInterval.Normalize(requestedIntervals, clip.StartDate, clip.EndDate);
            if (intervals.Count == 0)
                throw new InvalidOperationException("Selected interval is outside clip range.");

            var totalSeconds = ExportInterval.TotalSeconds(intervals);
            var start = intervals[0].StartTimeUtc;

            Log.Information(
                "Export {JobId}: {IntervalCount} interval(s), {TotalSeconds:F2}s total",
                jobId, intervals.Count, totalSeconds);

            // Build per-camera chunk lists, in output order (interval, then segment). A chunk with a null
            // path is a black filler: it keeps every camera the same length as the output even when a
            // camera is missing from a segment or the recording has a hole, which xstack otherwise
            // papers over by freezing that tile on its last frame.
            var byCamera = new Dictionary<Cameras, List<Chunk>>();
            foreach (var cam in request.OrderedCameras)
            {
                byCamera[cam] = new();
            }

            for (var intervalIndex = 0; intervalIndex < intervals.Count; intervalIndex++)
            {
                var interval = intervals[intervalIndex];

                foreach (var cam in request.OrderedCameras)
                {
                    var cursor = interval.StartTimeUtc;

                    foreach (var seg in clip.Segments)
                    {
                        var overlapStart = seg.StartDate > interval.StartTimeUtc ? seg.StartDate : interval.StartTimeUtc;
                        var overlapEnd = seg.EndDate < interval.EndTimeUtc ? seg.EndDate : interval.EndTimeUtc;
                        if (overlapEnd <= overlapStart)
                            continue;

                        var vf = CameraToFile(seg, cam);
                        if (vf == null)
                            continue;

                        AddFillerIfGap(byCamera[cam], (overlapStart - cursor).TotalSeconds, intervalIndex);
                        byCamera[cam].Add(new Chunk(
                            vf.FilePath,
                            (overlapStart - seg.StartDate).TotalSeconds,
                            (overlapEnd - overlapStart).TotalSeconds,
                            intervalIndex));
                        cursor = overlapEnd;
                    }

                    AddFillerIfGap(byCamera[cam], (interval.EndTimeUtc - cursor).TotalSeconds, intervalIndex);
                }
            }

            // Build ffmpeg command
            var exportDir = _settingsProvider.Settings.ExportRootPath;
            Directory.CreateDirectory(exportDir);

            var ext = SanitizeFormat(request.Format);
            var outputFile = Path.Combine(exportDir, jobId + "." + ext);
            // ffmpeg writes to a ".part" name; renamed to the final name only on success so
            // failed/in-progress files never surface in ListExports (presence-on-disk = Completed).
            // Keeps the real extension last so ffmpeg's muxer inference still works.
            tempOutputFile = Path.Combine(exportDir, jobId + ".part." + ext);

            // Build argv tokens for ffmpeg. Use ArgumentList to avoid shell quoting issues.
            var argv = new List<string>();
            argv.Add("-y");
            argv.Add("-hide_banner");
            argv.Add("-nostdin");
            AddArg(argv, "-progress", "pipe:1");

            // Inputs: for each real camera chunk, add -ss -t -i file (fillers are generated in the graph)
            var inputIndexMap = new Dictionary<(Cameras cam, int partIndex), int>();
            var globalInputIndex = 0;
            foreach (var cam in request.OrderedCameras)
            {
                var parts = byCamera[cam];
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.IsFiller)
                        continue;

                    argv.Add("-accurate_seek");
                    AddArg(argv, "-ss", FormatTimeArg(p.Start));
                    AddArg(argv, "-t", FormatTimeArg(p.Duration));
                    AddArg(argv, "-i", p.Path);
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

            // Each chunk is normalised to the cell size before concat, so a black filler splices in
            // cleanly next to real footage. (Scaling per chunk instead of once after concat costs the
            // same pixels — the concat inputs just have to agree on size, SAR, rate and pixel format.)
            const string chunkFormat = ",setsar=1,format=yuv420p";
            foreach (var cam in request.OrderedCameras)
            {
                var parts = byCamera[cam];
                var partLabels = new List<string>();

                for (int i = 0; i < parts.Count; i++)
                {
                    var label = $"[{cam}_p{i}]";
                    if (parts[i].IsFiller)
                    {
                        filter.Append($"color=c=black:size={cellW}x{cellH}:rate=30:duration={FormatTimeArg(parts[i].Duration)}{chunkFormat}");
                    }
                    else
                    {
                        // -ss/-t lands on frame boundaries and can overshoot by a frame; trim pins the
                        // chunk to its exact length so the interval seams (and the timestamp windows
                        // gated on them) don't drift.
                        filter.Append($"[{inputIndexMap[(cam, i)]}:v]")
                              .Append($"scale={cellW}:{cellH}:force_original_aspect_ratio=decrease,pad={cellW}:{cellH}:(ow-iw)/2:(oh-ih)/2{chunkFormat},fps=30")
                              .Append($",trim=duration={FormatTimeArg(parts[i].Duration)},setpts=PTS-STARTPTS");
                    }

                    filter.Append(label).Append(';');
                    partLabels.Add(label);
                }

                // No chunks at all means the camera was absent for the whole selection.
                if (partLabels.Count == 0)
                {
                    var blackLabel = $"[{cam}_p0]";
                    filter.Append($"color=c=black:size={cellW}x{cellH}:rate=30:duration={FormatTimeArg(totalSeconds)}{chunkFormat}")
                          .Append(blackLabel)
                          .Append(';');
                    partLabels.Add(blackLabel);
                }

                var concatOut = $"[{cam}_concat]";
                if (partLabels.Count == 1)
                {
                    filter.Append(partLabels[0]).Append("setpts=PTS-STARTPTS").Append(concatOut).Append(';');
                }
                else
                {
                    filter.Append(string.Join(string.Empty, partLabels))
                          .Append($"concat=n={partLabels.Count}:v=1:a=0")
                          .Append(concatOut)
                          .Append(';');
                }

                var final = concatOut;
                if (request.IncludeCameraLabels)
                {
                    var labelText = CameraLabel(cam);
                    var labeled = $"[{cam}_labeled]";
                    filter.Append(concatOut)
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

            // Location overlay: Use FFmpeg drawtext only when Python HUD renderer won't be invoked
            // Python HUD renders location when location overlay is requested AND front camera exists
            bool hasFrontCamera = byCamera.ContainsKey(Cameras.Front) && byCamera[Cameras.Front].Any(c => !c.IsFiller);
            bool wantsLocationOverlay = request.IncludeLocationOverlay;
            bool wantsSeiHud = request.IncludeSeiHud;
            bool willUsePythonHud = (wantsSeiHud || wantsLocationOverlay) && hasFrontCamera;

            Log.Information(
                "[LOCATION DEBUG] IncludeLocationOverlay={IncludeLocationOverlay}, IncludeSeiHud={IncludeSeiHud}, hasFrontCamera={HasFrontCamera}, willUsePythonHud={WillUsePythonHud}",
                wantsLocationOverlay, wantsSeiHud, hasFrontCamera, willUsePythonHud);

            if (wantsLocationOverlay && !willUsePythonHud)
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

            // Timestamp overlay on final output if requested.
            // %{pts:localtime:E} renders localtime(E + t), i.e. one clock running the length of the
            // output — so each interval gets its own drawtext, offset back by where it starts in the
            // output and gated to that span, otherwise intervals 2..N would keep counting through the
            // gaps. gte/lt rather than between() so neighbours don't both draw on the seam frame.
            if (request.IncludeTimestamp)
            {
                var ts = "[ts]";
                filter.Append(';')
                      .Append('[').Append(finalLabel).Append(']')
                      .Append("setpts=PTS-STARTPTS");

                var fmtSettings = _settingsProvider.Settings;
                // Colons inside the strftime pattern pass two parsers: the filtergraph option
                // parser (\: -> :) and then the %{...} expansion parser, which splits args on
                // bare ':'. So each format colon must arrive at the expansion layer as '\:',
                // which means emitting '\\\:' here. Verified against ffmpeg: '\:' or '\\:' both
                // break ("%{pts} requires at most 3 arguments" / graph parse error).
                var strftimePattern =
                    $"{TimeFormats.StrftimeDate(fmtSettings.DateFormat)} {TimeFormats.StrftimeTime(fmtSettings.TimeFormat)}"
                        .Replace(":", @"\\\:");

                var offsetSeconds = 0d;
                for (var i = 0; i < intervals.Count; i++)
                {
                    var interval = intervals[i];
                    var epoch = new DateTimeOffset(interval.StartTimeUtc.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0 - offsetSeconds;
                    var spanEnd = offsetSeconds + interval.DurationSeconds;
                    // Overshoot the last span so a final frame landing exactly on the boundary still draws.
                    var enableEnd = i == intervals.Count - 1 ? spanEnd + 1 : spanEnd;
                    filter.Append(',')
                          .Append($@"drawtext=text='%{{pts\:localtime\:{FormatTimeArg(epoch)}\:{strftimePattern}}}':fontcolor=white:fontsize=24:box=1:boxcolor=black@0.4:x=w-tw-10:y=h-th-10")
                          .Append($":enable='gte(t,{FormatTimeArg(offsetSeconds)})*lt(t,{FormatTimeArg(enableEnd)})'");
                    offsetSeconds = spanEnd;
                }

                filter.Append(ts);
                finalLabel = "ts";
            }

            // SEI HUD and/or location overlay with live GPS
            // Invoke HUD renderer if either SEI HUD or location overlay is requested
            Log.Information(
                "[LOCATION DEBUG] Checking Python HUD invocation: IncludeSeiHud={IncludeSeiHud}, IncludeLocationOverlay={IncludeLocationOverlay}",
                wantsSeiHud, wantsLocationOverlay);

            if (willUsePythonHud)
            {
                Log.Information("[LOCATION DEBUG] Python HUD section entered - will attempt to render HUD/location");

                const double seiFrameRate = 30.0; // HUD rendering frame rate
                // The HUD is overlaid with shortest=1, and the video is sum(ceil(fps*chunk)) frames while a
                // naive HUD would be ceil(fps*total) — always the shorter of the two, so it would trim the
                // video and still exit 0. Slack of one frame per chunk makes the HUD the shorter input.
                var maxChunks = byCamera.Values.Select(c => c.Count).DefaultIfEmpty(0).Max();
                var exportDurationSeconds = ExportTiming.HudDurationSeconds(totalSeconds, maxChunks, seiFrameRate);
                List<SeiMetadata> hudFrames = null;
                var hudFramesContainSei = false;

                // Find front camera segment info for SEI extraction using MP4 frame timing
                if (hasFrontCamera)
                {
                    var seiTimeline = new List<(double timeSeconds, SeiMetadata message)>();
                    var frontSegments = byCamera[Cameras.Front];

                    Log.Information(
                        "SEI HUD sync: Processing {SegmentCount} front camera chunks using MP4 frame timing",
                        frontSegments.Count);

                    // Several intervals can land on the same segment file, and neither service caches.
                    // Both do a full-file read (the SEI one protobuf-decodes every NAL), so memoize per
                    // job — job-scoped, so there is nothing to invalidate.
                    var timelineCache = new Dictionary<string, Mp4FrameTimeline>(StringComparer.OrdinalIgnoreCase);
                    var seiCache = new Dictionary<string, List<SeiMetadata>>(StringComparer.OrdinalIgnoreCase);

                    double cumulativeExportSeconds = 0;
                    ulong? lastFrameSeqNo = null;
                    double? lastLat = null, lastLon = null;
                    float? lastSpeed = null;
                    int segmentIndex = 0;
                    int lastIntervalIndex = -1;

                    foreach (var segment in frontSegments)
                    {
                        // The accumulator is the chunk's position in the concatenated output, so every exit from this
                        // body must advance it or all later telemetry lands early. finally makes that structural — a
                        // future `continue` cannot skip it. ChunkOutputSeconds accounts for ffmpeg emitting whole frames.
                        try
                        {
                            // A seam between intervals is an expected discontinuity, not a fault — drop the
                            // running continuity state so the diagnostics below stay meaningful.
                            if (segment.IntervalIndex != lastIntervalIndex)
                            {
                                lastFrameSeqNo = null;
                                lastLat = null;
                                lastLon = null;
                                lastSpeed = null;
                                lastIntervalIndex = segment.IntervalIndex;
                            }

                            // Black filler occupies output time but carries no telemetry
                            if (segment.IsFiller)
                            {
                                continue;
                            }

                            // Extract MP4 frame timing metadata
                            if (!timelineCache.TryGetValue(segment.Path, out var timeline))
                            {
                                timeline = await _mp4Timing.GetFrameTimelineAsync(segment.Path);
                                timelineCache[segment.Path] = timeline;
                            }

                            if (timeline == null)
                            {
                                Log.Warning("Failed to extract MP4 timing for {Path}, skipping SEI extraction", segment.Path);
                                continue;
                            }

                            // Extract ALL SEI messages from this segment
                            if (!seiCache.TryGetValue(segment.Path, out var allMessages))
                            {
                                allMessages = _seiParser.ExtractSeiMessages(segment.Path);
                                seiCache[segment.Path] = allMessages;
                            }

                            if (allMessages.Count == 0)
                            {
                                Log.Warning("No SEI metadata found in front segment {Path}", segment.Path);
                                continue;
                            }

                            // Validate timeline matches SEI message count
                            if (timeline.FrameCount != allMessages.Count)
                            {
                                Log.Warning(
                                    "MP4 frame count ({FrameCount}) != SEI message count ({SeiCount}) for {Path}. Using min for safety.",
                                    timeline.FrameCount, allMessages.Count, segment.Path);

                                // Trim SEI messages if MP4 has fewer frames
                                if (timeline.FrameCount < allMessages.Count)
                                {
                                    allMessages = allMessages.GetRange(0, timeline.FrameCount);
                                }
                            }

                            var startMs = segment.Start * 1000.0;
                            var endMs = (segment.Start + segment.Duration) * 1000.0;
                            var startFrameIndex = timeline.FindFrameIndexForMs(startMs);
                            var endFrameIndex = timeline.FindFrameIndexForMs(endMs);

                            if (startFrameIndex < 0 || endFrameIndex < 0)
                            {
                                Log.Warning(
                                    "Frame indices not found for SEI extraction: start={StartMs:F2}ms end={EndMs:F2}ms for {Path}",
                                    startMs, endMs, segment.Path);
                                continue;
                            }

                            startFrameIndex = Math.Max(0, startFrameIndex);
                            endFrameIndex = Math.Min(
                                Math.Min(endFrameIndex, timeline.FrameCount - 1),
                                allMessages.Count - 1);

                            if (endFrameIndex < startFrameIndex)
                            {
                                Log.Warning("Invalid frame range for SEI extraction: [{Start}..{End}] for {Path}",
                                    startFrameIndex, endFrameIndex, segment.Path);
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
                                    segment.Path,
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
                                Path.GetFileName(segment.Path),
                                segment.Start,
                                segment.Duration,
                                cumulativeExportSeconds,
                                segmentSeiMessages.Count,
                                segmentSeiMessages.Count > 0 ? segmentSeiMessages[0].FrameSeqNo : 0,
                                segmentSeiMessages.Count > 0 ? segmentSeiMessages[segmentSeiMessages.Count - 1].FrameSeqNo : 0,
                                segmentSeiMessages.Count > 0 ? segmentSeiMessages[0].VehicleSpeedMps : 0,
                                segmentSeiMessages.Count > 0 ? segmentSeiMessages[segmentSeiMessages.Count - 1].VehicleSpeedMps : 0);

                        }
                        finally
                        {
                            cumulativeExportSeconds += ExportTiming.ChunkOutputSeconds(segment.Duration, seiFrameRate);
                            segmentIndex++;
                        }
                    }

                    Log.Information(
                        "[LOCATION DEBUG] SEI extraction complete: seiTimeline.Count={SeiCount}",
                        seiTimeline.Count);

                    if (seiTimeline.Count > 0 && wantsSeiHud)
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

                        hudFramesContainSei = true;
                        hudFrames = resampledSeiMessages;
                    }
                    else
                    {
                        Log.Warning("[LOCATION DEBUG] No SEI metadata found in front camera for export {JobId} - seiTimeline.Count=0", jobId);
                        Log.Information(
                            "[LOCATION DEBUG] Fallback scenario: IncludeLocationOverlay={IncludeLocationOverlay}, locationStreetCity={StreetCity}, eventLat={Lat}, eventLon={Lon}",
                            wantsLocationOverlay,
                            locationStreetCity ?? "(null)",
                            eventLat?.ToString() ?? "(null)",
                            eventLon?.ToString() ?? "(null)");
                    }
                }
                else
                {
                    Log.Warning("[LOCATION DEBUG] SEI HUD/location requested but no front camera available for export {JobId}", jobId);
                    Log.Information(
                        "[LOCATION DEBUG] No front camera fallback: IncludeLocationOverlay={IncludeLocationOverlay}, willUsePythonHud={WillUsePythonHud}",
                        wantsLocationOverlay, willUsePythonHud);
                }

                // If location overlay is requested but we have no SEI frames, synthesize placeholder frames so GPS from event.json is still shown
                if (wantsLocationOverlay && (hudFrames == null || hudFrames.Count == 0))
                {
                    var placeholderCount = Math.Max(1, (int)Math.Ceiling(exportDurationSeconds * seiFrameRate));
                    hudFrames = Enumerable.Range(0, placeholderCount)
                                          .Select(_ => (SeiMetadata)null) // null payload -> location only
                                          .ToList();

                    Log.Information(
                        "[LOCATION DEBUG] Synthesized {FrameCount} placeholder HUD frames for location overlay using event.json GPS (lat={Lat}, lon={Lon})",
                        placeholderCount,
                        eventLat?.ToString() ?? "(null)",
                        eventLon?.ToString() ?? "(null)");
                }

                // Render HUD/location overlay if any frames are available
                if (hudFrames != null && hudFrames.Count > 0)
                {
                    hudFramesDir = Path.Combine(exportDir, $"{jobId}_hud_frames");
                    var useMph = _settingsProvider.Settings.SpeedUnit == "mph";

                    // Pass location data only if overlay is enabled
                    var streetCity = wantsLocationOverlay ? locationStreetCity : null;
                    double? lat = null;
                    double? lon = null;

                    if (wantsLocationOverlay && !hudFramesContainSei)
                    {
                        lat = eventLat;
                        lon = eventLon;
                    }

                    Log.Information(
                        "[LOCATION DEBUG] Passing to HUD renderer: streetCity={StreetCity}, lat={Lat}, lon={Lon}, renderLocationOverlay={RenderLocationOverlay}",
                        streetCity ?? "(null)", lat?.ToString() ?? "(null)", lon?.ToString() ?? "(null)", wantsLocationOverlay);

                    await _hudRenderer.RenderHudFramesToDirectoryAsync(
                        hudFrames,
                        hudFramesDir,
                        outW,
                        outH,
                        seiFrameRate,
                        useMph,
                        streetCity,
                        lat,
                        lon,
                        wantsLocationOverlay,
                        cancel);

                    // Add HUD frames as FFmpeg input
                    var hudInputIndex = globalInputIndex;
                    AddArg(argv, "-framerate", seiFrameRate.ToString("0.##", CultureInfo.InvariantCulture));
                    AddArg(argv, "-i", Path.Combine(hudFramesDir, "frame_%06d.png"));
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
                          .Append("overlay=0:0:shortest=1:format=rgb")
                          .Append(hudOverlayOut);
                    finalLabel = "hud_overlay";

                    Log.Information("HUD renderer output added to export {JobId} ({Count} frames)", jobId, hudFrames.Count);
                }
            }

            AddArg(argv, "-filter_complex", filter.ToString());

            // map final
            AddArg(argv, "-map", $"[{finalLabel}]");

            // No audio
            argv.Add("-an");

            // Codec / container options
            AddCodecArgs(argv, request);

            // Embed metadata: creation_time and simple title/comment with event time
            try
            {
                var eventTime = clip.Event?.Timestamp ?? start;
                var utc = eventTime.ToUniversalTime().ToString("o");
                AddArg(argv, "-metadata", "title=TeslaCamPlayer Export");

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
                AddArg(argv, "-metadata", $"comment={string.Join("; ", commentParts)}");

                AddArg(argv, "-metadata", $"creation_time={utc}");
            }
            catch { }

            argv.Add(tempOutputFile);

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
            // Same cadence as RefreshProgressService; terminal states broadcast unthrottled elsewhere.
            var lastProgressBroadcastUtc = DateTime.MinValue;
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
                            var now = DateTime.UtcNow;
                            if (now - lastProgressBroadcastUtc < TimeSpan.FromMilliseconds(250))
                                return;
                            lastProgressBroadcastUtc = now;

                            var sec = outMs / 1000000.0;
                            var pct = Math.Clamp(totalSeconds > 0 ? (sec / totalSeconds) * 100.0 : 0, 0, 100);
                            var eta = totalSeconds > 0 ? TimeSpan.FromSeconds(Math.Max(0, totalSeconds - sec)) : (TimeSpan?)null;
                            SetState(jobId, ExportState.Running, pct, eta: eta, reason: "progress");
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
                SetState(jobId, ExportState.Canceled);
                SafeDelete(tempOutputFile);
                return;
            }

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {proc.ExitCode}");

            File.Move(tempOutputFile, outputFile, overwrite: true);
            tempOutputFile = null;

            var url = BuildDownloadUrl(outputFile);
            _outputs[jobId] = outputFile;
            SetState(jobId, ExportState.Completed, 100, url);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export {JobId} failed", jobId);
            SetState(jobId, ExportState.Failed, errorMessage: ex.Message);
            SafeDelete(tempOutputFile);
        }
        finally
        {
            if (_cancellations.TryRemove(jobId, out var c)) c.Dispose();

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

    /// <summary>
    /// One piece of a camera's output timeline. A null <see cref="Path"/> is a black filler covering
    /// output time the camera has no footage for.
    /// </summary>
    private sealed record Chunk(string Path, double Start, double Duration, int IntervalIndex)
    {
        public bool IsFiller => Path == null;
    }

    // Below ~1 frame a filler is not worth a lavfi source; xstack absorbs the rounding.
    private static void AddFillerIfGap(List<Chunk> chunks, double gapSeconds, int intervalIndex)
    {
        if (gapSeconds > 0.02)
            chunks.Add(new Chunk(null, 0, gapSeconds, intervalIndex));
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

    private static void AddArg(List<string> argv, string name, string value)
    {
        argv.Add(name);
        argv.Add(value);
    }

    private static void AddCodecArgs(List<string> args, ExportRequest request)
    {
        var fmt = SanitizeFormat(request.Format);
        switch (fmt)
        {
            case "mp4":
            case "mov":
                AddArg(args, "-c:v", "libx264");
                AddArg(args, "-pix_fmt", "yuv420p");
                var (preset, crf) = QualityToPresetCrf(request.Quality);
                AddArg(args, "-preset", preset);
                AddArg(args, "-crf", crf);
                if (fmt == "mp4")
                {
                    AddArg(args, "-movflags", "+faststart");
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

    private string BuildDownloadUrl(string outputFile)
    {
        try
        {
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var full = Path.GetFullPath(outputFile);
            if (PathSafety.IsUnder(Path.GetFullPath(Path.Combine(wwwroot, "exports")), full))
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

    private static (string location, string eventPath) TryReadExportMetadata(string path)
    {
        try
        {
            // ponytail: raw ffprobe spawn kept — IFfProbeService only exposes duration and its
            // ExePath is protected; same bare-binary convention as the ffmpeg spawn above.
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format_tags=comment");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process == null)
                return (null, null);

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                return (null, null);

            var comment = stdout.Trim();
            if (string.IsNullOrWhiteSpace(comment))
                return (null, null);

            // Parse metadata from comment format: "EventTimeUTC=...; Location=...; EventPath=..."
            var parts = comment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            string location = null;
            string eventPath = null;

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("Location=", StringComparison.OrdinalIgnoreCase))
                {
                    location = trimmed.Substring("Location=".Length).Trim();
                }
                else if (trimmed.StartsWith("EventPath=", StringComparison.OrdinalIgnoreCase))
                {
                    eventPath = trimmed.Substring("EventPath=".Length).Trim();
                }
            }

            return (location, eventPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read export metadata from {Path}", path);
            return (null, null);
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
