namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class ClipPagedResponse
{
    public Clip[] Items { get; set; } = Array.Empty<Clip>();
    public int TotalCount { get; set; }
}
