using System;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IExportService
{
    Task<string> StartExportAsync(ExportRequest request);
    ExportStatus GetStatus(string jobId);
    bool TryGetOutputPath(string jobId, out string path);
    bool Cancel(string jobId);
}

