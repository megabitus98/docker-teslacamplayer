using System.Threading;
using System.Threading.Tasks;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

public interface IUpdateCheckService
{
    /// <summary>Cached update info (refreshed at most once per 24h). Never throws; failures report no update.</summary>
    Task<UpdateCheckResult> GetAsync(CancellationToken cancellationToken);
}
