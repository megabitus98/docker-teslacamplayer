namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>
/// Pure mapping from H.264 encoder + quality preset to ffmpeg args.
/// CRF only exists on libx264; each hardware encoder gets its rate-control equivalent.
/// </summary>
public static class ExportEncoder
{
    public const string Software = "libx264";

    /// <summary>Probe order. First working encoder wins; libx264 is the always-working floor.</summary>
    public static readonly string[] CandidatesInPriorityOrder =
    {
        "h264_videotoolbox",
        "h264_nvenc",
        "h264_qsv",
        "h264_vaapi",
        Software
    };

    public static IReadOnlyList<string> CodecArgs(string encoder, string quality)
    {
        var q = (quality ?? "").ToLowerInvariant();
        switch (encoder)
        {
            case "h264_nvenc":
                return new[] { "-c:v", "h264_nvenc", "-pix_fmt", "yuv420p", "-preset", "p5", "-rc", "vbr", "-cq", Pick(q, "18", "28", "23") };
            case "h264_qsv":
                return new[] { "-c:v", "h264_qsv", "-pix_fmt", "nv12", "-global_quality", Pick(q, "18", "28", "23") };
            case "h264_vaapi":
                // No -pix_fmt: frames are uploaded to the GPU via format=nv12,hwupload (see FilterSuffix).
                return new[] { "-c:v", "h264_vaapi", "-qp", Pick(q, "18", "28", "23") };
            case "h264_videotoolbox":
                return new[] { "-c:v", "h264_videotoolbox", "-pix_fmt", "yuv420p", "-q:v", Pick(q, "65", "45", "55") };
            default:
                // Byte-for-byte the pre-hwaccel software args.
                var (preset, crf) = q switch
                {
                    "high" => ("slow", "17"),
                    "low" => ("fast", "24"),
                    _ => ("medium", "20")
                };
                return new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", preset, "-crf", crf };
        }
    }

    /// <summary>Appended as an extra filter chain before mapping; vaapi encodes from GPU surfaces.</summary>
    public static string FilterSuffix(string encoder)
        => encoder == "h264_vaapi" ? ",format=nv12,hwupload" : "";

    /// <summary>Global argv tokens required before inputs (vaapi device init).</summary>
    public static IReadOnlyList<string> GlobalArgs(string encoder)
        => encoder == "h264_vaapi" ? new[] { "-vaapi_device", "/dev/dri/renderD128" } : Array.Empty<string>();

    private static string Pick(string quality, string high, string low, string medium)
        => quality switch { "high" => high, "low" => low, _ => medium };
}
