namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

/// <summary>
/// Maps the TIME_FORMAT / DATE_FORMAT settings to .NET and strftime format strings.
/// One source of truth so the UI and the export timestamp burn-in always agree.
/// </summary>
public static class TimeFormats
{
    public const string DefaultDateFormat = "dd MMM yy";
    public const string DefaultTimeFormat = "12h";

    public static readonly string[] DateFormatOptions = { "dd MMM yy", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy" };
    public static readonly string[] TimeFormatOptions = { "12h", "24h" };

    public static string DatePattern(string dateFormat)
        => Array.IndexOf(DateFormatOptions, dateFormat) >= 0 ? dateFormat : DefaultDateFormat;

    public static string TimePattern(string timeFormat)
        => timeFormat == "24h" ? "HH:mm:ss" : "h:mm:ss tt";

    public static string ShortTimePattern(string timeFormat)
        => timeFormat == "24h" ? "HH:mm" : "h:mm tt";

    /// <summary>strftime equivalent for ffmpeg drawtext. Unescaped — callers escape ':' for drawtext.</summary>
    public static string StrftimeDate(string dateFormat) => DatePattern(dateFormat) switch
    {
        "yyyy-MM-dd" => "%Y-%m-%d",
        "MM/dd/yyyy" => "%m/%d/%Y",
        "dd/MM/yyyy" => "%d/%m/%Y",
        _ => "%d %b %y"
    };

    public static string StrftimeTime(string timeFormat)
        => timeFormat == "24h" ? "%H:%M:%S" : "%I:%M:%S %p";
}
