using System;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

// Moved verbatim from ApiController.ExportItem — property names, types, and declaration
// order preserved so the serialized JSON shape is unchanged.
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
