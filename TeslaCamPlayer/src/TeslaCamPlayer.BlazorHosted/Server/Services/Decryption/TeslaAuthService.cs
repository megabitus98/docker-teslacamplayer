using System.Text.Json;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Models;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>
/// Holds the live Tesla dashcam credential and refreshes it. The user provides a refresh token
/// once (minted with scope=offline_access on client_id=dashcam); this exchanges it for 8h access
/// tokens as needed. Refresh tokens are single-use and rotate on every exchange, so each refresh
/// persists the new token back through <see cref="ISettingsProvider"/>.
/// </summary>
public sealed class TeslaAuthService : ITeslaAuthService
{
    private const string TokenEndpoint = "https://auth.tesla.com/oauth2/v3/token";
    private const string ClientId = "dashcam";
    private const string Scope = "openid profile email offline_access";
    private const string DashcamAudience = "https://dashcam.tesla.com/";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(120);

    private readonly ISettingsProvider _settingsProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _currentRefreshToken;
    private string _accessToken;
    private DateTimeOffset _expiresAt;
    private string _lastError;
    private bool _audienceOk;
    private bool _hasXEnc;

    public TeslaAuthService(ISettingsProvider settingsProvider, IHttpClientFactory httpClientFactory)
    {
        _settingsProvider = settingsProvider;
        _httpClientFactory = httpClientFactory;

        // Optional dev/quick-start path: seed a pre-obtained access token (e.g. copied from the
        // dashcam DevTools). Used until it expires; with no refresh token it simply expires.
        var seededAccess = Environment.GetEnvironmentVariable("TESLA_ACCESS_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(seededAccess))
        {
            _accessToken = seededAccess;
            _expiresAt = ReadExpiryFromJwt(seededAccess) ?? DateTimeOffset.UtcNow.AddHours(8);
            InspectClaims(seededAccess);
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settingsProvider.Settings.TeslaRefreshToken)
        || !string.IsNullOrEmpty(_accessToken);

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var configured = _settingsProvider.Settings.TeslaRefreshToken?.Trim();
        var haveSeededAccess = !string.IsNullOrEmpty(_accessToken);
        if (string.IsNullOrEmpty(configured) && !haveSeededAccess)
            throw new TeslaNotConnectedException("No Tesla refresh token is configured.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // The user pasted a different refresh token than the one we're holding — start over with it.
            if (!string.IsNullOrEmpty(configured) && !string.Equals(configured, _currentRefreshToken, StringComparison.Ordinal))
            {
                _currentRefreshToken = configured;
                _accessToken = null;
                _expiresAt = default;
                _lastError = null;
            }

            var stillValid = !string.IsNullOrEmpty(_accessToken)
                && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin;
            if (stillValid && !forceRefresh)
                return _accessToken;

            if (string.IsNullOrEmpty(_currentRefreshToken))
            {
                // No refresh token: rely on the seeded access token while it lasts.
                if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _expiresAt)
                    return _accessToken;
                throw new TeslaRefreshFailedException("Tesla access token expired and no refresh token is configured. Reconnect your account.");
            }

            await RefreshLockedAsync(cancellationToken);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset? ReadExpiryFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return null;

            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            // ignore — fall back to a default lifetime
        }

        return null;
    }

    public async Task<TeslaConnectionStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new TeslaConnectionStatus { HasToken = false, Connected = false };

        try
        {
            await GetAccessTokenAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        return new TeslaConnectionStatus
        {
            HasToken = true,
            Connected = !string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _expiresAt && _lastError == null,
            AudienceOk = _audienceOk,
            HasXEnc = _hasXEnc,
            LastError = _lastError
        };
    }

    private async Task RefreshLockedAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("tesla");
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = _currentRefreshToken,
                ["scope"] = Scope
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _lastError = $"Network error contacting Tesla: {ex.Message}";
            throw new TeslaRefreshFailedException(_lastError);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _accessToken = null;
            _lastError = $"Tesla rejected the refresh token ({(int)response.StatusCode}). Reconnect your account.";
            Log.Warning("Tesla token refresh failed with status {Status}.", (int)response.StatusCode);
            throw new TeslaRefreshFailedException(_lastError);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 28800;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        _lastError = null;

        if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } rotated
            && !string.Equals(rotated, _currentRefreshToken, StringComparison.Ordinal))
        {
            _currentRefreshToken = rotated;
            try
            {
                // Persist the rotated (single-use) token so a restart keeps working.
                _settingsProvider.SetPersistedValue(nameof(Settings.TeslaRefreshToken), rotated);
            }
            catch (Exception ex)
            {
                // Not fatal: the in-memory token still works and Tesla keeps the last one valid ~24h.
                Log.Warning(ex, "Failed to persist rotated Tesla refresh token; keeping it in memory only.");
            }
        }

        InspectClaims(_accessToken);
    }

    private void InspectClaims(string accessToken)
    {
        _audienceOk = false;
        _hasXEnc = false;
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
                return;

            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("aud", out var aud))
            {
                _audienceOk = aud.ValueKind == JsonValueKind.Array
                    ? aud.EnumerateArray().Any(a => a.GetString() == DashcamAudience)
                    : aud.GetString() == DashcamAudience;
            }

            _hasXEnc = root.TryGetProperty("x-enc", out _);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not inspect Tesla access token claims.");
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
