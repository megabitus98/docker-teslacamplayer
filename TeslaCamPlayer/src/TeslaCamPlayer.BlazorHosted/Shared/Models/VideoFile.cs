namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class VideoFile
{
    public string FilePath { get; init; }
    public string Url { get; init; }
    public string EventFolderName { get; init; }
    public ClipType ClipType { get; init; }
    public DateTime StartDate { get; init; }
    public Cameras Camera { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// True when the file on disk is a Tesla-encrypted clip that has not yet been decrypted.
    /// Encrypted files are indexed without a duration (ffprobe can't read them) and are decrypted
    /// on demand when the event is opened.
    /// </summary>
    public bool IsEncrypted { get; init; }

    public static string BuildApiUrl(string filePath) => $"/Api/Video/{Uri.EscapeDataString(filePath)}";
}
