namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>
/// Snapshot of the Tesla dashcam credential state, surfaced to the client so it can prompt the
/// user to connect / reconnect and explain decryption availability.
/// </summary>
public class TeslaConnectionStatus
{
    /// <summary>A refresh token is configured (via env, appsettings, or the WebUI).</summary>
    public bool HasToken { get; set; }

    /// <summary>A usable access token was obtained (token valid and refresh working).</summary>
    public bool Connected { get; set; }

    /// <summary>The access token's audience includes the dashcam decrypt service.</summary>
    public bool AudienceOk { get; set; }

    /// <summary>The access token carries the x-enc claim the key service needs to unwrap FEKs.</summary>
    public bool HasXEnc { get; set; }

    /// <summary>Last auth error, if any (e.g. refresh rejected — user must reconnect).</summary>
    public string LastError { get; set; }
}
