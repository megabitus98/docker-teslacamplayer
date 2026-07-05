using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

public interface ITeslaAuthService
{
    /// <summary>True when a refresh token is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a valid dashcam access token, refreshing when near expiry (single-flight).
    /// Throws <see cref="TeslaNotConnectedException"/> when no token is configured and
    /// <see cref="TeslaRefreshFailedException"/> when the refresh is rejected.
    /// </summary>
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>Attempts to obtain a token and reports the resulting connection state (never throws).</summary>
    Task<TeslaConnectionStatus> ProbeAsync(CancellationToken cancellationToken = default);
}
