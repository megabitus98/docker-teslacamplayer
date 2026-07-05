namespace TeslaCamPlayer.BlazorHosted.Server.Services.Decryption;

/// <summary>No Tesla refresh token is configured — the user must connect their account.</summary>
public sealed class TeslaNotConnectedException : Exception
{
    public TeslaNotConnectedException(string message) : base(message) { }
}

/// <summary>A refresh token is present but Tesla rejected it — the user must reconnect.</summary>
public sealed class TeslaRefreshFailedException : Exception
{
    public TeslaRefreshFailedException(string message) : base(message) { }
}
