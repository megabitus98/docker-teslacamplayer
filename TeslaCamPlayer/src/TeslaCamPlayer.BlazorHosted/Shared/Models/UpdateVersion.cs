namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public static class UpdateVersion
{
    /// <summary>
    /// True when latest is a strictly newer parseable version than current.
    /// "v" prefixes tolerated; anything unparseable means no notification.
    /// </summary>
    public static bool IsNewer(string current, string latest)
        => TryParse(current, out var c) && TryParse(latest, out var l) && l > c;

    private static bool TryParse(string value, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Version.TryParse(value.Trim().TrimStart('v', 'V'), out version);
    }
}
