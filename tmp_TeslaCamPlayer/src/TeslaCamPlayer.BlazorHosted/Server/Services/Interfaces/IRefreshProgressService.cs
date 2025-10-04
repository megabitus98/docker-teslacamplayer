using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IRefreshProgressService
{
    void Start(int total);
    void Increment();
    void Complete();
    RefreshStatus GetStatus();
}

