using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Diagnostics;
using System.Web;
using TeslaCamPlayer.BlazorHosted.Server.Providers;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services;
using TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Controllers;

[ApiController]
[Route("Api/[action]")]
public class ApiController : ControllerBase
{
    private readonly IClipsService _clipsService;
    private readonly IRefreshProgressService _refreshProgressService;
    private readonly IExportService _exportService;
    private readonly ISettingsProvider _settingsProvider;
    private readonly ITeslaAuthService _teslaAuthService;
    private readonly IClipDecryptionService _clipDecryptionService;

    public ApiController(
        ISettingsProvider settingsProvider,
        IClipsService clipsService,
        IRefreshProgressService refreshProgressService,
        IExportService exportService,
        ITeslaAuthService teslaAuthService,
        IClipDecryptionService clipDecryptionService)
    {
        _settingsProvider = settingsProvider;
        _clipsService = clipsService;
        _refreshProgressService = refreshProgressService;
        _exportService = exportService;
        _teslaAuthService = teslaAuthService;
        _clipDecryptionService = clipDecryptionService;
    }

    [HttpGet]
    public async Task<Clip[]> GetClips(bool refreshCache = false)
        => await _clipsService.GetClipsAsync(refreshCache);

    [HttpGet]
    public async Task<ClipPagedResponse> GetClipsPaged(
        int skip = 0,
        int take = 50,
        [FromQuery] ClipType[]? types = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
        => await _clipsService.GetClipsPagedAsync(skip, take, types, fromDate, toDate);

    [HttpGet]
    public async Task<DateTime[]> GetAvailableDates([FromQuery] ClipType[]? types = null)
        => await _clipsService.GetAvailableDatesAsync(types);

    [HttpGet]
    public async Task<int> GetClipIndexByDate(DateTime date, [FromQuery] ClipType[]? types = null)
        => await _clipsService.GetClipIndexByDateAsync(date, types);

    [HttpGet]
    public RefreshStatus GetRefreshStatus()
        => _refreshProgressService.GetStatus();

    private bool TryGetRootFullPath(out string rootFullPath)
    {
        rootFullPath = string.Empty;
        var clipsRootPath = _settingsProvider.Settings.ClipsRootPath;
        if (string.IsNullOrWhiteSpace(clipsRootPath))
        {
            return false;
        }

        try
        {
            rootFullPath = EnsureTrailingSeparator(Path.GetFullPath(clipsRootPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool IsUnderRootPath(string path, string rootFullPath)
    {
        var fullPath = Path.GetFullPath(path);
        var rootWithoutSeparator = rootFullPath.TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(rootWithoutSeparator, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet]
    public AppConfig GetConfig()
    {
        var settings = _settingsProvider.Settings;
        return new AppConfig { EnableDelete = settings.EnableDelete, SpeedUnit = settings.SpeedUnit };
    }

    [HttpGet]
    public async Task<TeslaConnectionStatus> TeslaStatus(CancellationToken cancellationToken)
        => await _teslaAuthService.ProbeAsync(cancellationToken);

    [HttpPost]
    public async Task<IActionResult> PrepareEvent(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest("Missing path");

        var fullPath = Path.GetFullPath(HttpUtility.UrlDecode(path));
        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        if (!IsUnderRootPath(fullPath, rootFullPath))
            return BadRequest("Invalid path");

        try
        {
            var clip = await _clipsService.PrepareEncryptedEventAsync(fullPath, cancellationToken);
            if (clip == null)
            {
                return Ok(new PrepareEventResponse
                {
                    Success = false,
                    ErrorCode = "DecryptFailed",
                    ErrorMessage = "Could not decrypt this event's clips."
                });
            }

            return Ok(new PrepareEventResponse { Success = true, Clip = clip });
        }
        catch (TeslaNotConnectedException ex)
        {
            return Ok(new PrepareEventResponse { Success = false, ErrorCode = "NotConnected", ErrorMessage = ex.Message });
        }
        catch (TeslaRefreshFailedException ex)
        {
            return Ok(new PrepareEventResponse { Success = false, ErrorCode = "RefreshFailed", ErrorMessage = ex.Message });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PrepareEvent failed for {Path}", fullPath);
            return Ok(new PrepareEventResponse { Success = false, ErrorCode = "DecryptFailed", ErrorMessage = "Decryption failed." });
        }
    }

    [HttpGet]
    public AppSettingsResponse GetAppSettings()
        => _settingsProvider.GetAppSettings();

    [HttpPost]
    public IActionResult SaveAppSettings([FromBody] SaveAppSettingsRequest request)
    {
        var result = _settingsProvider.SaveAppSettings(request);
        if (result.Success && result.RequiresClipRefresh)
        {
            _clipsService.InvalidateCache();
        }

        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpDelete]
    public IActionResult DeleteEvent(string path)
    {
        var settings = _settingsProvider.Settings;
        if (!settings.EnableDelete)
            return StatusCode(403, "Delete function is disabled");

        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        if (string.IsNullOrEmpty(path))
            return BadRequest("Invalid path");

        var fullPath = Path.GetFullPath(path);
        if (!IsUnderRootPath(fullPath, rootFullPath))
            return BadRequest("Invalid path");

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
                Log.Information("Deleted event folder: {Path}", fullPath);
                return Ok();
            }
            return NotFound("Directory not found");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error deleting directory: {ex.Message}");
        }
    }

    [HttpGet("{path}.mp4")]
    public IActionResult Video(string path)
        => ServeFile(path, ".mp4", "video/mp4", true);

    [HttpGet("{path}.png")]
    public IActionResult Thumbnail(string path)
        => ServeFile(path, ".png", "image/png");

    private IActionResult ServeFile(string path, string extension, string contentType, bool enableRangeProcessing = false)
    {
        path = HttpUtility.UrlDecode(path);
        path += extension;

        path = Path.GetFullPath(path);
        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        // Decrypted clips live in the cache directory, outside the clips root — allow those too.
        if (!IsUnderRootPath(path, rootFullPath) && !_clipDecryptionService.IsCachePath(path))
            return BadRequest($"File must be in subdirectory under \"{rootFullPath}\", but was \"{path}\"");

        if (!System.IO.File.Exists(path))
            return NotFound();

        return PhysicalFile(path, contentType, enableRangeProcessing);
    }

    private static (string location, string eventPath) TryReadExportMetadata(string path)
    {
        try
        {
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

    [HttpGet]
    public IActionResult ExportFile(string path)
    {
        path = System.Web.HttpUtility.UrlDecode(path);
        if (string.IsNullOrWhiteSpace(path)) return BadRequest();
        path = Path.GetFullPath(path);
        var exportsRoot = Path.GetFullPath(_settingsProvider.Settings.ExportRootPath);
        if (!path.StartsWith(exportsRoot)) return BadRequest("Invalid path");
        if (!System.IO.File.Exists(path)) return NotFound();
        var contentType = "application/octet-stream";
        var fileName = Path.GetFileName(path);
        var result = new PhysicalFileResult(path, contentType)
        {
            EnableRangeProcessing = true,
            FileDownloadName = fileName
        };
        return result;
    }

    public class ExportItem
    {
        public string FileName { get; set; }
        public string Url { get; set; }
        public long SizeBytes { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string JobId { get; set; }
        public ExportStatus Status { get; set; }
        public string Location { get; set; }
        public string EventPath { get; set; }
    }

    [HttpGet]
    public IActionResult ListExports()
    {
        var root = _settingsProvider.Settings.ExportRootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Ok(Array.Empty<ExportItem>());

        var items = new List<ExportItem>();
        foreach (var path in Directory.EnumerateFiles(root))
        {
            try
            {
                var fi = new FileInfo(path);
                var jobId = Path.GetFileNameWithoutExtension(fi.Name);
                var st = _exportService.GetStatus(jobId) ?? new ExportStatus
                {
                    JobId = jobId,
                    State = ExportState.Completed,
                    Percent = 100
                };

                string url;
                var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "exports");
                if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(wwwroot)))
                {
                    url = "/exports/" + fi.Name;
                }
                else
                {
                    url = $"/Api/ExportFile?path={Uri.EscapeDataString(Path.GetFullPath(path))}";
                }

                var (location, eventPath) = TryReadExportMetadata(fi.FullName);
                items.Add(new ExportItem
                {
                    FileName = fi.Name,
                    Url = url,
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
        return Ok(items);
    }

    [HttpPost]
    public async Task<IActionResult> StartExport([FromBody] ExportRequest request)
    {
        if (request == null)
            return BadRequest("Missing request");

        if (string.IsNullOrWhiteSpace(request.ClipDirectoryPath))
        {
            Log.Warning("StartExport called with empty clip path.");
            return BadRequest("Clip path is invalid");
        }

        // Validate path is under root
        var fullPath = Path.GetFullPath(request.ClipDirectoryPath);
        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        if (!IsUnderRootPath(fullPath, rootFullPath))
            return BadRequest("Clip path is invalid");

        request.ClipDirectoryPath = fullPath;
        var id = await _exportService.StartExportAsync(request);
        return Ok(new { jobId = id });
    }

    [HttpGet]
    public IActionResult ExportStatus(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return BadRequest();
        var st = _exportService.GetStatus(jobId);
        if (st == null) return NotFound();
        return Ok(st);
    }

    [HttpPost]
    public IActionResult CancelExport(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return BadRequest();
        var ok = _exportService.Cancel(jobId);
        return ok ? Ok() : NotFound();
    }

    [HttpDelete]
    public IActionResult DeleteExport(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return BadRequest("Job ID is required");

        try
        {
            var exportRoot = Path.GetFullPath(_settingsProvider.Settings.ExportRootPath);
            if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
                return NotFound("Export directory not found");

            // Find the export file by jobId
            var files = Directory.EnumerateFiles(exportRoot)
                .Where(f => Path.GetFileNameWithoutExtension(f).Equals(jobId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!files.Any())
                return NotFound("Export file not found");

            // Delete all matching files (should typically be just one)
            foreach (var file in files)
            {
                System.IO.File.Delete(file);
                Log.Information("Deleted export file: {File}", file);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting export file for job {JobId}", jobId);
            return StatusCode(500, $"Error deleting export: {ex.Message}");
        }
    }
}
