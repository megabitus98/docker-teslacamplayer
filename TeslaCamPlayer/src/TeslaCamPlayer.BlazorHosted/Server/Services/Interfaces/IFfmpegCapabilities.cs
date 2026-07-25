namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IFfmpegCapabilities
{
    /// <summary>Best working H.264 encoder, probed once on first use. libx264 is the guaranteed floor.</summary>
    string GetPreferredEncoder();
}
