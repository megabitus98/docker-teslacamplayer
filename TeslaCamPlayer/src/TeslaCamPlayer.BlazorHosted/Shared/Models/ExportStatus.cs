using System;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public enum ExportState
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}

public class ExportStatus
{
    public string JobId { get; set; }
    public ExportState State { get; set; }
    public double Percent { get; set; } // 0..100
    public TimeSpan? Eta { get; set; }
    public string OutputUrl { get; set; } // when completed
    public string ErrorMessage { get; set; }
}

