namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class Clip
{
    public ClipType Type { get; }
    public ClipVideoSegment[] Segments { get; }
    public Event Event { get; init; }
    public DateTime StartDate { get; }
    public DateTime EndDate { get; }
    public double TotalSeconds { get; }
    public string ThumbnailUrl { get; init; }
    public string DirectoryPath { get; set; }

    /// <summary>True when any camera in this clip is still an encrypted (not-yet-decrypted) file.</summary>
    public bool IsEncrypted { get; }

    public Clip(ClipType type, ClipVideoSegment[] segments)
    {
        Type = type;
        Segments = segments.OrderBy(s => s.StartDate).ToArray();
        StartDate = segments.Min(s => s.StartDate);
        EndDate = segments.Max(s => s.EndDate);
        TotalSeconds = EndDate.Subtract(StartDate).TotalSeconds;
        IsEncrypted = Segments.Any(s => s.VideoFiles.Any(v => v is { IsEncrypted: true }));
    }

    public ClipVideoSegment SegmentAtDate(DateTime date)
        => Segments.FirstOrDefault(s => s.StartDate <= date && s.EndDate >= date);
}
