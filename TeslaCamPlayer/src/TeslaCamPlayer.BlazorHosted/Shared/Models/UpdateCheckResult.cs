namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class UpdateCheckResult
{
    public string Current { get; set; }
    public string Latest { get; set; }
    public string Url { get; set; }
    public bool UpdateAvailable { get; set; }
}
