using Newtonsoft.Json.Linq;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class UpdateCheckService : IUpdateCheckService
{
    private const string ReleasesUrl = "https://api.github.com/repos/megabitus98/docker-teslacamplayer/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly string CurrentVersion =
        typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsProvider _settingsProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateCheckResult _cached;
    private DateTime _cachedAtUtc;

    public UpdateCheckService(IHttpClientFactory httpClientFactory, ISettingsProvider settingsProvider)
    {
        _httpClientFactory = httpClientFactory;
        _settingsProvider = settingsProvider;
    }

    public async Task<UpdateCheckResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!_settingsProvider.Settings.UpdateCheck)
            return new UpdateCheckResult { Current = CurrentVersion, UpdateAvailable = false };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached == null || DateTime.UtcNow - _cachedAtUtc >= CacheDuration)
            {
                _cached = await FetchAsync(cancellationToken);
                _cachedAtUtc = DateTime.UtcNow;
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCheckResult> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("github");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var json = await client.GetStringAsync(ReleasesUrl, cts.Token);
            var release = JObject.Parse(json);
            var tag = release.Value<string>("tag_name");

            return new UpdateCheckResult
            {
                Current = CurrentVersion,
                Latest = tag,
                Url = release.Value<string>("html_url"),
                UpdateAvailable = UpdateVersion.IsNewer(CurrentVersion, tag)
            };
        }
        catch (Exception ex)
        {
            // Offline / rate-limited / air-gapped: quietly report no update, retry after the cache window.
            Log.Debug(ex, "Update check failed");
            return new UpdateCheckResult { Current = CurrentVersion, UpdateAvailable = false };
        }
    }
}
