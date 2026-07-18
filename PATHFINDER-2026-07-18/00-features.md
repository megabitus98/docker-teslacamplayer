# Pathfinder Feature Inventory — docker-teslacamplayer

Date: 2026-07-18
Solution: `TeslaCamPlayer/src/TeslaCamPlayer.sln` (.NET 8, hosted Blazor WASM)
Projects: Server (`TeslaCamPlayer.BlazorHosted.Server`), Client (`TeslaCamPlayer.BlazorHosted.Client`), Shared (`TeslaCamPlayer.BlazorHosted.Shared`)

Paths below are relative to `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/` unless rooted.

## Build / test / lint (discovered)

- Build: `dotnet build TeslaCamPlayer/src/TeslaCamPlayer.sln`
- Tests: **none exist** (no test project in the solution)
- Lint: `.editorconfig` only (no C# analyzers); nullable = warnings
- Client assets: gulp + dart-sass, esbuild, protobufjs (`Client/package.json`, `Client/gulpfile.js`)

## Features

### F1. Clip indexing / scanning
Walk `ClipsRootPath` for TeslaCam MP4s, parse filename/folder timestamps + camera + duration (ffprobe), persist a searchable index. Progressive full + incremental background refresh, memory-adaptive batching.
- Entry: `Controllers/ApiController.cs:42` (`GetClips`), `:46` (`GetClipsPaged`); `Services/ClipsService.cs:98` (`GetClipsAsync`) → `:276` (`StartBackgroundRefreshIfNeeded`) → `:408` (`RefreshFullAsync`) / `:336` (`RefreshIncrementalAsync`)
- Core: `Services/ClipsService.cs`, `Services/FfProbeService.cs`, `Services/HybridDurationProbeService.cs`, `Services/Mp4DurationReader.cs`, `Helpers/ParseFfProbeOutputHelper.cs`
- Calls into: F2, F5, F8 (`EcryptfsDecryptor.IsEncryptedFile` at `Services/ClipsService.cs:1066`)

### F2. Clip index storage (SQLite)
Persistent `video_files` table; paged/filtered queries, distinct event folders, available dates, stale-entry pruning.
- Entry: `Services/SqliteClipIndexRepository.cs:38` (`LoadVideoFilesAsync`), `:88` (`UpsertVideoFilesAsync`)
- Core: `Services/SqliteClipIndexRepository.cs`, `Services/Interfaces/IClipIndexRepository.cs`
- Called by: F1, F3; DB path from F7

### F3. Event/Clip API (browse + serve + delete)
HTTP surface for the client: paged clips, available dates, date→index, config, video/thumbnail serving with traversal guards + range support, event deletion.
- Entry: `Controllers/ApiController.cs:16`; actions `GetClipsPaged:46`, `GetAvailableDates:54`, `GetClipIndexByDate:58`, `GetConfig:99`, `Video:202`, `Thumbnail:206`, `DeleteEvent:169`, `ServeFile:210`, guard `IsUnderRootPath:91`
- Core: `Controllers/ApiController.cs`, `Shared/Models/{Clip,VideoFile,ClipPagedResponse,Event,Cameras,ClipType}.cs`
- Calls into: F1/F2, F7, F8 (`IsCachePath`)

### F4. Client player / viewer UI
Blazor WASM UI: virtualized event list, calendar, multi-camera synced playback, filtering, scrubbing, fullscreen, SEI HUD overlay, encrypted-clip unlock prompts.
- Entry: `Client/Program.cs:7`; `Client/Pages/Index.razor.cs:20` (`OnInitializedAsync:79`, `SetActiveClip:827`); `Client/Components/ClipViewer/ClipViewer.razor(.cs)` + partials (`.Playback/.Timeline/.Markers/.State/.TileLayout/.Fullscreen/.SeiParsing.cs`)
- Core: `Client/Pages/Index.razor.cs`, `Client/Components/ClipViewer/*`, `Client/Components/{VideoPlayer,CameraFilter,EventFilter,SeiHud}.razor`, `Client/Models/*`, `Client/wwwroot/js/dashcam/*.js`, `Client/Helpers/HttpClientNewtonsoftJsonHelper.cs`
- Calls into: F3, F5, F6, F7, F8 (`Api/PrepareEvent`, `Api/TeslaStatus`)

### F5. Real-time status (SignalR)
Push indexing-refresh and export-job progress; per-job and all-exports subscription groups.
- Entry: `Hubs/StatusHub.cs:8` (`OnConnectedAsync:19`, `SubscribeToExport:55`, `SubscribeToAllExports:76`), mapped `Program.cs:95`; `Services/RefreshProgressService.cs:25/44/71`; `Client/Services/StatusHubClient.cs:13`
- Core: `Hubs/StatusHub.cs`, `Services/RefreshProgressService.cs`, `Client/Services/StatusHubClient.cs`, `Shared/Models/{RefreshStatus,ExportStatus}.cs`
- Called by: F1, F6; consumed by F4

### F6. Export / clip rendering
Server-side ffmpeg pipeline: trim, xstack camera grid, optional labels/timestamp/location/SEI HUD burn-in, encode, SignalR progress, download, cancel/delete, retention cleanup, export history.
- Entry: `Controllers/ApiController.cs` `StartExport:376`, `ExportStatus:401`, `CancelExport:410`, `DeleteExport:418`, `ListExports:323`, `ExportFile:292`; `Services/ExportService.cs:100` (`StartExportAsync`) → `:137` (`RunExportAsync`); `Services/ExportCleanupService.cs:31` (hosted)
- Core: `Services/ExportService.cs`, `Services/ExportCleanupService.cs`, `Services/ExportMetadata.cs`, `Shared/Models/{ExportRequest,ExportStatus,PrepareEventResponse}.cs`, `Client/Pages/Export*.razor`
- Calls into: F5, F9, F8 (decrypt-before-export `Services/ExportService.cs:171`), F1, F7

### F7. Settings / configuration management
Layered settings: appsettings.json < env vars < WebUI overrides persisted to `/config/teslacamplayer.settings.json`. Validation, secret masking, first-run gating, refresh invalidation on path change.
- Entry: `Controllers/ApiController.cs` `GetAppSettings:153`, `SaveAppSettings:157`; `Providers/SettingsProvider.cs:11` (`SaveAppSettings:51`, `SetPersistedValue:155`, `CreateDefinitions:614`)
- Core: `Providers/SettingsProvider.cs`, `Server/Models/Settings.cs`, `Shared/Models/{AppSettings,AppConfig}.cs`, `Client/Pages/SettingsDialog.razor`
- Called by: nearly everything (singleton)

### F8. Encrypted-clip decryption + Tesla auth
Detect eCryptfs-wrapped clips, fetch FEKs from Tesla `decrypt/batch` (OAuth), decrypt to mirrored plaintext cache, manage OAuth credential (refresh-token rotation / seeded access token), LRU cache eviction.
- Entry: `Controllers/ApiController.cs` `PrepareEvent:110`, `TeslaStatus:106`; `Services/Decryption/ClipDecryptionService.cs:42` (`EnsureEventDecryptedAsync`); `Services/ClipsService.cs:1137` (`PrepareEncryptedEventAsync`); `Services/Decryption/TeslaAuthService.cs:63` (`GetAccessTokenAsync`); `Services/Decryption/TeslaKeyService.cs:29` (`FetchFeksAsync`); `Services/Decryption/EcryptfsDecryptor.cs:36`; `Services/Decryption/DecryptedCacheCleanupService.cs:21` (hosted)
- Core: `Services/Decryption/*`, `Shared/Models/TeslaConnectionStatus.cs`
- Calls into: F7 (tokens, `SetPersistedValue`), Tesla HTTP (`Program.cs:29`); consumed by F3/F4/F6

### F9. SEI telemetry / HUD rendering
Extract Tesla per-frame SEI metadata (GPS, speed, gear…) from MP4 + frame timing; render HUD PNG overlay via bundled Python `hud_renderer.py` for export burn-in. Duplicated client-side in JS (protobufjs) for live playback overlay.
- Entry: `Services/SeiParserService.cs:13` (`ExtractSeiMessages`), `Services/Mp4TimingService.cs:17` (`GetFrameTimelineAsync`), `Services/HudRendererService.cs:32` (`RenderHudFramesToDirectoryAsync`); driven from `Services/ExportService.cs:409-710`
- Core: `Services/SeiParserService.cs`, `Services/Mp4TimingService.cs`, `Services/HudRendererService.cs`, `Services/SeiHudFilterBuilder.cs`, `Server/lib/hud_renderer.py`, `Client/wwwroot/js/dashcam/{sei-parser-interop,sei-hud,sei-hud-interop,dashcam-mp4}.js`, `Client/Components/SeiHud.razor`
- Called by: F6; client side by F4

### F10. Docker / deployment glue
Multi-arch container packaging, s6-overlay supervision, portable builds, release CI. (Packaging only — excluded from refactor scope; no flowchart fan-out value beyond inventory.)
- Entry: `/Dockerfile`, `/root/etc/s6-overlay/s6-rc.d/svc-teslacamplayer/run`
- Core: `Dockerfile*`, `docker-compose.yaml`, `build-*.sh/ps1`, `root/etc/s6-overlay/**`, `.github/workflows/*`

## Known duplication candidates (pre-Phase-2 flags)

- SEI parsing duplicated across languages: C# `SeiParserService` (Google.Protobuf) vs JS `sei-parser-interop.js` (protobufjs), schema defined inline in both.
