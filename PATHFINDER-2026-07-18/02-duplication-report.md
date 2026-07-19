# Pathfinder Duplication Report — docker-teslacamplayer

Date: 2026-07-18. Base path: `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/`.
Evidence source: nine per-feature flowchart traces (`01-flowcharts/`), all claims grep/read-verified.

## A. Cross-feature duplications (accidental — consolidate)

### A1. Path-safety primitives — 4 variants, 2 `EnsureTrailingSeparator` copies
- `ApiController.TryGetRootFullPath:66` + `EnsureTrailingSeparator:86` + `IsUnderRootPath:91` (normalized, correct) — used by PrepareEvent/DeleteEvent/ServeFile/StartExport
- `ClipDecryptionService.IsCachePath:27` + second `EnsureTrailingSeparator:196` — same technique, second copy
- `ApiController.ExportFile:299` — `StartsWith(exportsRoot)` **without separator normalization** (traversal weakness: `/data/exports-evil/` passes); `ListExports:346` same shape
- `ApiController.DeleteExport:431` — filename-equality enumeration (safe but a fourth shape)
Why diverged: decryption feature added its own helper; export endpoints hand-rolled. Accidental.

### A2. Cleanup hosted-service scaffolding — 2 copies
- `ExportCleanupService.ExecuteAsync:31-46` ≡ `DecryptedCacheCleanupService.ExecuteAsync:21-36` (while/try-CleanupOnce-catch-log/try-Delay), plus identical dir-guard + enumerate + per-file try-delete-log shape (`ExportCleanupService.cs:52-72` vs `DecryptedCacheCleanupService.cs:42-80`).
- **Legitimate divergence to preserve**: eviction policy (age by `CreationTimeUtc` vs LRU-size by `LastAccessTimeUtc`).

### A3. `/Api/Video/{escaped}` URL rule — 4 sites, 2 files
- `SqliteClipIndexRepository.cs:63`, `:356`; `ClipsService.cs:1091`, `:1198`. A Shared-model concern smeared across the repo and service.

### A4. Export path/URL ownership split — controller ⇄ service
- `ExportRootPath` re-resolution ×5: `ExportService.cs:228`, `:1003`; `ApiController.cs:298`, `:346`, `:426`
- Download-URL shaping duplicated: `ExportService.BuildDownloadUrl:1004-1010` ≡ `ApiController.ListExports:345-353`
- Controller bypasses services: inline `Directory.EnumerateFiles` `:331`, `File.Delete` `:441`, direct ffprobe spawn `TryReadExportMetadata:229` despite `IFfProbeService`; two range-serving mechanisms (`ServeFile:226` vs hand-built `PhysicalFileResult:303`).

### A5. Progress-broadcast concern — 2 parallel implementations (mostly legitimate)
- `RefreshProgressService` (dedicated service, 250 ms throttle, Clients.All, single status) vs `ExportService.BroadcastStatus:47` (inlined, unthrottled, groups, dict). Identical `ContinueWith(OnlyOnFaulted)` idiom (`RefreshProgressService.cs:126`, `ExportService.cs:85`) and manual clone methods (`:91` vs `:34`).
- **Verdict: legitimate specialization in fan-out/cardinality; do NOT merge the services.** The consolidable part is export-internal: 7 inline `new ExportStatus{...}` + broadcast sites (`:103,:146,:150,:787,:821,:831,:842`) → one `SetState` wrapper. Throttle asymmetry flagged as pre-existing behavior (not changed).

### A6. SEI/MP4/HUD — cross-language duplication (partly legitimate, partly fixable)
- **NAL/SEI parser hand-ported line-by-line** C#⇄JS: `SeiParserService.cs:44-178` ⇄ `dashcam-mp4.js:40-196` (six matching steps incl. emulation-byte strip). *Legitimate specialization* (server C# for export burn-in, client JS for live overlay) — unifying languages would be an architecture change; keep both.
- **Protobuf schema ×4 independently maintained copies**: `Server/Models/SeiMetadata.proto`, checked-in generated `SeiMetadata.cs` (no `<Protobuf>` build item — hand-regenerated), `Client/wwwroot/js/dashcam/dashcam.proto`, inline `PROTO_TEXT` (`sei-parser-interop.js:7-44`). **Accidental** — one source + generation/copy step is achievable without behavior change.
- **MP4 box parsers ×4**: `Mp4TimingService.cs:36-216`, `dashcam-mp4.js:50-99`, `Mp4DurationReader.cs:20-300`, `SeiParserService.FindMdatBox:148`. C# side has 3 partially-overlapping walkers; `FindBox` (`Mp4TimingService.cs:170`) can serve `FindMdatBox`. Cross-language copy stays.
- **HUD formatters ×3**: `hud_renderer.py` ⇄ `sei-hud.js` ⇄ `SeiHudFilterBuilder.cs` (speed conv, gear map, autopilot map). `SeiHudFilterBuilder` is **dead** (no callers — grep-verified) → deleting it removes one full copy. **Live drift found**: throttle heuristic `<=1.5` (`hud_renderer.py:593`) vs `<=1.2` (`sei-hud.js:76`, `HudRendererService.cs:446`) — pre-existing bug, flagged for separate handling.

### A7. Dual JSON stacks (client)
- Newtonsoft: `HttpClientNewtonsoftJsonHelper.cs:11` (8 GET call sites) + manual `JsonConvert` (`Index.razor.cs:872`)
- System.Text.Json: `StartExport` (`Index.razor.cs:550/:552`), export history (`ExportHistory.razor:92`, `ExportHistoryDialog.razor:147`)
Same API family, two serializers — drift risk with Shared model attributes.

