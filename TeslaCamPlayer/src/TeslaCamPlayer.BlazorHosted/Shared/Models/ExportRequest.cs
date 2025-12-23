using System;
using System.Collections.Generic;

namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class ExportRequest
{
    public string ClipDirectoryPath { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }

    // Cameras to include, in the exact on-screen order (row-major)
    public List<Cameras> OrderedCameras { get; set; } = new();
    public int GridColumns { get; set; }

    // Output settings
    public string Format { get; set; } = "mp4"; // mp4, mov
    public int? Width { get; set; } // null => auto/original
    public int? Height { get; set; } // null => auto/original
    public string Quality { get; set; } = "medium"; // low/medium/high

    // Overlays
    public bool IncludeTimestamp { get; set; } = true;
    public bool IncludeCameraLabels { get; set; } = true;
    public bool IncludeLocationOverlay { get; set; } = true;
    public bool IncludeSeiHud { get; set; } = true;
}
