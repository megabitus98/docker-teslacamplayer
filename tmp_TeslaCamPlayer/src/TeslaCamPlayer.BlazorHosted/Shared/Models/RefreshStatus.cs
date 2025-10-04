namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class RefreshStatus
{
    public bool IsRefreshing { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
}

