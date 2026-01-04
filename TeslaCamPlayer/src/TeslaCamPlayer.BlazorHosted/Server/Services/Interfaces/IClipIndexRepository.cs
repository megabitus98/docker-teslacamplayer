using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IClipIndexRepository
{
    Task<IReadOnlyList<VideoFile>> LoadVideoFilesAsync();
    Task ResetAsync();
    Task UpsertVideoFilesAsync(IEnumerable<VideoFile> videoFiles);
    Task RemoveByDirectoriesAsync(IEnumerable<string> directories);

    // Paginated queries for performance with large datasets
    Task<IReadOnlyList<EventFolderInfo>> GetDistinctEventFoldersPagedAsync(
        int skip,
        int take,
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);

    Task<IReadOnlyList<VideoFile>> LoadVideoFilesByEventFoldersAsync(IEnumerable<string> eventFolders);

    Task<int> GetTotalEventCountAsync(
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null);

    Task<IReadOnlyList<DateTime>> GetAvailableDatesAsync(ClipType[]? clipTypes = null);
}

public class EventFolderInfo
{
    public string EventFolder { get; set; } = string.Empty;
    public string? DirectoryPath { get; set; }
    public ClipType ClipType { get; set; }
    public long LatestTicks { get; set; }
}