### A8. Path normalization across layers
- `ClipsService.NormalizeDirectoryPath:972` ≡ `SqliteClipIndexRepository.NormalizeDirectory:698`.
- `CacheDatabasePath` default derivation duplicated: `SettingsProvider.cs:411-421` vs repo fallback `SqliteClipIndexRepository.cs:22`.

## B. Within-feature duplications (consolidate in place)

### B1. SqliteClipIndexRepository
- Connection-open boilerplate ×11 (`:43-46,:80-83,:103-108,:207-212,:239-242,:322-325,:376-379,:428-431,:470-473,:519-522,:537-540,:563-574`)
- WHERE-builder ×4 (`:244-276,:381-413,:475-489,:433-445`) + IN-expansion ×2 (`:327,:577`)
- Row→VideoFile mapping ×2 (`:52-70` ≡ `:345-363`)
- `Convert.ToInt32(ExecuteScalarAsync())` ×2 (`:420,:511`); magic `864000000000` (`:450`)

### B2. ClipsService
- Timestamp DateTime construction ×2 verbatim (`:1056-1062` ≡ `:1184-1190`) + variant `ParseTimestampFromMatch:796`
- TeslaCam naming grammar ×3 regexes (`:1265`, `:722`, `:718`)
- Six-camera segment mapping ×2 (`:250-255`, `:996-1001`)
- Event-clip assembly ×2 (`ParseClip:1208` ≈ `BuildClipFromEventVideos:217`)
- knownVideoFiles seeding ×2 (`:354-360` ≡ `:416-422`)

### B3. ExportService
- ffmpeg arg-pair idiom ×~15 (`:251-257,:687-690,:717,:730,:742,:744,:966-974`)
- Status+broadcast boilerplate ×7 (see A5)

### B4. ApiController
- Error-string copy-paste: "Clips root path…" ×4 (`:118,:177,:217,:391`); "Invalid path" ×5; jobId check ×3; `StatusCode(500, ex.Message)` ×2 (`:198,:450` — leaks exception text, pre-existing)

### B5. Client
- Active clip held twice (`Index.razor.cs:48` + `ClipViewer.State.cs:15`), imperative sync
- Hand-rolled 6-field dirty check (`ClipViewer.razor.cs:63-81`) forced by mutable `CameraFilterValues`
- `Index.razor.cs` 1041 lines / ≥7 concerns vs ClipViewer's 8 concern-partials

### B6. Settings
- `Clone(Settings):564-582` — hand-maintained mirror of `Settings.cs`, silent-data-loss footgun on every read

## C. Dead code (grep-verified, delete)

| Item | Location |
|---|---|
| `ResetAsync` | `SqliteClipIndexRepository.cs:76` + `IClipIndexRepository.cs` (zero callers) |
| `ExportMetadata.cs` | whole file (constants referenced nowhere, casing mismatch with actual tags) |
| `EscapePath` | `ExportService.cs:887` |
| `QualityToQscale` | `ExportService.cs:989` |
| `srtPath` + cleanup branch | `ExportService.cs:139,:854` (never assigned) |
| `RenderHudFramesToPipeAsync` | `HudRendererService.cs:180` (no callers) |
| `SeiHudFilterBuilder` (whole class) | `SeiHudFilterBuilder.cs` (no callers; removes a full HUD-formatter copy) |
| `EventFilterValues.IsInFilter` | `Client/Models/EventFilterValues.cs:21-45` (never called) |
| Debug `Console.WriteLine` | `ClipViewer.Timeline.cs:32`, `ClipViewer.State.cs:112,:117` |

## D. Pre-existing bugs — FLAGGED, not fixed by the refactor

1. Throttle-heuristic drift 1.5 vs 1.2 (`hud_renderer.py:593` vs `sei-hud.js:76`/`HudRendererService.cs:446`) — live playback and export render the same pedal differently.
2. `DeleteEvent` never invalidates clip cache/index (`ApiController.cs:169-200`) — stale entries until next refresh.
3. ffmpeg failure leaves partial output on disk (`ExportService.cs` delete only on cancel `:822`) — `ListExports` surfaces corrupt export as Completed.
4. `StatusCode(500, ex.Message)` leaks raw exception text (`ApiController.cs:198,:450`).
5. `ExportFile:299` no-separator StartsWith traversal weakness (fixed as a natural consequence of A1 unification — called out explicitly).
6. `ClipsService` is Transient with static refresh state; `_ffprobeSemaphore` per-instance while refresh gate is global (`Program.cs:20-22`, `ClipsService.cs:54`).
7. Export progress broadcasts unthrottled (every ffmpeg progress line) vs refresh's 250 ms throttle.
8. Client SEI parsing re-downloads the full MP4 (`sei-parser-interop.js:158-170`) — every clip transferred twice.
9. protobufjs loaded from CDN (`index.html:28`) — breaks offline/self-contained deployments.

## E. Legitimate specialization (do NOT unify)

- C# vs JS SEI/NAL/MP4 parsers (different runtimes, different trust/perf contexts) — unify the *schema source*, not the parsers.
- Cleanup eviction policies (age vs LRU-size) — unify the loop scaffolding only.
- Refresh vs export broadcast fan-out semantics (Clients.All vs groups; single vs dict) — unify nothing across them; clean up export-internal boilerplate only.
- Client reconnect/resubscribe plumbing — already centralized and well-designed; parallel per-DTO handler registration is acceptable (2 types).
