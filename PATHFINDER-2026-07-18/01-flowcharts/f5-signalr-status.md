# F5 — Real-time Status (SignalR)

Base path: `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/`

## Hub + broadcast sites

`StatusHub` (`Server/Hubs/StatusHub.cs`): `OnConnectedAsync:19` (replays refresh status to caller `:28`), `OnDisconnectedAsync:33`, `SubscribeToExport:55` (group `export:{jobId}` `:63`, replay `:72`), `SubscribeToAllExports:76` (`export:all` `:78`), `UnsubscribeFromExport:84`, `UnsubscribeFromAllExports:100`. Mapped `Program.cs:95`.

Only two `IHubContext<StatusHub>` injections exist:
- `RefreshProgressService.cs:17/20` → `Clients.All.SendAsync("RefreshStatusUpdated", …)` at `:125`; `Start:25`, `Increment:44` (250 ms throttle `:13,:57`), `Complete:71`, `GetStatus:83`, clone `:91`, `BroadcastStatus:100`.
- `ExportService.cs:25/90` → private `BroadcastStatus:47` sends to `export:{jobId}` + `export:all` (`:82-83`), clone `CloneStatus:34`, state in `ConcurrentDictionary _status`. **No ExportProgressService exists** — export's broadcast concern is inlined in the 1000-line ExportService.

Client: single shared `HubConnection` in `StatusHubClient` (singleton, `Client/Program.cs:13`): `On<RefreshStatus>:216`, `On<ExportStatus>:226`, `RegisterRefreshHandler:39` / `RegisterExportHandler:56`, `EnsureConnectionAsync:194`, `WithAutomaticReconnect:213`, `Reconnected → ResubscribeAsync:259/281` with ref-counted subscription tables. Consumers: `Index.razor.cs:90-91,302`, `ExportProgressDialog.razor:58-123`, `ExportHistoryDialog.razor:74-259`.

## Flowchart

```mermaid
flowchart TD
    subgraph REFRESH["Refresh / indexing path"]
        CS_Start["ClipsService.Start<br/>ClipsService.cs:372,436"] --> RPS_Start["RefreshProgressService.Start<br/>RefreshProgressService.cs:25"]
        CS_Inc["ClipsService.Increment per file<br/>ClipsService.cs:668"] --> RPS_Inc["Increment (250ms throttle)<br/>RefreshProgressService.cs:44"]
        CS_Comp["ClipsService.Complete<br/>ClipsService.cs:332"] --> RPS_Comp["Complete<br/>RefreshProgressService.cs:71"]
        RPS_Start --> RPS_BC["BroadcastStatus<br/>RefreshProgressService.cs:100"]
        RPS_Inc --> RPS_BC
        RPS_Comp --> RPS_BC
        RPS_BC -->|"Clients.All RefreshStatusUpdated :125"| HUBALL["all connections"]
        HUBALL --> C_ROn["On RefreshStatus<br/>StatusHubClient.cs:216"]
        C_ROn --> C_RH["Index.HandleRefreshStatusAsync<br/>Index.razor.cs:302"]
        HUB_Conn["OnConnectedAsync replay<br/>StatusHub.cs:28"] --> C_ROn
    end
    subgraph EXPORT["Export job path"]
        EX_Run["RunExportAsync progress lines<br/>ExportService.cs:772-793"] --> EX_BC["BroadcastStatus (no throttle)<br/>ExportService.cs:47"]
        EX_BC --> EX_Dict["_status ConcurrentDictionary<br/>ExportService.cs:58"]
        EX_BC -->|"export:{jobId} + export:all :82-83"| HUBGRP["subscribed groups"]
        HUBGRP --> C_EOn["On ExportStatus<br/>StatusHubClient.cs:226"]
        C_EOn --> C_EH1["Index (started job)"]
        C_EOn --> C_EH2["ExportProgressDialog.razor:78"]
        C_EOn --> C_EH3["ExportHistoryDialog.razor:104"]
        C_Sub["SubscribeToExportAsync<br/>StatusHubClient.cs:73,146"] --> HUB_Sub["StatusHub.SubscribeToExport<br/>StatusHub.cs:55,76"]
        HUB_Sub -->|"AddToGroup + replay :63,72"| C_EOn
    end
    C_Recon["Reconnected → ResubscribeAsync<br/>StatusHubClient.cs:259,281"] --> C_Sub
```

## Parallel-implementation analysis (feeds Phase 2)

Refresh and export are two implementations of the same "cache latest status, replay on subscribe, clone, fire-and-forget broadcast" concern:

| Concern | RefreshProgressService | ExportService.BroadcastStatus |
|---|---|---|
| Extraction | Dedicated service + interface | Inlined private method, no interface |
| Throttling | 250 ms gate | **none** — every ffmpeg line broadcasts |
| Fan-out | `Clients.All` | groups `export:{jobId}` + `export:all` |
| Cardinality | single global status | dict keyed by jobId |
| Concurrency | `lock` + clone method | ConcurrentDictionary + static clone |
| Error pattern | `ContinueWith(OnlyOnFaulted)` `:126` | identical `:85` |

Client-side reconnect/resubscribe is **centralized and well-designed** (one HubConnection, ref-counted subscriptions) — not duplicated. Minor type-driven duplication: RegisterRefreshHandler/RegisterExportHandler + notify loops are parallel per DTO (acceptable; generic channel would be over-engineering).

## External dependencies

SignalR server+client packages, Serilog, ffmpeg `-progress pipe:1` as timing source (`ExportService.cs:752,772`), MudBlazor dialogs, NavigationManager (`StatusHubClient.cs:212`).

## Confidence

High; exhaustive IHubContext grep. Gaps: UI-render tails of handlers not line-verified.
