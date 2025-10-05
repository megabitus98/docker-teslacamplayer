using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IClipIndexRepository
{
    Task<IReadOnlyList<VideoFile>> LoadVideoFilesAsync();
    Task ResetAsync();
    Task UpsertVideoFilesAsync(IEnumerable<VideoFile> videoFiles);
    Task RemoveByDirectoriesAsync(IEnumerable<string> directories);
}
