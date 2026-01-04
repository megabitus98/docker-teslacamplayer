using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IClipsService
{
    Task<Clip[]> GetClipsAsync(bool refreshCache = false);

    Task<ClipPagedResponse> GetClipsPagedAsync(
        int skip,
        int take,
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);

    Task<DateTime[]> GetAvailableDatesAsync(ClipType[]? clipTypes = null);
}
