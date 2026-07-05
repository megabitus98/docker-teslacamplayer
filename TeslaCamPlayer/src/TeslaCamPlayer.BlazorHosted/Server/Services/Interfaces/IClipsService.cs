using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IClipsService
{
    void InvalidateCache();

    Task<Clip[]> GetClipsAsync(bool refreshCache = false);

    Task<ClipPagedResponse> GetClipsPagedAsync(
        int skip,
        int take,
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);

    Task<DateTime[]> GetAvailableDatesAsync(ClipType[]? clipTypes = null);

    Task<int> GetClipIndexByDateAsync(DateTime date, ClipType[]? clipTypes = null);

    /// <summary>
    /// Decrypts an encrypted event's clips on demand and returns the rebuilt clip with decrypted
    /// video URLs and real durations, or null when nothing playable resulted.
    /// </summary>
    Task<Clip> PrepareEncryptedEventAsync(string eventDir, CancellationToken cancellationToken = default);
}
