using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

public interface ISettingsProvider
{
    Settings Settings { get; }

    AppSettingsResponse GetAppSettings();

    SaveAppSettingsResponse SaveAppSettings(SaveAppSettingsRequest request);
}
