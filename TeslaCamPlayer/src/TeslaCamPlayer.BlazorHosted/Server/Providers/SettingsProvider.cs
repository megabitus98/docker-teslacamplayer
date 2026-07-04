using Microsoft.Extensions.Configuration;
using Serilog;
using System.Globalization;
using System.Text.Json;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Providers;

public class SettingsProvider : ISettingsProvider
{
    private const string SettingsFileName = "teslacamplayer.settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AppSettingDefinition[] Definitions = CreateDefinitions();

    private readonly object _gate = new();
    private readonly string _settingsFilePath;
    private Settings _settings;
    private PersistedSettings _persistedSettings;

    public SettingsProvider()
    {
        _settingsFilePath = GetSettingsFilePath();
        _persistedSettings = LoadPersistedSettings();
        _settings = BuildSettings(true, _persistedSettings.Values);
    }

    public Settings Settings
    {
        get
        {
            lock (_gate)
            {
                return Clone(_settings);
            }
        }
    }

    public AppSettingsResponse GetAppSettings()
    {
        lock (_gate)
        {
            var persisted = Clone(_persistedSettings);
            return BuildResponse(Clone(_settings), BuildSettings(false, null), persisted);
        }
    }

    public SaveAppSettingsResponse SaveAppSettings(SaveAppSettingsRequest request)
    {
        lock (_gate)
        {
            request ??= new SaveAppSettingsRequest();

            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var before = Clone(_settings);
            var persisted = Clone(_persistedSettings);
            var baseline = BuildSettings(false, null);
            var candidateOverrides = new Dictionary<string, string?>(persisted.Values, StringComparer.OrdinalIgnoreCase);
            var resetKeys = new HashSet<string>(request.ResetKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var key in resetKeys)
            {
                candidateOverrides.Remove(key);
            }

            foreach (var (key, rawValue) in request.Values ?? new Dictionary<string, string?>())
            {
                if (resetKeys.Contains(key))
                {
                    continue;
                }

                var definition = FindDefinition(key);
                if (definition == null)
                {
                    continue;
                }

                var parsed = definition.Parse(rawValue);
                if (!parsed.Success)
                {
                    errors[key] = parsed.Error ?? "Invalid value.";
                    continue;
                }

                var normalized = parsed.Value ?? string.Empty;
                var baselineValue = definition.GetValue(baseline);

                if (persisted.Values.ContainsKey(key) || !StringEquals(normalized, baselineValue))
                {
                    candidateOverrides[key] = normalized;
                }
                else
                {
                    candidateOverrides.Remove(key);
                }
            }

            var candidateBatchSize = GetCandidateInt(candidateOverrides, baseline, nameof(Settings.IndexingBatchSize));
            var candidateMinBatchSize = GetCandidateInt(candidateOverrides, baseline, nameof(Settings.IndexingMinBatchSize));
            if (candidateMinBatchSize > candidateBatchSize)
            {
                errors[nameof(Settings.IndexingMinBatchSize)] = "Minimum batch size cannot exceed batch size.";
            }

            var candidate = BuildSettings(true, candidateOverrides);
            ValidateSettings(candidate, createDirectories: true, errors);

            if (errors.Count > 0)
            {
                return new SaveAppSettingsResponse
                {
                    Success = false,
                    Message = "Some settings need attention.",
                    Errors = errors,
                    Settings = BuildResponse(Clone(_settings), baseline, persisted, errors)
                };
            }

            var nextPersisted = new PersistedSettings
            {
                IsConfigured = true,
                Values = candidateOverrides
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            };

            SavePersistedSettings(nextPersisted);
            _persistedSettings = nextPersisted;
            _settings = candidate;

            var requiresClipRefresh = Definitions
                .Where(d => d.CausesClipRefresh)
                .Any(d => !StringEquals(d.GetValue(before), d.GetValue(candidate)));

            return new SaveAppSettingsResponse
            {
                Success = true,
                Message = "Settings saved.",
                RequiresClipRefresh = requiresClipRefresh,
                Settings = BuildResponse(Clone(_settings), BuildSettings(false, null), Clone(_persistedSettings))
            };
        }
    }

