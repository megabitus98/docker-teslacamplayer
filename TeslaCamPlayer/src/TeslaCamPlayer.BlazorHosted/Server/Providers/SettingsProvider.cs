using System.Globalization;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Providers;

public class SettingsProvider : ISettingsProvider
{
    public Settings Settings => _settings.Value;

    private readonly Lazy<Settings> _settings = new(SettingsValueFactory);

    private static Settings SettingsValueFactory()
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables();

        var configuration = builder.Build();
        var settings = configuration.Get<Settings>() ?? new Settings();

        // Prefer explicit ENABLE_DELETE env var if present; fallback to config/binder default
        var enableDeleteEnv = Environment.GetEnvironmentVariable("ENABLE_DELETE");
        if (!string.IsNullOrWhiteSpace(enableDeleteEnv))
        {
            var normalized = enableDeleteEnv.Trim().ToLowerInvariant();
            settings.EnableDelete = normalized switch
            {
                "1" => true,
                "0" => false,
                "true" => true,
                "false" => false,
                "yes" => true,
                "no" => false,
                "on" => true,
                "off" => false,
                _ => settings.EnableDelete
            };
        }

        // Read SPEED_UNIT environment variable
        var speedUnitEnv = Environment.GetEnvironmentVariable("SPEED_UNIT");
        if (!string.IsNullOrWhiteSpace(speedUnitEnv))
        {
            var normalized = speedUnitEnv.Trim().ToLowerInvariant();
            settings.SpeedUnit = normalized switch
            {
                "kmh" => "kmh",
                "km/h" => "kmh",
                "kph" => "kmh",
                "mph" => "mph",
                "miles" => "mph",
                _ => settings.SpeedUnit
            };
        }

        // Ensure valid value (defensive programming)
        if (settings.SpeedUnit != "kmh" && settings.SpeedUnit != "mph")
        {
            settings.SpeedUnit = "kmh";
        }

        // Determine cache database path (fallback to prior CacheFilePath for backward compatibility)
        if (string.IsNullOrWhiteSpace(settings.CacheDatabasePath))
        {
            if (!string.IsNullOrWhiteSpace(settings.CacheFilePath))
            {
                var fileName = settings.CacheFilePath;
                var candidate = Path.ChangeExtension(fileName, ".db");
                settings.CacheDatabasePath = candidate;
            }
            else
            {
                settings.CacheDatabasePath = Path.Combine(AppContext.BaseDirectory, "clips.db");
            }
        }

        var cacheDbEnv = Environment.GetEnvironmentVariable("CACHE_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(cacheDbEnv))
        {
            settings.CacheDatabasePath = cacheDbEnv;
        }

        // ExportRootPath default: under wwwroot/exports
        if (string.IsNullOrWhiteSpace(settings.ExportRootPath))
        {
            var defaultExports = Path.Combine(AppContext.BaseDirectory, "wwwroot", "exports");
            settings.ExportRootPath = defaultExports;
        }

        var exportRootEnv = Environment.GetEnvironmentVariable("EXPORT_ROOT_PATH");
        if (!string.IsNullOrWhiteSpace(exportRootEnv))
        {
            settings.ExportRootPath = exportRootEnv;
        }

        var exportRetentionEnv = Environment.GetEnvironmentVariable("EXPORT_RETENTION_HOURS");
        if (!string.IsNullOrWhiteSpace(exportRetentionEnv) && int.TryParse(exportRetentionEnv, out var hrs))
        {
            settings.ExportRetentionHours = Math.Max(0, hrs);
        }

        // Indexing batch size
        if (settings.IndexingBatchSize <= 0)
        {
            settings.IndexingBatchSize = 1000;
        }

        var indexingBatchEnv = Environment.GetEnvironmentVariable("INDEXING_BATCH_SIZE");
        if (!string.IsNullOrWhiteSpace(indexingBatchEnv) && int.TryParse(indexingBatchEnv, out var batchSize) && batchSize > 0)
        {
            settings.IndexingBatchSize = batchSize;
        }

        // Indexing minimum batch size should not exceed main batch size
        if (settings.IndexingMinBatchSize <= 0)
        {
            settings.IndexingMinBatchSize = Math.Min(250, settings.IndexingBatchSize);
        }

        var indexingMinBatchEnv = Environment.GetEnvironmentVariable("INDEXING_MIN_BATCH_SIZE");
        if (!string.IsNullOrWhiteSpace(indexingMinBatchEnv) && int.TryParse(indexingMinBatchEnv, out var minBatch) && minBatch > 0)
        {
            settings.IndexingMinBatchSize = Math.Min(minBatch, settings.IndexingBatchSize);
        }

        settings.IndexingMinBatchSize = Math.Min(settings.IndexingMinBatchSize, settings.IndexingBatchSize);

        // Max memory utilization is a fraction between 0 and 1
        if (settings.IndexingMaxMemoryUtilization <= 0 || settings.IndexingMaxMemoryUtilization > 1)
        {
            settings.IndexingMaxMemoryUtilization = 0.85d;
        }

        var indexingMaxMemoryEnv = Environment.GetEnvironmentVariable("INDEXING_MAX_MEMORY_UTILIZATION");
        if (!string.IsNullOrWhiteSpace(indexingMaxMemoryEnv)
            && double.TryParse(indexingMaxMemoryEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxMem)
            && maxMem > 0
            && maxMem <= 1)
        {
            settings.IndexingMaxMemoryUtilization = maxMem;
        }

        // Delay between memory recovery attempts defaults to 5 seconds
        if (settings.IndexingMemoryRecoveryDelaySeconds <= 0)
        {
            settings.IndexingMemoryRecoveryDelaySeconds = 5;
        }

        var indexingRecoveryDelayEnv = Environment.GetEnvironmentVariable("INDEXING_MEMORY_RECOVERY_DELAY_SECONDS");
        if (!string.IsNullOrWhiteSpace(indexingRecoveryDelayEnv)
            && int.TryParse(indexingRecoveryDelayEnv, out var recoveryDelay)
            && recoveryDelay > 0)
        {
            settings.IndexingMemoryRecoveryDelaySeconds = recoveryDelay;
        }

        return settings;
    }
}
