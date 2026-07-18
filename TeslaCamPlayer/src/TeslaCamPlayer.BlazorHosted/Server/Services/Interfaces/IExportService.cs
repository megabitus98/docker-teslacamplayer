using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IExportService
{
    Task<string> StartExportAsync(ExportRequest request);
    ExportStatus GetStatus(string jobId);
    bool TryGetOutputPath(string jobId, out string path);
    bool Cancel(string jobId);
    Task<List<ExportItem>> ListExportsAsync();

    /// <summary>Deletes all export files matching <paramref name="jobId"/>. Returns false with the
    /// not-found reason in <paramref name="error"/>; IO failures propagate as exceptions.</summary>
    bool DeleteExport(string jobId, out string error);
}

