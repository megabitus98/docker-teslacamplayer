using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Web;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;
using TeslaCamPlayer.BlazorHosted.Server.Providers;

namespace TeslaCamPlayer.BlazorHosted.Server.Controllers;

[ApiController]
[Route("Api/[action]")]
public class ApiController : ControllerBase
{
    private readonly IClipsService _clipsService;
    private readonly IRefreshProgressService _refreshProgressService;
    private readonly IExportService _exportService;
    private readonly ISettingsProvider _settingsProvider;
    private readonly string _rootFullPath;
    private readonly bool _enableDelete;

    public ApiController(ISettingsProvider settingsProvider, IClipsService clipsService, IRefreshProgressService refreshProgressService, IExportService exportService)
    {
        _rootFullPath = Path.GetFullPath(settingsProvider.Settings.ClipsRootPath);
        _enableDelete = settingsProvider.Settings.EnableDelete;
        _settingsProvider = settingsProvider;
        _clipsService = clipsService;
        _refreshProgressService = refreshProgressService;
        _exportService = exportService;
    }

    [HttpGet]
    public async Task<Clip[]> GetClips(bool refreshCache = false)
        => await _clipsService.GetClipsAsync(refreshCache);

    [HttpGet]
    public RefreshStatus GetRefreshStatus()
        => _refreshProgressService.GetStatus();

    private bool IsUnderRootPath(string path)
        => path.StartsWith(_rootFullPath);

    [HttpGet]
    public AppConfig GetConfig()
        => new AppConfig { EnableDelete = _enableDelete };

    [HttpDelete]
    public IActionResult DeleteEvent(string path)
    {
        if (!_enableDelete)
            return StatusCode(403, "Delete function is disabled");

        if (string.IsNullOrEmpty(path) || !IsUnderRootPath(path))
            return BadRequest("Invalid path");

        try
        {
            var fullPath = Path.Combine(_rootFullPath, path);
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
        if (!IsUnderRootPath(path))
            return BadRequest($"File must be in subdirectory under \"{_rootFullPath}\", but was \"{path}\"");

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
        if (!path.StartsWith(exportsRoot)) return BadRequest("Invalid path");
        if (!System.IO.File.Exists(path)) return NotFound();
        var contentType = "application/octet-stream";
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    public class ExportItem
    {
        public string FileName { get; set; }
        public string Url { get; set; }
        public long SizeBytes { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string JobId { get; set; }
        public ExportStatus Status { get; set; }
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

                items.Add(new ExportItem
                {
                    FileName = fi.Name,
                    Url = url,
                    SizeBytes = fi.Length,
                    CreatedUtc = fi.CreationTimeUtc,
                    JobId = jobId,
                    Status = st
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

        // Validate path is under root
        var fullPath = Path.GetFullPath(request.ClipDirectoryPath ?? string.Empty);
        if (!IsUnderRootPath(fullPath))
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
}
