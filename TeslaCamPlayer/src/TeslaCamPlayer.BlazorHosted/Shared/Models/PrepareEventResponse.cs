namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>
/// Result of decrypting an encrypted event on demand. On success <see cref="Clip"/> is the rebuilt
/// clip (decrypted URLs + real durations); otherwise <see cref="ErrorCode"/> tells the client how to
/// respond (prompt to connect, reconnect, or show a decrypt failure).
/// </summary>
public class PrepareEventResponse
{
    public bool Success { get; set; }

    /// <summary>NotConnected | RefreshFailed | DecryptFailed — null on success.</summary>
    public string ErrorCode { get; set; }

    public string ErrorMessage { get; set; }

    public Clip Clip { get; set; }
}
