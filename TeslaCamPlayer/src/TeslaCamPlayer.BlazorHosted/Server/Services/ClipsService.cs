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
    private readonly IClipIndexRepository _clipIndexRepository;

    // State for background progressive refresh
    private static readonly object _refreshGate = new();
    private static Task _refreshTask;
    private static RefreshMode? _pendingFullRefresh;

    private enum RefreshMode { Incremental, Full }

    public ClipsService(
        ISettingsProvider settingsProvider,
        IFfProbeService ffProbeService,
        IRefreshProgressService refreshProgressService,
        IClipIndexRepository clipIndexRepository)
    {
        _settingsProvider = settingsProvider;
        _ffProbeService = ffProbeService;
        _refreshProgressService = refreshProgressService;
        _clipIndexRepository = clipIndexRepository;
        // Native MP4 mvhd parsing reads ~256 KiB from each file. On spinning-disk arrays, more
        // concurrent random reads cause head thrashing rather than parallelism — match
        // ProcessorCount, which empirically balances CPU-bound parse work and disk seek pressure.
        int semaphoreCount = Environment.ProcessorCount;
        _ffprobeSemaphore = new SemaphoreSlim(semaphoreCount);
    }

    public void InvalidateCache()
    {
        _cache = null;
    }

    private async Task<Clip[]> GetCachedAsync()
    {
        IReadOnlyList<VideoFile> stored;
        try
        {
            stored = await _clipIndexRepository.LoadVideoFilesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read clip cache from SQLite database.");
            return null;
        }

        if (stored == null || stored.Count == 0)
        {
            return null;
        }

        var cache = BuildClipCache(stored, out var prunedDirectories);

        if (prunedDirectories.Length > 0)
        {
            try
            {
                await _clipIndexRepository.RemoveByDirectoriesAsync(prunedDirectories);
            }
            catch (Exception pruneEx)
            {
                Log.Error(pruneEx, "Failed to prune missing clip directories from cache database.");
            }
        }

        return cache;
    }

    public async Task<Clip[]> GetClipsAsync(bool refreshCache = false)
    {
        _cache ??= await GetCachedAsync();

        // If we already have a cache and caller didn't request refresh, return fast
        if (!refreshCache && _cache != null)
            return _cache;

        // Incremental is a fast pass that only adds files newer than the latest in the DB.
        // When the DB is empty, it has nothing to skip past — the queued full refresh handles
        // the initial population. Running both on cold start would full-refresh twice.
        if (_cache is { Length: > 0 })
            StartBackgroundRefreshIfNeeded(RefreshMode.Incremental);
        StartBackgroundRefreshIfNeeded(RefreshMode.Full);

        // Ensure we always return a non-null array for the first paint
        _cache ??= Array.Empty<Clip>();
        return _cache;
    }

    public async Task<ClipPagedResponse> GetClipsPagedAsync(
        int skip,
        int take,
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        // Get total count for pagination
        var totalCount = await _clipIndexRepository.GetTotalEventCountAsync(clipTypes, fromDate, toDate);

        if (totalCount == 0)
        {
            return new ClipPagedResponse
            {
                Items = Array.Empty<Clip>(),
                TotalCount = 0
            };
        }

        // Get paginated event folders
        var eventFolders = await _clipIndexRepository.GetDistinctEventFoldersPagedAsync(
            skip, take, clipTypes, fromDate, toDate);

        if (eventFolders.Count == 0)
        {
            return new ClipPagedResponse
            {
                Items = Array.Empty<Clip>(),
                TotalCount = totalCount
            };
        }

        // Load video files for those event folders
        var folderNames = eventFolders
            .Where(f => !string.IsNullOrEmpty(f.EventFolder))
            .Select(f => f.EventFolder)
            .ToList();

        var videoFiles = await _clipIndexRepository.LoadVideoFilesByEventFoldersAsync(folderNames);

        // Build clips from the video files
        var clips = BuildClipsFromVideoFiles(videoFiles, eventFolders);

        return new ClipPagedResponse
        {
            Items = clips,
            TotalCount = totalCount
        };
    }

    public async Task<DateTime[]> GetAvailableDatesAsync(ClipType[]? clipTypes = null)
    {
        var dates = await _clipIndexRepository.GetAvailableDatesAsync(clipTypes);
        return dates.ToArray();
    }

    public async Task<int> GetClipIndexByDateAsync(DateTime date, ClipType[]? clipTypes = null)
    {
        return await _clipIndexRepository.GetEventIndexByDateAsync(date, clipTypes);
    }

    private Clip[] BuildClipsFromVideoFiles(
        IReadOnlyList<VideoFile> videoFiles,
        IReadOnlyList<EventFolderInfo> eventFolders)
    {
        if (videoFiles == null || videoFiles.Count == 0)
        {
            return Array.Empty<Clip>();
        }

        // Group video files by event folder
        var videosByFolder = videoFiles
            .GroupBy(v => v.EventFolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key ?? "", g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Build clips in the order of event folders (already sorted by latest date)
        var clips = new List<Clip>(eventFolders.Count);
        foreach (var folderInfo in eventFolders)
        {
            if (string.IsNullOrEmpty(folderInfo.EventFolder))
            {
                continue;
            }

            if (!videosByFolder.TryGetValue(folderInfo.EventFolder, out var folderVideos) || folderVideos.Count == 0)
            {
                continue;
            }

            var clip = BuildClipFromEventVideos(folderVideos, folderInfo.ClipType);
            if (clip != null)
            {
                clips.Add(clip);
            }
        }

        return clips.ToArray();
    }

    private Clip BuildClipFromEventVideos(List<VideoFile> eventVideoFiles, ClipType clipType)
    {
        if (eventVideoFiles == null || eventVideoFiles.Count == 0)
        {
            return null;
        }

        var segments = eventVideoFiles
            .GroupBy(v => v.StartDate)
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

        var eventFolderPath = Path.GetDirectoryName(eventVideoFiles.First().FilePath);
        if (string.IsNullOrEmpty(eventFolderPath))
        {
            return null;
        }

        var expectedEventJsonPath = Path.Combine(eventFolderPath, "event.json");
        var eventInfo = TryReadEvent(expectedEventJsonPath);

        var expectedEventThumbnailPath = Path.Combine(eventFolderPath, "thumb.png");
        var thumbnailUrl = File.Exists(expectedEventThumbnailPath)
            ? $"/Api/Thumbnail/{Uri.EscapeDataString(expectedEventThumbnailPath)}"
            : NoThumbnailImageUrl;

        return new Clip(clipType, segments)
        {
            DirectoryPath = eventFolderPath,
            Event = eventInfo,
            ThumbnailUrl = thumbnailUrl
        };
    }

    private void StartBackgroundRefreshIfNeeded(RefreshMode mode)
    {
        lock (_refreshGate)
        {
            if (mode == RefreshMode.Full)
            {
                _pendingFullRefresh = RefreshMode.Full;
            }

            if (_refreshTask != null && !_refreshTask.IsCompleted)
                return;

            var effectiveMode = _pendingFullRefresh.HasValue && mode != RefreshMode.Full
                ? mode // run the requested mode first, full will follow
                : (_pendingFullRefresh ?? mode);

            if (effectiveMode == RefreshMode.Full)
                _pendingFullRefresh = null;

            _refreshTask = Task.Run(async () =>
            {
                await RefreshCacheWorkerAsync(effectiveMode);

                // After completion, check if a full refresh was queued
                lock (_refreshGate)
                {
                    if (_pendingFullRefresh.HasValue)
                    {
                        var pending = _pendingFullRefresh.Value;
                        _pendingFullRefresh = null;
                        _refreshTask = Task.Run(async () => await RefreshCacheWorkerAsync(pending));
                    }
                }
            });
        }
    }

    private async Task RefreshCacheWorkerAsync(RefreshMode mode)
    {
        try
        {
            if (mode == RefreshMode.Incremental)
            {
                await RefreshIncrementalAsync();
            }
            else
            {
                await RefreshFullAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Event indexing ({Mode}) failed with an unhandled exception.", mode);
        }
        finally
        {
            _refreshProgressService.Complete();
        }
    }

    private async Task RefreshIncrementalAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settingsProvider.Settings;

        var maxTicks = await _clipIndexRepository.GetMaxStartTicksAsync();
        if (!maxTicks.HasValue)
        {
            // GetClipsAsync skips Incremental on cold start, but stay defensive in case some
            // other caller triggers it without a queued Full behind it.
            Log.Information("Incremental refresh: no existing data in database; deferring to full refresh.");
            return;
        }

        var cutoff = new DateTime(maxTicks.Value).AddHours(-1);
        Log.Information("Incremental refresh: scanning for events since {Cutoff}.", cutoff);

        var existing = _cache ?? Array.Empty<Clip>();
        var knownVideoFiles = new ConcurrentDictionary<string, VideoFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in existing
                     .SelectMany(c => c.Segments.SelectMany(s => s.VideoFiles))
                     .Where(v => v != null))
        {
            knownVideoFiles[video.FilePath] = video;
        }

        var candidatePaths = EnumerateCandidatePathsSince(settings, cutoff);
        var totalCandidates = candidatePaths.Count;
        Log.Information("Incremental refresh: found {CandidateCount} candidate video files since {Cutoff}.", totalCandidates, cutoff);

        if (totalCandidates == 0)
        {
            Log.Information("Incremental refresh: no new files found. Completed in {Elapsed}.", stopwatch.Elapsed);
            return;
        }

        _refreshProgressService.Start(totalCandidates, "incremental");

        var batchResults = await ProcessBatchInternalAsync(candidatePaths, knownVideoFiles);

        if (batchResults.Count > 0)
        {
            await _clipIndexRepository.UpsertVideoFilesAsync(batchResults);
        }

        // Merge new files into the existing in-memory cache instead of re-loading the entire DB.
        var existingVideoFiles = (_cache ?? Array.Empty<Clip>())
            .SelectMany(c => c.Segments.SelectMany(s => s.VideoFiles))
            .Where(v => v != null);

        var mergedByPath = new Dictionary<string, VideoFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existingVideoFiles)
            mergedByPath[v.FilePath] = v;
        foreach (var v in batchResults)
            mergedByPath[v.FilePath] = v;

        var cache = BuildClipCache(mergedByPath.Values, knownDirectories: null, out var prunedDirectories);
        _cache = cache;

        if (prunedDirectories.Length > 0)
        {
            await _clipIndexRepository.RemoveByDirectoriesAsync(prunedDirectories);
        }

        stopwatch.Stop();
        Log.Information(
            "Incremental refresh completed in {Elapsed}. Processed {ProcessedCount} files, {NewCount} new/updated.",
            stopwatch.Elapsed,
            totalCandidates,
            batchResults.Count);
    }

    private async Task RefreshFullAsync()
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

        var candidatePaths = EnumerateCandidatePaths(settings);
        var totalCandidates = candidatePaths.Count;
        Log.Information(
            "Full event indexing started with {CandidateCount} candidate video files. Target batch size {BatchSize}, minimum batch size {MinBatchSize}, memory threshold {MemoryThreshold:P2}.",
            totalCandidates,
            initialBatchSize,
            minBatchSize,
            settings.IndexingMaxMemoryUtilization);

        // Get all currently indexed paths to detect stale entries later
        var indexedPaths = await _clipIndexRepository.GetAllFilePathsAsync();

        _refreshProgressService.Start(totalCandidates, "full");

        var candidatePathSet = new HashSet<string>(candidatePaths, StringComparer.OrdinalIgnoreCase);
        var aggregatedResults = new List<VideoFile>();
        var pendingPaths = new List<string>(initialBatchSize);
        var currentBatchSize = initialBatchSize;
        var batchNumber = 0;

        foreach (var path in candidatePaths)
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

        // Remove stale entries (files in DB but no longer on disk)
        var stalePaths = indexedPaths.Where(p => !candidatePathSet.Contains(p)).ToList();
        if (stalePaths.Count > 0)
        {
            Log.Information("Full refresh: removing {StaleCount} stale entries from database.", stalePaths.Count);
            await _clipIndexRepository.RemoveByFilePathsAsync(stalePaths);
        }

        // We just enumerated the disk — reuse those directory names instead of stat-ing every clip.
        var knownDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in candidatePaths)
        {
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir))
                knownDirectories.Add(NormalizeDirectoryPath(dir));
        }

        await UpdateCacheAsync(aggregatedResults, knownDirectories);

        stopwatch.Stop();

        var totalEventCount = aggregatedResults
            .Select(v => v?.EventFolderName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Log.Information(
            "Full event indexing completed in {Elapsed}. Indexed {VideoCount} video files across {EventCount} events using {BatchCount} batches. Removed {StaleCount} stale entries.",
            stopwatch.Elapsed,
            aggregatedResults.Count,
            totalEventCount,
            batchNumber,
            stalePaths.Count);
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

        if (batchResults.Count > 0)
        {
            await _clipIndexRepository.UpsertVideoFilesAsync(batchResults);
        }

        // Cache rebuild is deferred until the end of the refresh — rebuilding per batch is O(N²)
        // and triggers Directory.Exists per cached event clip every time.
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

    private List<string> EnumerateCandidatePaths(Settings settings)
    {
        var rootPath = settings.ClipsRootPath;
        Log.Information("Scanning for video files in {ClipsRootPath}...", rootPath);

        if (!Directory.Exists(rootPath))
        {
            Log.Warning("ClipsRootPath does not exist: {ClipsRootPath}", rootPath);
            return new List<string>();
        }

        var allMp4Files = Directory.EnumerateFiles(rootPath, "*.mp4", SearchOption.AllDirectories).ToList();
        Log.Information("Found {TotalMp4Count} .mp4 files in {ClipsRootPath}.", allMp4Files.Count, rootPath);

        if (allMp4Files.Count == 0)
            return new List<string>();

        var matched = new List<string>();
        var skippedSamples = new List<string>();

        foreach (var path in allMp4Files)
        {
            if (FileNameRegex.IsMatch(path))
            {
                matched.Add(path);
            }
            else if (skippedSamples.Count < 5)
            {
                skippedSamples.Add(path);
            }
        }

        if (skippedSamples.Count > 0)
        {
            Log.Warning(
                "Skipped {SkippedCount} .mp4 files that did not match the expected TeslaCam naming pattern. Samples: {Samples}",
                allMp4Files.Count - matched.Count,
                skippedSamples);
        }

        Log.Information("Matched {MatchedCount} of {TotalCount} .mp4 files to TeslaCam naming pattern.", matched.Count, allMp4Files.Count);

        return matched;
    }

    // Matches event folder names; HH-MM-SS suffix is optional to align with FileNameRegexGenerated.
    private static readonly Regex FolderTimestampRegex = new(
        @"^(?<year>20\d{2})\-(?<month>[0-1][0-9])\-(?<day>[0-3][0-9])(?:_(?<hour>[0-2][0-9])(?:\-(?<minute>[0-5][0-9])(?:\-(?<second>[0-5][0-9]))?)?)?$",
        RegexOptions.Compiled);

    private static readonly Regex FileTimestampRegex = new(
        @"(?<year>20\d{2})\-(?<month>[0-1][0-9])\-(?<day>[0-3][0-9])_(?<hour>[0-2][0-9])\-(?<minute>[0-5][0-9])\-(?<second>[0-5][0-9])\-(?:back|front|left_repeater|right_repeater|left_pillar|right_pillar|left_bpillar|right_bpillar|leftpillar|rightpillar)\.mp4$",
        RegexOptions.Compiled);

    private List<string> EnumerateCandidatePathsSince(Settings settings, DateTime cutoff)
    {
        var rootPath = settings.ClipsRootPath;
        Log.Information("Incremental scan: looking for files since {Cutoff} in {ClipsRootPath}...", cutoff, rootPath);

        if (!Directory.Exists(rootPath))
        {
            Log.Warning("ClipsRootPath does not exist: {ClipsRootPath}", rootPath);
            return new List<string>();
        }

        var matched = new List<string>();
        var clipTypeDirs = new[] { "SavedClips", "SentryClips", "RecentClips" };

        foreach (var clipTypeDir in clipTypeDirs)
        {
            var clipTypePath = Path.Combine(rootPath, clipTypeDir);
            if (!Directory.Exists(clipTypePath))
                continue;

            if (clipTypeDir == "RecentClips")
            {
                // RecentClips has no event subfolders — files are directly in the folder
                foreach (var filePath in Directory.EnumerateFiles(clipTypePath, "*.mp4"))
                {
                    var fileName = Path.GetFileName(filePath);
                    var fileMatch = FileTimestampRegex.Match(fileName);
                    if (!fileMatch.Success)
                        continue;

                    var fileDate = ParseTimestampFromMatch(fileMatch);
                    if (fileDate >= cutoff && FileNameRegex.IsMatch(filePath))
                    {
                        matched.Add(filePath);
                    }
                }
            }
            else
            {
                // SavedClips/SentryClips have event subfolders named with timestamps
                foreach (var eventDir in Directory.EnumerateDirectories(clipTypePath))
                {
                    var folderName = Path.GetFileName(eventDir);
                    var folderMatch = FolderTimestampRegex.Match(folderName);
                    if (!folderMatch.Success)
                        continue;

                    var folderDate = ParseTimestampFromMatch(folderMatch);
                    if (folderDate < cutoff)
                        continue;

                    foreach (var filePath in Directory.EnumerateFiles(eventDir, "*.mp4"))
                    {
                        if (FileNameRegex.IsMatch(filePath))
                        {
                            matched.Add(filePath);
                        }
                    }
                }
            }
        }

        Log.Information("Incremental scan: matched {MatchedCount} files since {Cutoff}.", matched.Count, cutoff);
        return matched;
    }

    private static DateTime ParseTimestampFromMatch(Match match)
    {
        return new DateTime(
            int.Parse(match.Groups["year"].Value),
            int.Parse(match.Groups["month"].Value),
            int.Parse(match.Groups["day"].Value),
            int.Parse(match.Groups["hour"].Value),
            int.Parse(match.Groups["minute"].Value),
            int.Parse(match.Groups["second"].Value));
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

    private async Task UpdateCacheAsync(IReadOnlyCollection<VideoFile> videoFiles, HashSet<string> knownDirectories = null)
    {
        try
        {
            var cache = BuildClipCache(videoFiles, knownDirectories, out var prunedDirectories);
            _cache = cache;

            if (prunedDirectories.Length > 0)
            {
                await _clipIndexRepository.RemoveByDirectoriesAsync(prunedDirectories);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update clip cache during indexing.");
        }
    }

    private Clip[] BuildClipCache(IEnumerable<VideoFile> videoFiles, out string[] prunedDirectories)
        => BuildClipCache(videoFiles, knownDirectories: null, out prunedDirectories);

    private Clip[] BuildClipCache(IEnumerable<VideoFile> videoFiles, HashSet<string> knownDirectories, out string[] prunedDirectories)
    {
        if (videoFiles == null)
        {
            prunedDirectories = Array.Empty<string>();
            return Array.Empty<Clip>();
        }

        var snapshot = videoFiles
            .Where(v => v != null)
            .ToArray();

        if (snapshot.Length == 0)
        {
            prunedDirectories = Array.Empty<string>();
            return Array.Empty<Clip>();
        }

        var recentClips = GetRecentClips(snapshot
            .Where(v => v.ClipType == ClipType.Recent)
            .ToList()).ToList();

        var eventClips = snapshot
            .Select(v => v.EventFolderName)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .AsParallel()
            .Select(e => ParseClip(e, snapshot))
            .ToArray();

        var combined = eventClips
            .Concat(recentClips.AsParallel())
            .OrderByDescending(c => c.StartDate)
            .ToArray();

        var sanitized = PruneMissingEventClips(combined, knownDirectories, out prunedDirectories);
        return sanitized;
    }

    private Clip[] PruneMissingEventClips(Clip[] clips, HashSet<string> knownDirectories, out string[] removedDirectories)
    {
        removedDirectories = Array.Empty<string>();

        if (clips == null)
        {
            return Array.Empty<Clip>();
        }

        if (clips.Length == 0)
        {
            return clips;
        }

        var filtered = new List<Clip>(clips.Length);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clip in clips)
        {
            if (clip == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(clip.DirectoryPath))
            {
                filtered.Add(clip);
                continue;
            }

            // Prefer the set we just built from the disk enumeration — avoids a syscall per clip.
            // When that's not available (initial load), fall back to a real Directory.Exists.
            var normalized = NormalizeDirectoryPath(clip.DirectoryPath);
            var exists = knownDirectories != null
                ? knownDirectories.Contains(normalized)
                : Directory.Exists(clip.DirectoryPath);

            if (exists)
            {
                filtered.Add(clip);
                continue;
            }

            if (missing.Add(normalized))
            {
                Log.Information("Removed cached event at {DirectoryPath} because it no longer exists on disk.", clip.DirectoryPath);
            }
        }

        if (missing.Count == 0)
        {
            return clips;
        }

        removedDirectories = missing.ToArray();
        return filtered.ToArray();
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.TrimEnd(Path.DirectorySeparatorChar);
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
    // Event folder timestamp components after the date are optional: some Tesla firmwares emit
    // folders like "2025-12-22" or "2025-12-22_16" without the full HH-MM-SS suffix.
    // Mirrors the upstream fix at TylerB260/docker-teslacamplayer@1991c9c1.
    [GeneratedRegex(@"(?:[\\/]|^)(?<type>(?:Recent|Saved|Sentry)Clips)(?:[\\/](?<event>(?<year>20\d{2})\-(?<month>[0-1][0-9])\-(?<day>[0-3][0-9])_?(?<hour>[0-2][0-9])?\-?(?<minute>[0-5][0-9])?\-?(?<second>[0-5][0-9])?))?[\\/](?<vyear>20\d{2})\-(?<vmonth>[0-1][0-9])\-(?<vday>[0-3][0-9])_(?<vhour>[0-2][0-9])\-(?<vminute>[0-5][0-9])\-(?<vsecond>[0-5][0-9])\-(?<camera>back|front|left_repeater|right_repeater|left_pillar|right_pillar|left_bpillar|right_bpillar|leftpillar|rightpillar)\.mp4")]
    private static partial Regex FileNameRegexGenerated();
}
