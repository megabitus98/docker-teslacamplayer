using Google.Protobuf;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Serilog;
using System.Text;
using System.Web;
using TeslaCamPlayer.BlazorHosted.Server.Helpers;
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
    private readonly ISeiParserService _seiParser;
    private readonly IMp4TimingService _mp4Timing;

    public ApiController(
        ISettingsProvider settingsProvider,
        IClipsService clipsService,
        IRefreshProgressService refreshProgressService,
        IExportService exportService,
        ITeslaAuthService teslaAuthService,
        IClipDecryptionService clipDecryptionService,
        ISeiParserService seiParser,
        IMp4TimingService mp4Timing)
    {
        _settingsProvider = settingsProvider;
        _clipsService = clipsService;
        _refreshProgressService = refreshProgressService;
        _exportService = exportService;
        _teslaAuthService = teslaAuthService;
        _clipDecryptionService = clipDecryptionService;
        _seiParser = seiParser;
        _mp4Timing = mp4Timing;
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
            rootFullPath = PathSafety.EnsureTrailingSeparator(Path.GetFullPath(clipsRootPath));
            return true;
        }
        catch
        {
            return false;
        }
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

        if (!PathSafety.IsUnder(rootFullPath, fullPath))
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
    public async Task<IActionResult> DeleteEvent(string path)
    {
        var settings = _settingsProvider.Settings;
        if (!settings.EnableDelete)
            return StatusCode(403, "Delete function is disabled");

        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        if (string.IsNullOrEmpty(path))
            return BadRequest("Invalid path");

        var fullPath = Path.GetFullPath(path);
        if (!PathSafety.IsUnder(rootFullPath, fullPath))
            return BadRequest("Invalid path");

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
                await _clipsService.RemoveEventAsync(fullPath);
                Log.Information("Deleted event folder: {Path}", fullPath);
                return Ok();
            }
            return NotFound("Directory not found");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting event folder {Path}", fullPath);
            return StatusCode(500, "Error deleting directory");
        }
    }

    [HttpGet("{path}.mp4")]
    public IActionResult Video(string path)
        => ServeFile(path, ".mp4", "video/mp4", true);

    // Serves the per-frame SEI telemetry of a clip as JSON so the browser HUD doesn't have to
    // re-download and parse the whole MP4. Same route shape as Video: /Api/SeiData/{escaped}.mp4
    [HttpGet("{path}.mp4")]
    public async Task<IActionResult> SeiData(string path)
    {
        path = HttpUtility.UrlDecode(path) + ".mp4";
        path = Path.GetFullPath(path);
        if (!TryGetRootFullPath(out var rootFullPath))
            return BadRequest("Clips root path is not configured.");

        // Decrypted clips live in the cache directory, outside the clips root — allow those too.
        if (!PathSafety.IsUnder(rootFullPath, path) && !_clipDecryptionService.IsCachePath(path))
            return BadRequest("Invalid path");

        if (!System.IO.File.Exists(path))
            return NotFound();

        var messages = _seiParser.ExtractSeiMessages(path);
        var timeline = messages.Count > 0 ? await _mp4Timing.GetFrameTimelineAsync(path) : null;

        // Protobuf JSON (camelCase fields, enum value names, defaults included) — the exact
        // shape sei-hud.js normalizeTelemetry already understands.
        var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithFormatDefaultValues(true));
        var sb = new StringBuilder();
        sb.Append("{\"frameStartsMs\":");
        sb.Append(timeline?.FrameStartsMs != null ? JsonConvert.SerializeObject(timeline.FrameStartsMs) : "null");
        sb.Append(",\"frames\":[");
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(formatter.Format(messages[i]));
        }
        sb.Append("]}");
        return Content(sb.ToString(), "application/json");
    }

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
        if (!PathSafety.IsUnder(rootFullPath, path) && !_clipDecryptionService.IsCachePath(path))
            return BadRequest($"File must be in subdirectory under \"{rootFullPath}\", but was \"{path}\"");

        if (!System.IO.File.Exists(path))
            return NotFound();

        return PhysicalFile(path, contentType, enableRangeProcessing);
    }

    [HttpGet]
    public IActionResult ExportFile(string path)
    {
        path = System.Web.HttpUtility.UrlDecode(path);
        if (string.IsNullOrWhiteSpace(path)) return BadRequest();
        path = Path.GetFullPath(path);
        var exportsRoot = Path.GetFullPath(_settingsProvider.Settings.ExportRootPath);
        if (!PathSafety.IsUnder(exportsRoot, path)) return BadRequest("Invalid path");
        if (!System.IO.File.Exists(path)) return NotFound();
        // Same serving mechanism as ServeFile: PhysicalFile helper with range processing.
        return PhysicalFile(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
    }

    [HttpGet]
    public async Task<IActionResult> ListExports()
        => Ok(await _exportService.ListExportsAsync());

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

        if (!PathSafety.IsUnder(rootFullPath, fullPath))
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
            return _exportService.DeleteExport(jobId, out var error) ? Ok() : NotFound(error);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deleting export file for job {JobId}", jobId);
            return StatusCode(500, "Error deleting export");
        }
    }
}
