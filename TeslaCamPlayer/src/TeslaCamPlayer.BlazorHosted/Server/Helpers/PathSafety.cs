namespace TeslaCamPlayer.BlazorHosted.Server.Helpers;

/// <summary>
/// Single source of truth for path containment guards. Callers pass already-GetFullPath'd roots.
/// </summary>
public static class PathSafety
{
    public static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    public static bool IsUnder(string rootFullPath, string candidateFullPath)
    {
        var fullPath = Path.GetFullPath(candidateFullPath);
        var rootWithSeparator = EnsureTrailingSeparator(rootFullPath);
        var rootWithoutSeparator = rootWithSeparator.TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(rootWithoutSeparator, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