    private AppSettingsResponse BuildResponse(
        Settings effectiveSettings,
        Settings baselineSettings,
        PersistedSettings persisted,
        Dictionary<string, string>? saveErrors = null)
    {
        var configuredJsonKeys = GetConfiguredJsonKeys();
        var items = Definitions
            .Select(definition => BuildItem(definition, effectiveSettings, baselineSettings, persisted, configuredJsonKeys, saveErrors))
            .ToArray();

        var clipsRoot = items.First(i => i.Key == nameof(Settings.ClipsRootPath));
        var needsSetup = !persisted.IsConfigured || !clipsRoot.IsValid;

        return new AppSettingsResponse
        {
            NeedsSetup = needsSetup,
            SetupReason = needsSetup
                ? !clipsRoot.IsValid
                    ? clipsRoot.ValidationMessage
                    : "Review and save the application settings once before using TeslaCamPlayer."
                : null,
            Settings = items
        };
    }

    private static AppSettingItem BuildItem(
        AppSettingDefinition definition,
        Settings effectiveSettings,
        Settings baselineSettings,
        PersistedSettings persisted,
        HashSet<string> configuredJsonKeys,
        Dictionary<string, string>? saveErrors)
    {
        persisted.Values.TryGetValue(definition.Key, out var savedValue);
        var envValue = GetEnvironmentValue(definition);
        var validationMessage = saveErrors != null && saveErrors.TryGetValue(definition.Key, out var saveError)
            ? saveError
            : ValidateSetting(definition.Key, effectiveSettings, createDirectories: false);

        return new AppSettingItem
        {
            Key = definition.Key,
            Label = definition.Label,
            Description = definition.Description,
            InputType = definition.InputType,
            Options = definition.Options,
            EnvVarName = definition.EnvVarNames.FirstOrDefault(),
            EnvValue = envValue,
            SavedValue = savedValue,
            BaselineValue = definition.GetValue(baselineSettings),
            EffectiveValue = definition.GetValue(effectiveSettings),
            Source = savedValue != null ? "WebUI override" : GetBaselineSource(definition, configuredJsonKeys),
            IsRequired = definition.IsRequired,
            IsValid = string.IsNullOrWhiteSpace(validationMessage),
            ValidationMessage = validationMessage
        };
    }

    private Settings BuildSettings(bool includeOverrides, Dictionary<string, string?>? overrides)
    {
        var settings = LoadJsonSettings();
        NormalizeSettings(settings);

        foreach (var definition in Definitions)
        {
            var raw = GetEnvironmentValue(definition);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parsed = definition.Parse(raw);
            if (parsed.Success)
            {
                definition.ApplyValue(settings, parsed.Value ?? string.Empty);
            }
        }

        NormalizeSettings(settings);

        if (includeOverrides && overrides != null)
        {
            foreach (var definition in Definitions)
            {
                if (!overrides.TryGetValue(definition.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var parsed = definition.Parse(raw);
                if (parsed.Success)
                {
                    definition.ApplyValue(settings, parsed.Value ?? string.Empty);
                }
            }
        }

        NormalizeSettings(settings);
        return settings;
    }

    private static Settings LoadJsonSettings()
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), optional: true);

