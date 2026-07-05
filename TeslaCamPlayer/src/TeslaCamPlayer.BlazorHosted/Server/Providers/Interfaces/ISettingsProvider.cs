using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

public interface ISettingsProvider
{
    Settings Settings { get; }

    AppSettingsResponse GetAppSettings();

    SaveAppSettingsResponse SaveAppSettings(SaveAppSettingsRequest request);

    /// <summary>
    /// Atomically writes a single persisted override and rebuilds effective settings. Used by
    /// the auth service to persist the rotated refresh token without a full validation pass.
    /// </summary>
    void SetPersistedValue(string key, string? value);
}
