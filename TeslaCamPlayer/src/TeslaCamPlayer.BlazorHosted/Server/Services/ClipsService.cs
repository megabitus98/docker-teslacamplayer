using Newtonsoft.Json;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public partial class ClipsService : IClipsService
{
    private const string NoThumbnailImageUrl = "/img/no-thumbnail.png";
    private static readonly Regex FileNameRegex = FileNameRegexGenerated();
    private static Clip[] _cache;

    private readonly ISettingsProvider _settingsProvider;
    private readonly IFfProbeService _ffProbeService;
    private readonly SemaphoreSlim _ffprobeSemaphore;
    private readonly IRefreshProgressService _refreshProgressService;

    // State for background progressive refresh
    private readonly object _refreshGate = new();
    private Task _refreshTask;

    public ClipsService(ISettingsProvider settingsProvider, IFfProbeService ffProbeService, IRefreshProgressService refreshProgressService)
    {
        _settingsProvider = settingsProvider;
        _ffProbeService = ffProbeService;
        _refreshProgressService = refreshProgressService;
        // limit the number of ffprobe instances to how many cores we have, otherwise... BOOM!
        int semaphoreCount = Environment.ProcessorCount;
        _ffprobeSemaphore = new SemaphoreSlim(semaphoreCount);
    }

    private string CacheFilePath => _settingsProvider.Settings.CacheFilePath ?? Path.Combine(AppContext.BaseDirectory, "clips.json");

    private async Task<Clip[]> GetCachedAsync()
    {
        if (!File.Exists(CacheFilePath))
        {
            return null;
        }

        Clip[] cached;
        try
        {
            var json = await File.ReadAllTextAsync(CacheFilePath);
            cached = JsonConvert.DeserializeObject<Clip[]>(json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read clip cache from {CacheFilePath}", CacheFilePath);
            return null;
        }

        if (cached == null)
        {
            return null;
        }

        var sanitized = PruneMissingEventClips(cached, out var removedAny);
        if (removedAny)
        {
            try
            {
                await PersistCacheAsync(sanitized);
            }
            catch (Exception persistEx)
            {
                Log.Error(persistEx, "Failed to update clip cache after pruning missing events.");
            }
        }

        return sanitized;
    }

    public async Task<Clip[]> GetClipsAsync(bool refreshCache = false)
    {
        _cache ??= await GetCachedAsync();

        // If we already have a cache and caller didn't request refresh, return fast
        if (!refreshCache && _cache != null)
            return _cache;

        // When there is no cache yet (first run) or refresh is requested, start background refresh
        StartBackgroundRefreshIfNeeded();

        // Ensure we always return a non-null array for the first paint
        _cache ??= Array.Empty<Clip>();
        return _cache;
    }

    private void StartBackgroundRefreshIfNeeded()
    {
        lock (_refreshGate)
        {
            if (_refreshTask != null && !_refreshTask.IsCompleted)
                return;

            _refreshTask = Task.Run(async () => await RefreshCacheWorkerAsync());
        }
    }

    private async Task RefreshCacheWorkerAsync()
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var settings = _settingsProvider.Settings;
            var initialBatchSize = Math.Max(settings.IndexingBatchSize, settings.IndexingMinBatchSize);
            var minBatchSize = Math.Max(1, settings.IndexingMinBatchSize);

            var existing = _cache ?? Array.Empty<Clip>();
            var knownVideoFiles = new ConcurrentDictionary<string, VideoFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in existing
                         .SelectMany(c => c.Segments.SelectMany(s => s.VideoFiles))
                         .Where(v => v != null))
            {
                knownVideoFiles[video.FilePath] = video;
            }

            var totalCandidates = EnumerateCandidatePaths(settings).Count();
            Log.Information(
                "Event indexing started with {CandidateCount} candidate video files. Target batch size {BatchSize}, minimum batch size {MinBatchSize}, memory threshold {MemoryThreshold:P2}.",
                totalCandidates,
                initialBatchSize,
                minBatchSize,
                settings.IndexingMaxMemoryUtilization);

            _refreshProgressService.Start(totalCandidates);

            var aggregatedResults = new List<VideoFile>();
            var pendingPaths = new List<string>(initialBatchSize);
            var currentBatchSize = initialBatchSize;
            var batchNumber = 0;

            foreach (var path in EnumerateCandidatePaths(settings))
            {
                pendingPaths.Add(path);

                while (pendingPaths.Count >= currentBatchSize)
                {
                    batchNumber++;
                    currentBatchSize = await ExecuteBatchAsync(
                        batchNumber,
                        pendingPaths,
                        currentBatchSize,
                        minBatchSize,
                        knownVideoFiles,
                        aggregatedResults,
                        settings,
                        CancellationToken.None);
                }
            }

            if (pendingPaths.Count > 0)
            {
                batchNumber++;
                currentBatchSize = await ExecuteBatchAsync(
                    batchNumber,
                    pendingPaths,
                    Math.Min(currentBatchSize, pendingPaths.Count),
                    minBatchSize,
                    knownVideoFiles,
                    aggregatedResults,
                    settings,
                    CancellationToken.None);
            }

            await PublishPartialAsync(aggregatedResults.ToArray());

            stopwatch.Stop();

            var totalEventCount = aggregatedResults
                .Select(v => v?.EventFolderName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            Log.Information(
                "Event indexing completed in {Elapsed}. Indexed {VideoCount} video files across {EventCount} events using {BatchCount} batches.",
                stopwatch.Elapsed,
                aggregatedResults.Count,
                totalEventCount,
                batchNumber);
        }
        finally
        {
            _refreshProgressService.Complete();
        }
    }

    private async Task<int> ExecuteBatchAsync(
        int batchNumber,
        List<string> pendingPaths,
        int requestedBatchSize,
        int minBatchSize,
        ConcurrentDictionary<string, VideoFile> knownVideoFiles,
        List<VideoFile> aggregateResults,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var (targetBatchSize, snapshotBefore) = await PrepareBatchAsync(
            Math.Max(minBatchSize, requestedBatchSize),
            minBatchSize,
            batchNumber,
            settings,
            cancellationToken);

        var actualCount = Math.Min(targetBatchSize, pendingPaths.Count);
        if (actualCount == 0)
        {
            return targetBatchSize;
        }

        var batchPaths = pendingPaths.GetRange(0, actualCount);
        pendingPaths.RemoveRange(0, actualCount);

        Log.Information(
            "Batch {BatchNumber} starting: {FileCount} files. WorkingSet={WorkingSetMb:F2} MB, Managed={ManagedMb:F2} MB, Available={AvailableMb:F2} MB, Utilization={Utilization:P2} (threshold {Threshold:P2}).",
            batchNumber,
            batchPaths.Count,
            snapshotBefore.WorkingSetInMegabytes,
            snapshotBefore.ManagedMemoryInMegabytes,
            snapshotBefore.AvailableMemoryInMegabytes,
            snapshotBefore.Utilization,
            settings.IndexingMaxMemoryUtilization);

        var batchResults = await ProcessBatchInternalAsync(batchPaths, knownVideoFiles);

        var batchEventCount = batchResults
            .Select(v => v?.EventFolderName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        aggregateResults.AddRange(batchResults);

        await PublishPartialAsync(aggregateResults.ToArray());

        PerformGarbageCollection($"post-batch {batchNumber}");
        var snapshotAfter = CaptureMemorySnapshot();

        Log.Information(
            "Batch {BatchNumber} completed: indexed {VideoCount} videos ({EventCount} events). WorkingSet={WorkingSetMb:F2} MB, Managed={ManagedMb:F2} MB, Available={AvailableMb:F2} MB, Utilization={Utilization:P2}.",
            batchNumber,
            batchResults.Count,
            batchEventCount,
            snapshotAfter.WorkingSetInMegabytes,
            snapshotAfter.ManagedMemoryInMegabytes,
            snapshotAfter.AvailableMemoryInMegabytes,
            snapshotAfter.Utilization);

        return targetBatchSize;
    }

    private async Task<(int BatchSize, MemorySnapshot Snapshot)> PrepareBatchAsync(
        int requestedBatchSize,
        int minBatchSize,
        int batchNumber,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var adjustedBatchSize = Math.Max(minBatchSize, requestedBatchSize);

        while (true)
        {
            var snapshot = CaptureMemorySnapshot();

            if (settings.IndexingMaxMemoryUtilization <= 0 || snapshot.Utilization <= settings.IndexingMaxMemoryUtilization)
            {
                return (adjustedBatchSize, snapshot);
            }

            if (adjustedBatchSize > minBatchSize)
            {
                var reducedBatchSize = Math.Max(minBatchSize, adjustedBatchSize / 2);
                Log.Warning(
                    "Batch {BatchNumber}: memory utilization {Utilization:P2} exceeds threshold {Threshold:P2}. Reducing batch size from {CurrentSize} to {ReducedSize}.",
                    batchNumber,
                    snapshot.Utilization,
                    settings.IndexingMaxMemoryUtilization,
                    adjustedBatchSize,
                    reducedBatchSize);
                adjustedBatchSize = reducedBatchSize;
            }
            else
            {
                Log.Warning(
                    "Batch {BatchNumber}: memory utilization {Utilization:P2} exceeds threshold {Threshold:P2}. Pausing for {DelaySeconds}s to recover.",
                    batchNumber,
                    snapshot.Utilization,
                    settings.IndexingMaxMemoryUtilization,
                    settings.IndexingMemoryRecoveryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(settings.IndexingMemoryRecoveryDelaySeconds), cancellationToken);
            }

            PerformGarbageCollection($"memory-pressure (batch {batchNumber})");
        }
    }

    private async Task<List<VideoFile>> ProcessBatchInternalAsync(
        List<string> batchPaths,
        ConcurrentDictionary<string, VideoFile> knownVideoFiles)
    {
        var tasks = batchPaths
            .Select(path => ProcessVideoFileAsync(path, knownVideoFiles))
            .ToArray();

        var processed = await Task.WhenAll(tasks);
        return processed
            .Where(v => v != null)
            .ToList();
    }

    private async Task<VideoFile> ProcessVideoFileAsync(
        string path,
        ConcurrentDictionary<string, VideoFile> knownVideoFiles)
    {
        try
        {
            if (knownVideoFiles.TryGetValue(path, out var known))
            {
                return known;
            }

            var match = FileNameRegex.Match(path);
            if (!match.Success)
            {
                Log.Debug("Skipping video file that does not match expected naming pattern: {Path}", path);
                return null;
            }

            var parsed = await TryParseVideoFileAsync(path, match);
            if (parsed != null)
            {
                knownVideoFiles[path] = parsed;
            }

            return parsed;
        }
        finally
        {
            _refreshProgressService.Increment();
        }
    }

    private IEnumerable<string> EnumerateCandidatePaths(Settings settings)
    {
        foreach (var path in Directory.EnumerateFiles(settings.ClipsRootPath, "*.mp4", SearchOption.AllDirectories))
        {
            if (FileNameRegex.IsMatch(path))
            {
                yield return path;
            }
        }
    }

    private static void PerformGarbageCollection(string reason)
    {
        Log.Information("Invoking garbage collection ({Reason}).", reason);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static MemorySnapshot CaptureMemorySnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var managedMemory = GC.GetTotalMemory(forceFullCollection: false);
        var gcInfo = GC.GetGCMemoryInfo();
        var availableMemory = gcInfo.TotalAvailableMemoryBytes > 0
            ? gcInfo.TotalAvailableMemoryBytes
            : gcInfo.HighMemoryLoadThresholdBytes;
        var utilization = availableMemory > 0
            ? (double)gcInfo.MemoryLoadBytes / availableMemory
            : 0d;

        return new MemorySnapshot(workingSet, managedMemory, availableMemory, utilization);
    }

    private static double BytesToMegabytes(long bytes)
        => bytes <= 0 ? 0d : bytes / 1024d / 1024d;

    private readonly struct MemorySnapshot
    {
        public MemorySnapshot(long workingSetBytes, long managedMemoryBytes, long availableMemoryBytes, double utilization)
        {
            WorkingSetBytes = workingSetBytes;
            ManagedMemoryBytes = managedMemoryBytes;
            AvailableMemoryBytes = availableMemoryBytes;
            Utilization = utilization;
        }

        public long WorkingSetBytes { get; }
        public long ManagedMemoryBytes { get; }
        public long AvailableMemoryBytes { get; }
        public double Utilization { get; }

        public double WorkingSetInMegabytes => BytesToMegabytes(WorkingSetBytes);
        public double ManagedMemoryInMegabytes => BytesToMegabytes(ManagedMemoryBytes);
        public double AvailableMemoryInMegabytes => BytesToMegabytes(AvailableMemoryBytes);
    }

    private async Task PublishPartialAsync(VideoFile[] videoFiles)
    {
        try
        {
            var recentClips = GetRecentClips(videoFiles
                .Where(vfi => vfi.ClipType == ClipType.Recent)
                .ToList());

            var eventClips = videoFiles
                .Select(vfi => vfi.EventFolderName)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct()
                .AsParallel()
                .Select(e => ParseClip(e, videoFiles))
                .ToArray();

            var combined = eventClips
                .Concat(recentClips.AsParallel())
                .OrderByDescending(c => c.StartDate)
                .ToArray();

            var sanitized = PruneMissingEventClips(combined, out _);
            _cache = sanitized;
            await PersistCacheAsync(sanitized);
        }
        catch
        {
            // ignore partial publish errors to avoid stopping the refresh loop
        }
    }

    private async Task PersistCacheAsync(Clip[] clips)
    {
        var cacheDirectory = Path.GetDirectoryName(CacheFilePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
        }

        await File.WriteAllTextAsync(CacheFilePath, JsonConvert.SerializeObject(clips));
    }

    private Clip[] PruneMissingEventClips(Clip[] clips, out bool removedAny)
    {
        removedAny = false;

        if (clips == null)
        {
            return null;
        }

        if (clips.Length == 0)
        {
            return clips;
        }

        var filtered = new List<Clip>(clips.Length);
        foreach (var clip in clips)
        {
            if (clip == null)
            {
                removedAny = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(clip.DirectoryPath) || Directory.Exists(clip.DirectoryPath))
            {
                filtered.Add(clip);
                continue;
            }

            removedAny = true;
            Log.Information("Removed cached event at {DirectoryPath} because it no longer exists on disk.", clip.DirectoryPath);
        }

        return removedAny ? filtered.ToArray() : clips;
    }

    private static IEnumerable<Clip> GetRecentClips(List<VideoFile> recentVideoFiles)
    {
        recentVideoFiles = recentVideoFiles.OrderByDescending(f => f.StartDate).ToList();

        var currentClipSegments = new List<ClipVideoSegment>();
        for (var i = 0; i < recentVideoFiles.Count;)
        {
            var currentVideoFile = recentVideoFiles[i];
            var segmentVideos = recentVideoFiles.Where(f => f.StartDate == currentVideoFile.StartDate).ToList();
            var segment = new ClipVideoSegment
            {
                StartDate = currentVideoFile.StartDate,
                EndDate = currentVideoFile.StartDate.Add(currentVideoFile.Duration),
                CameraFront = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.Front),
                CameraLeftRepeater = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.LeftRepeater),
                CameraRightRepeater = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.RightRepeater),
                CameraLeftBPillar = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.LeftBPillar),
                CameraRightBPillar = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.RightBPillar),
                CameraBack = segmentVideos.FirstOrDefault(v => v.Camera == Cameras.Back)
            };

            currentClipSegments.Add(segment);

            // Set i to the video after the last video in this clip segment, ie: the first video of the next segment.
            i = i + segmentVideos.Count + 1;

            // No more recent video files
            if (i >= recentVideoFiles.Count)
            {
                yield return new Clip(ClipType.Recent, currentClipSegments.ToArray())
                {
                    ThumbnailUrl = NoThumbnailImageUrl
                };
                currentClipSegments.Clear();
                yield break;
            }

            const int segmentVideoGapToleranceInSeconds = 5;
            var nextSegmentFirstVideo = recentVideoFiles[i];
            // Next video is within X seconds of last video of current segment, continue building clip segments
            if (nextSegmentFirstVideo.StartDate <= segment.EndDate.AddSeconds(segmentVideoGapToleranceInSeconds))
                continue;

            // Next video is more than X seconds, assume it's a new recent video clip
            yield return new Clip(ClipType.Recent, currentClipSegments.ToArray())
            {
                ThumbnailUrl = NoThumbnailImageUrl
            };
            currentClipSegments.Clear();
        }
    }

    private async Task<VideoFile> TryParseVideoFileAsync(string path, Match regexMatch)
    {
        try
        {
            return await ParseVideoFileAsync(path, regexMatch);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to parse info for video file from path: {Path}", path);
            return null;
        }
    }

    private async Task<VideoFile> ParseVideoFileAsync(string path, Match regexMatch)
    {
        await _ffprobeSemaphore.WaitAsync();
        try
        {
            var clipType = regexMatch.Groups["type"].Value switch
            {
                "RecentClips" => ClipType.Recent,
                "SavedClips" => ClipType.Saved,
                "SentryClips" => ClipType.Sentry,
                _ => ClipType.Unknown
            };

            var cameraToken = regexMatch.Groups["camera"].Value;
            var camera = cameraToken switch
            {
                "back" => Cameras.Back,
                "front" => Cameras.Front,
                "left_repeater" => Cameras.LeftRepeater,
                "right_repeater" => Cameras.RightRepeater,
                // Support pillar camera file names (various common namings)
                "left_pillar" => Cameras.LeftBPillar,
                "right_pillar" => Cameras.RightBPillar,
                "left_bpillar" => Cameras.LeftBPillar,
                "right_bpillar" => Cameras.RightBPillar,
                "leftpillar" => Cameras.LeftBPillar,
                "rightpillar" => Cameras.RightBPillar,
                _ => Cameras.Unknown
            };

            var date = new DateTime(
                int.Parse(regexMatch.Groups["vyear"].Value),
                int.Parse(regexMatch.Groups["vmonth"].Value),
                int.Parse(regexMatch.Groups["vday"].Value),
                int.Parse(regexMatch.Groups["vhour"].Value),
                int.Parse(regexMatch.Groups["vminute"].Value),
                int.Parse(regexMatch.Groups["vsecond"].Value));

            var duration = await _ffProbeService.GetVideoFileDurationAsync(path);
            if (!duration.HasValue)
            {
                Log.Error("Failed to get duration for video file {Path}", path);
                return null;
            }

            var eventFolderName = clipType != ClipType.Recent
                ? regexMatch.Groups["event"].Value
                : null;

            return new VideoFile
            {
                FilePath = path,
                Url = $"/Api/Video/{Uri.EscapeDataString(path)}",
                EventFolderName = eventFolderName,
                ClipType = clipType,
                StartDate = date,
                Camera = camera,
                Duration = duration.Value
            };
        }
        finally
        {
            _ffprobeSemaphore.Release();
        }
    }

    private static Clip ParseClip(string eventFolderName, IEnumerable<VideoFile> videoFiles)
    {
        var eventVideoFiles = videoFiles
            .AsParallel()
            .Where(v => v.EventFolderName == eventFolderName)
            .ToList();

        var segments = eventVideoFiles
            .GroupBy(v => v.StartDate)
            .AsParallel()
            .Select(g => new ClipVideoSegment
            {
                StartDate = g.Key,
                EndDate = g.Key.Add(g.First().Duration),
                CameraFront = g.FirstOrDefault(v => v.Camera == Cameras.Front),
                CameraLeftRepeater = g.FirstOrDefault(v => v.Camera == Cameras.LeftRepeater),
                CameraRightRepeater = g.FirstOrDefault(v => v.Camera == Cameras.RightRepeater),
                CameraLeftBPillar = g.FirstOrDefault(v => v.Camera == Cameras.LeftBPillar),
                CameraRightBPillar = g.FirstOrDefault(v => v.Camera == Cameras.RightBPillar),
                CameraBack = g.FirstOrDefault(v => v.Camera == Cameras.Back)
            })
            .ToArray();

        var eventFolderPath = Path.GetDirectoryName(eventVideoFiles.First().FilePath)!;
        var expectedEventJsonPath = Path.Combine(eventFolderPath, "event.json");
        var eventInfo = TryReadEvent(expectedEventJsonPath);

        var expectedEventThumbnailPath = Path.Combine(eventFolderPath, "thumb.png");
        var thumbnailUrl = File.Exists(expectedEventThumbnailPath)
            ? $"/Api/Thumbnail/{Uri.EscapeDataString(expectedEventThumbnailPath)}"
            : NoThumbnailImageUrl;

        return new Clip(eventVideoFiles.First().ClipType, segments)
        {
            DirectoryPath = eventFolderPath,
            Event = eventInfo,
            ThumbnailUrl = thumbnailUrl
        };
    }

    private static Event TryReadEvent(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Event>(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to read {EventJsonPath}", path);
            return null;
        }
    }

    /*
	 * \SavedClips\2023-06-16_17-18-06\2023-06-16_17-12-49-front.mp4"
	 * type = SavedClips
	 * event = 2023-06-16_17-18-06
	 * year = 2023
	 * month = 06
	 * day = 17
	 * hour = 18
	 * minute = 06
	 * vyear = 2023
	 * vmonth = 06
	 * vhour = 17
	 * vminute = 12
	 * vsecond = 49
	 * camera = front
	 */
    [GeneratedRegex(@"(?:[\\/]|^)(?<type>(?:Recent|Saved|Sentry)Clips)(?:[\\/](?<event>(?<year>20\d{2})\-(?<month>[0-1][0-9])\-(?<day>[0-3][0-9])_(?<hour>[0-2][0-9])\-(?<minute>[0-5][0-9])\-(?<second>[0-5][0-9])))?[\\/](?<vyear>20\d{2})\-(?<vmonth>[0-1][0-9])\-(?<vday>[0-3][0-9])_(?<vhour>[0-2][0-9])\-(?<vminute>[0-5][0-9])\-(?<vsecond>[0-5][0-9])\-(?<camera>back|front|left_repeater|right_repeater|left_pillar|right_pillar|left_bpillar|right_bpillar|leftpillar|rightpillar)\.mp4")]
    private static partial Regex FileNameRegexGenerated();
}