        var configuration = builder.Build();
        return configuration.Get<Settings>() ?? new Settings();
    }

    private PersistedSettings LoadPersistedSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new PersistedSettings();
            }

            var json = File.ReadAllText(_settingsFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions) ?? new PersistedSettings();
            persisted.Values = new Dictionary<string, string?>(persisted.Values ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
            return persisted;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load WebUI settings from {Path}. Falling back to environment and appsettings.", _settingsFilePath);
            return new PersistedSettings();
        }
    }

    private void SavePersistedSettings(PersistedSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static string GetSettingsFilePath()
    {
        const string configDirectory = "/config";
        var directory = Directory.Exists(configDirectory)
            ? configDirectory
            : AppContext.BaseDirectory;

        return Path.Combine(directory, SettingsFileName);
    }

    private static HashSet<string> GetConfiguredJsonKeys()
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), optional: true);

        var configuration = builder.Build();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            if (!string.IsNullOrWhiteSpace(configuration[definition.Key]))
            {
                keys.Add(definition.Key);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration[nameof(Settings.CacheFilePath)]))
        {
            keys.Add(nameof(Settings.CacheDatabasePath));
        }

        return keys;
    }

    private static string GetBaselineSource(AppSettingDefinition definition, HashSet<string> configuredJsonKeys)
    {
        var envValue = GetEnvironmentValue(definition);
        if (!string.IsNullOrWhiteSpace(envValue) && definition.Parse(envValue).Success)
        {
            return "environment";
        }

        return configuredJsonKeys.Contains(definition.Key) ? "appsettings.json" : "default";
    }

    private static string? GetEnvironmentValue(AppSettingDefinition definition)
    {
        foreach (var envVarName in definition.EnvVarNames)
        {
            var value = Environment.GetEnvironmentVariable(envVarName);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static void NormalizeSettings(Settings settings)
    {
        if (settings.SpeedUnit != "kmh" && settings.SpeedUnit != "mph")
        {
            settings.SpeedUnit = "kmh";
        }

        if (string.IsNullOrWhiteSpace(settings.CacheDatabasePath))
        {
            if (!string.IsNullOrWhiteSpace(settings.CacheFilePath))
            {
                settings.CacheDatabasePath = Path.ChangeExtension(settings.CacheFilePath, ".db");
            }
            else
            {
                settings.CacheDatabasePath = Path.Combine(AppContext.BaseDirectory, "clips.db");
            }
        }

        if (string.IsNullOrWhiteSpace(settings.ExportRootPath))
        {
            settings.ExportRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "exports");
        }

        settings.ExportRetentionHours = Math.Max(0, settings.ExportRetentionHours);

        if (settings.IndexingBatchSize <= 0)
        {
            settings.IndexingBatchSize = 1000;
        }

        if (settings.IndexingMinBatchSize <= 0)
        {
            settings.IndexingMinBatchSize = Math.Min(250, settings.IndexingBatchSize);
        }

        settings.IndexingMinBatchSize = Math.Min(settings.IndexingMinBatchSize, settings.IndexingBatchSize);

        if (settings.IndexingMaxMemoryUtilization <= 0 || settings.IndexingMaxMemoryUtilization > 1)
        {
            settings.IndexingMaxMemoryUtilization = 0.85d;
        }

        if (settings.IndexingMemoryRecoveryDelaySeconds <= 0)
        {
            settings.IndexingMemoryRecoveryDelaySeconds = 5;
        }
    }

    private static void ValidateSettings(Settings settings, bool createDirectories, Dictionary<string, string> errors)
    {
        foreach (var definition in Definitions)
        {
            var error = ValidateSetting(definition.Key, settings, createDirectories);
            if (!string.IsNullOrWhiteSpace(error))
            {
                errors[definition.Key] = error;
            }
        }

        if (settings.IndexingMinBatchSize > settings.IndexingBatchSize)
        {
            errors[nameof(Settings.IndexingMinBatchSize)] = "Minimum batch size cannot exceed batch size.";
        }
    }

    private static string? ValidateSetting(string key, Settings settings, bool createDirectories)
    {
        try
        {
            return key switch
            {
                nameof(Settings.ClipsRootPath) => ValidateClipsRootPath(settings.ClipsRootPath),
                nameof(Settings.CacheDatabasePath) => ValidateFilePath(settings.CacheDatabasePath, createDirectories),
                nameof(Settings.ExportRootPath) => ValidateDirectoryPath(settings.ExportRootPath, createDirectories),
                _ => null
            };
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string? ValidateClipsRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Clips root path is required.";
        }

        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(fullPath)
            ? null
            : $"Clips root path does not exist or is not readable: {fullPath}";
    }

    private static string? ValidateFilePath(string path, bool createDirectories)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path is required.";
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "Path must include a directory.";
        }

        return EnsureWritableDirectory(directory, createDirectories);
    }

    private static string? ValidateDirectoryPath(string path, bool createDirectories)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path is required.";
        }

        return EnsureWritableDirectory(Path.GetFullPath(path), createDirectories);
    }

    private static string? EnsureWritableDirectory(string directory, bool createDirectories)
    {
        if (!Directory.Exists(directory))
        {
            if (!createDirectories)
            {
                return null;
            }

            Directory.CreateDirectory(directory);
        }

        if (!createDirectories)
        {
            return null;
        }

        var testPath = Path.Combine(directory, $".teslacamplayer-write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(testPath, string.Empty);
        File.Delete(testPath);
        return null;
    }

    private static Settings Clone(Settings settings)
        => new()
        {
            ClipsRootPath = settings.ClipsRootPath,
            CacheFilePath = settings.CacheFilePath,
            CacheDatabasePath = settings.CacheDatabasePath,
            EnableDelete = settings.EnableDelete,
            SpeedUnit = settings.SpeedUnit,
            ExportRootPath = settings.ExportRootPath,
            ExportRetentionHours = settings.ExportRetentionHours,
            IndexingBatchSize = settings.IndexingBatchSize,
            IndexingMinBatchSize = settings.IndexingMinBatchSize,
            IndexingMaxMemoryUtilization = settings.IndexingMaxMemoryUtilization,
            IndexingMemoryRecoveryDelaySeconds = settings.IndexingMemoryRecoveryDelaySeconds
        };

    private static PersistedSettings Clone(PersistedSettings settings)
        => new()
        {
            IsConfigured = settings.IsConfigured,
            Values = new Dictionary<string, string?>(settings.Values, StringComparer.OrdinalIgnoreCase)
        };

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static AppSettingDefinition? FindDefinition(string key)
        => Definitions.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    private static int GetCandidateInt(Dictionary<string, string?> overrides, Settings baseline, string key)
    {
        var definition = FindDefinition(key);
        if (definition == null)
        {
            return 0;
        }

        var value = overrides.TryGetValue(key, out var overrideValue)
            ? overrideValue
            : definition.GetValue(baseline);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static AppSettingDefinition[] CreateDefinitions()
        => new[]
        {
            new AppSettingDefinition(
                nameof(Settings.ClipsRootPath),
                "Clips root path",
                "Folder that contains SavedClips, SentryClips, and RecentClips.",
                "text",
                new[] { "ClipsRootPath", "CLIPS_ROOT_PATH" },
                settings => settings.ClipsRootPath,
                (settings, value) => settings.ClipsRootPath = value,
                ParseRequiredString,
                isRequired: true,
                causesClipRefresh: true),
            new AppSettingDefinition(
                nameof(Settings.CacheDatabasePath),
                "Cache database path",
                "SQLite database used for the clip index.",
                "text",
                new[] { "CACHE_DATABASE_PATH", "CacheDatabasePath" },
                settings => settings.CacheDatabasePath,
                (settings, value) => settings.CacheDatabasePath = value,
                ParseRequiredString,
                isRequired: true,
                causesClipRefresh: true),
            new AppSettingDefinition(
                nameof(Settings.EnableDelete),
                "Enable delete",
                "Allow deleting clip folders from the WebUI.",
                "boolean",
                new[] { "ENABLE_DELETE", "EnableDelete" },
                settings => settings.EnableDelete ? "true" : "false",
                (settings, value) => settings.EnableDelete = bool.Parse(value),
                ParseBool),
            new AppSettingDefinition(
                nameof(Settings.SpeedUnit),
                "Speed unit",
                "Speed display unit for telemetry overlays.",
                "select",
                new[] { "SPEED_UNIT", "SpeedUnit" },
                settings => settings.SpeedUnit,
                (settings, value) => settings.SpeedUnit = value,
                ParseSpeedUnit,
                options: new[] { "kmh", "mph" }),
            new AppSettingDefinition(
                nameof(Settings.ExportRootPath),
                "Export root path",
                "Folder where exported videos are written.",
                "text",
                new[] { "EXPORT_ROOT_PATH", "ExportRootPath" },
                settings => settings.ExportRootPath,
                (settings, value) => settings.ExportRootPath = value,
                ParseRequiredString,
                isRequired: true),
            new AppSettingDefinition(
                nameof(Settings.ExportRetentionHours),
                "Export retention hours",
                "Hours to keep exported videos. Use 0 to disable cleanup.",
                "integer",
                new[] { "EXPORT_RETENTION_HOURS", "ExportRetentionHours" },
                settings => settings.ExportRetentionHours.ToString(CultureInfo.InvariantCulture),
                (settings, value) => settings.ExportRetentionHours = int.Parse(value, CultureInfo.InvariantCulture),
                value => ParseInt(value, min: 0)),
            new AppSettingDefinition(
                nameof(Settings.IndexingBatchSize),
                "Indexing batch size",
                "Target number of video files processed per indexing batch.",
                "integer",
                new[] { "INDEXING_BATCH_SIZE", "IndexingBatchSize" },
                settings => settings.IndexingBatchSize.ToString(CultureInfo.InvariantCulture),
                (settings, value) => settings.IndexingBatchSize = int.Parse(value, CultureInfo.InvariantCulture),
                value => ParseInt(value, min: 1)),
            new AppSettingDefinition(
                nameof(Settings.IndexingMinBatchSize),
                "Indexing minimum batch size",
                "Smallest batch size used when memory pressure reduces indexing throughput.",
                "integer",
                new[] { "INDEXING_MIN_BATCH_SIZE", "IndexingMinBatchSize" },
                settings => settings.IndexingMinBatchSize.ToString(CultureInfo.InvariantCulture),
                (settings, value) => settings.IndexingMinBatchSize = int.Parse(value, CultureInfo.InvariantCulture),
                value => ParseInt(value, min: 1)),
            new AppSettingDefinition(
                nameof(Settings.IndexingMaxMemoryUtilization),
                "Indexing max memory utilization",
                "Memory utilization threshold between 0 and 1 before indexing slows down.",
                "decimal",
                new[] { "INDEXING_MAX_MEMORY_UTILIZATION", "IndexingMaxMemoryUtilization" },
                settings => settings.IndexingMaxMemoryUtilization.ToString(CultureInfo.InvariantCulture),
                (settings, value) => settings.IndexingMaxMemoryUtilization = double.Parse(value, CultureInfo.InvariantCulture),
                ParseMemoryUtilization),
            new AppSettingDefinition(
                nameof(Settings.IndexingMemoryRecoveryDelaySeconds),
                "Indexing memory recovery delay",
                "Seconds to pause when memory utilization stays above the configured threshold.",
                "integer",
                new[] { "INDEXING_MEMORY_RECOVERY_DELAY_SECONDS", "IndexingMemoryRecoveryDelaySeconds" },
                settings => settings.IndexingMemoryRecoveryDelaySeconds.ToString(CultureInfo.InvariantCulture),
                (settings, value) => settings.IndexingMemoryRecoveryDelaySeconds = int.Parse(value, CultureInfo.InvariantCulture),
                value => ParseInt(value, min: 1))
        };

    private static ParseResult ParseRequiredString(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? ParseResult.Fail("Value is required.")
            : ParseResult.Ok(trimmed);
    }

    private static ParseResult ParseBool(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "1" or "true" or "yes" or "on" => ParseResult.Ok("true"),
            "0" or "false" or "no" or "off" => ParseResult.Ok("false"),
            _ => ParseResult.Fail("Use true or false.")
        };
    }

    private static ParseResult ParseSpeedUnit(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "kmh" or "km/h" or "kph" => ParseResult.Ok("kmh"),
            "mph" or "miles" => ParseResult.Ok("mph"),
            _ => ParseResult.Fail("Use kmh or mph.")
        };
    }

    private static ParseResult ParseInt(string? value, int min)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < min)
        {
            return ParseResult.Fail($"Use an integer greater than or equal to {min}.");
        }

        return ParseResult.Ok(result.ToString(CultureInfo.InvariantCulture));
    }

    private static ParseResult ParseMemoryUtilization(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || result <= 0 || result > 1)
        {
            return ParseResult.Fail("Use a decimal greater than 0 and less than or equal to 1.");
        }

        return ParseResult.Ok(result.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class AppSettingDefinition
    {
        public AppSettingDefinition(
            string key,
            string label,
            string description,
            string inputType,
            string[] envVarNames,
            Func<Settings, string?> getValue,
            Action<Settings, string> applyValue,
            Func<string?, ParseResult> parse,
            bool isRequired = false,
            bool causesClipRefresh = false,
            string[]? options = null)
        {
            Key = key;
            Label = label;
            Description = description;
            InputType = inputType;
            EnvVarNames = envVarNames;
            GetValue = settings => getValue(settings) ?? string.Empty;
            ApplyValue = applyValue;
            Parse = parse;
            IsRequired = isRequired;
            CausesClipRefresh = causesClipRefresh;
            Options = options ?? Array.Empty<string>();
        }

        public string Key { get; }
        public string Label { get; }
        public string Description { get; }
        public string InputType { get; }
        public string[] EnvVarNames { get; }
        public Func<Settings, string> GetValue { get; }
        public Action<Settings, string> ApplyValue { get; }
        public Func<string?, ParseResult> Parse { get; }
        public bool IsRequired { get; }
        public bool CausesClipRefresh { get; }
        public string[] Options { get; }
    }

    private readonly record struct ParseResult(bool Success, string? Value, string? Error)
    {
        public static ParseResult Ok(string value) => new(true, value, null);
        public static ParseResult Fail(string error) => new(false, null, error);
    }

    private sealed class PersistedSettings
    {
        public bool IsConfigured { get; set; }
        public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
