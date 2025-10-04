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
        if (!string.IsNullOrWhiteSpace(exportRetentionEnv) && int.TryParse(exportRetentionEnv, out var hrs) && hrs > 0)
        {
            settings.ExportRetentionHours = hrs;
        }

        return settings;
    }
}
