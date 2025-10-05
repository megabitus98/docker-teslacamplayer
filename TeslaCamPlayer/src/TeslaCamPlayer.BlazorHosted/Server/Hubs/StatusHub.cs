using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;

namespace TeslaCamPlayer.BlazorHosted.Server.Hubs;

public class StatusHub : Hub
{
    private readonly IRefreshProgressService _refreshProgressService;
    private readonly IExportService _exportService;

    public StatusHub(IRefreshProgressService refreshProgressService, IExportService exportService)
    {
        _refreshProgressService = refreshProgressService;
        _exportService = exportService;
    }

    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();
        Log.Information(
            "StatusHub connection opened. ConnectionId={ConnectionId}, RemoteIp={RemoteIp}, UserAgent={UserAgent}",
            Context.ConnectionId,
            context?.Connection.RemoteIpAddress,
            context?.Request.Headers["User-Agent"].FirstOrDefault());

        await Clients.Caller.SendAsync("RefreshStatusUpdated", _refreshProgressService.GetStatus());

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        var context = Context.GetHttpContext();
        if (exception != null)
        {
            Log.Warning(
                exception,
                "StatusHub connection closed with error. ConnectionId={ConnectionId}, RemoteIp={RemoteIp}",
                Context.ConnectionId,
                context?.Connection.RemoteIpAddress);
        }
        else
        {
            Log.Information(
                "StatusHub connection closed. ConnectionId={ConnectionId}, RemoteIp={RemoteIp}",
                Context.ConnectionId,
                context?.Connection.RemoteIpAddress);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeToExport(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            Log.Warning("StatusHub received empty export subscription request. ConnectionId={ConnectionId}", Context.ConnectionId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GetExportGroupName(jobId));
        Log.Information(
            "Connection subscribed to export updates. ConnectionId={ConnectionId}, JobId={JobId}",
            Context.ConnectionId,
            jobId);

        var status = _exportService.GetStatus(jobId);
        if (status != null)
        {
            await Clients.Caller.SendAsync("ExportStatusUpdated", status);
        }
    }

    public async Task SubscribeToAllExports()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AllExportsGroupName);
        Log.Information(
            "Connection subscribed to all export updates. ConnectionId={ConnectionId}",
            Context.ConnectionId);
    }

    public Task UnsubscribeFromExport(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            Log.Warning("StatusHub received empty export unsubscription request. ConnectionId={ConnectionId}", Context.ConnectionId);
            return Task.CompletedTask;
        }

        Log.Information(
            "Connection unsubscribed from export updates. ConnectionId={ConnectionId}, JobId={JobId}",
            Context.ConnectionId,
            jobId);

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GetExportGroupName(jobId));
    }

    public Task UnsubscribeFromAllExports()
    {
        Log.Information(
            "Connection unsubscribed from all export updates. ConnectionId={ConnectionId}",
            Context.ConnectionId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, AllExportsGroupName);
    }

    internal static string GetExportGroupName(string jobId) => $"export:{jobId}";
    internal const string AllExportsGroupName = "export:all";
}
