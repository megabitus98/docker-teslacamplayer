using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Web;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Controllers;

[ApiController]
[Route("Api/[action]")]
public class ApiController : ControllerBase
{
    private readonly IClipsService _clipsService;
    private readonly IRefreshProgressService _refreshProgressService;
    private readonly string _rootFullPath;
    private readonly bool _enableDelete;

    public ApiController(ISettingsProvider settingsProvider, IClipsService clipsService, IRefreshProgressService refreshProgressService)
    {
        _rootFullPath = Path.GetFullPath(settingsProvider.Settings.ClipsRootPath);
        _enableDelete = settingsProvider.Settings.EnableDelete;
        _clipsService = clipsService;
        _refreshProgressService = refreshProgressService;
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
}
