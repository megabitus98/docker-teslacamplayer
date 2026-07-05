namespace TeslaCamPlayer.BlazorHosted.Shared.Models;

public class AppSettingsResponse
{
    public bool NeedsSetup { get; set; }
    public string SetupReason { get; set; }
    public AppSettingItem[] Settings { get; set; } = Array.Empty<AppSettingItem>();
}

public class AppSettingItem
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InputType { get; set; } = "text";
    public string[] Options { get; set; } = Array.Empty<string>();
    public string EnvVarName { get; set; }
    public string EnvValue { get; set; }
    public string SavedValue { get; set; }
    public string BaselineValue { get; set; }
    public string EffectiveValue { get; set; }
    public string Source { get; set; } = "default";
    public bool IsRequired { get; set; }

    /// <summary>
    /// When true the value is a secret (e.g. a token): the server never sends the real value to
    /// the client — a mask marker stands in — and the client renders a password field.
    /// </summary>
    public bool IsSecret { get; set; }
    public bool IsValid { get; set; } = true;
    public string ValidationMessage { get; set; }
}

public class SaveAppSettingsRequest
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] ResetKeys { get; set; } = Array.Empty<string>();
}

public class SaveAppSettingsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public bool RequiresClipRefresh { get; set; }
    public AppSettingsResponse Settings { get; set; } = new();
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
