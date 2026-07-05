namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>One file to fetch a key for. <paramref name="Id"/> is echoed back in the result.</summary>
public readonly record struct FekRequest(string Id, EncryptedHeader Header);

/// <summary>A per-file key error returned by the decrypt endpoint (e.g. unable_to_verify_ownership).</summary>
public readonly record struct FekError(string Id, string Message);

public sealed record FekBatchResult(IReadOnlyDictionary<string, byte[]> Keys, IReadOnlyList<FekError> Errors);

public interface ITeslaKeyService
{
    /// <summary>
    /// Fetches the 16-byte FEK for each cloud-keyed file from dashcam.tesla.com/api/1/decrypt/batch.
    /// Propagates <see cref="TeslaNotConnectedException"/> / <see cref="TeslaRefreshFailedException"/>
    /// from auth; per-file failures are returned in <see cref="FekBatchResult.Errors"/>, not thrown.
    /// </summary>
    Task<FekBatchResult> FetchFeksAsync(IReadOnlyList<FekRequest> requests, CancellationToken cancellationToken = default);
}
