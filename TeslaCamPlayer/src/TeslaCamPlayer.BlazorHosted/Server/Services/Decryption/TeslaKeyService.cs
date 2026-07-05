using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>
/// Fetches file-encryption keys (FEKs) from Tesla's dashcam decrypt endpoint. The server unwraps
/// each file's account-wrapped key (ECDH + AES-GCM) and returns the raw 16-byte AES key; only the
/// wrapped-key header blobs cross the network, never the video body.
/// </summary>
public sealed class TeslaKeyService : ITeslaKeyService
{
    private const string BatchUrl = "https://dashcam.tesla.com/api/1/decrypt/batch";
    private const string Origin = "https://dashcam.tesla.com";
    private const int MaxBatchSize = 30; // dashcam decrypt/batch caps at 30 items per request

    private readonly ITeslaAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;

    public TeslaKeyService(ITeslaAuthService auth, IHttpClientFactory httpClientFactory)
    {
        _auth = auth;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<FekBatchResult> FetchFeksAsync(IReadOnlyList<FekRequest> requests, CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var errors = new List<FekError>();

        var cloudKeyed = requests.Where(r => r.Header.HasCloudKey).ToList();
        if (cloudKeyed.Count == 0)
            return new FekBatchResult(keys, errors);

        for (var offset = 0; offset < cloudKeyed.Count; offset += MaxBatchSize)
        {
            var chunk = cloudKeyed.Skip(offset).Take(MaxBatchSize).ToList();
            var byId = chunk.ToDictionary(r => r.Id, r => r);
            var payload = BuildPayload(chunk);

            var json = await PostBatchAsync(payload, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                continue;

            foreach (var res in results.EnumerateArray())
            {
                var id = res.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (id == null || !byId.ContainsKey(id))
                    continue;

                if (res.TryGetProperty("error", out var errEl) && errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    var message = errEl.ToString();
                    errors.Add(new FekError(id, message));
                    Log.Warning("Tesla decrypt/batch key error for {Id}: {Error}", id, message);
                    continue;
                }

                if (res.TryGetProperty("key", out var keyEl) && keyEl.GetString() is { Length: > 0 } b64)
                    keys[id] = Convert.FromBase64String(b64);
            }
        }

        return new FekBatchResult(keys, errors);
    }

    private static string BuildPayload(IReadOnlyList<FekRequest> chunk)
    {
        var items = chunk.Select(r => new
        {
            id = r.Id,
            vin = r.Header.Vin,
            key_id = r.Header.KeyId,
            timestamp = r.Header.Timestamp,
            wrapped_key = Convert.ToBase64String(r.Header.WrappedKey),
            public_key = Convert.ToBase64String(r.Header.PublicKey)
        });
        return JsonSerializer.Serialize(new { items });
    }

    private async Task<string> PostBatchAsync(string payload, CancellationToken cancellationToken)
    {
        var response = await SendAsync(payload, forceRefresh: false, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token rejected mid-flight: force one refresh and retry.
            response.Dispose();
            response = await SendAsync(payload, forceRefresh: true, cancellationToken);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();

        if (status == HttpStatusCode.Unauthorized)
            throw new TeslaRefreshFailedException("Tesla rejected the access token for decryption. Reconnect your account.");
        if (!((int)status >= 200 && (int)status < 300))
            throw new InvalidOperationException($"decrypt/batch returned {(int)status}: {Truncate(body)}");

        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(string payload, bool forceRefresh, CancellationToken cancellationToken)
    {
        var accessToken = await _auth.GetAccessTokenAsync(forceRefresh, cancellationToken);
        var client = _httpClientFactory.CreateClient("tesla");

        var request = new HttpRequestMessage(HttpMethod.Post, BatchUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", Origin + "/");

        return await client.SendAsync(request, cancellationToken);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
