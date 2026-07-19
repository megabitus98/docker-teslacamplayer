using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IClipsService
{
    void InvalidateCache();

    /// <summary>
    /// Removes a deleted event folder from the clip index and invalidates the in-memory cache.
    /// </summary>
    Task RemoveEventAsync(string eventDir);

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
