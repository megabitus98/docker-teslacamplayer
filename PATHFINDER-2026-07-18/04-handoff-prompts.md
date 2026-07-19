# Pathfinder Handoff Prompts — /make-plan per unified system

Copy any block below into `/make-plan`. Base path for all file refs: `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/`. Evidence: `PATHFINDER-2026-07-18/01-flowcharts/` + `02-duplication-report.md`. Global constraints for every plan: behavioral parity (no feature/API changes), no new abstractions beyond what removes duplication, build must stay green (`dotnet build TeslaCamPlayer/src/TeslaCamPlayer.sln`).

## T1 — Dead-code deletion sweep

```
Plan a dead-code deletion sweep in docker-teslacamplayer (evidence: PATHFINDER-2026-07-18/02-duplication-report.md §C).
Delete, after re-verifying zero callers by grep:
- ResetAsync: Server/Services/SqliteClipIndexRepository.cs:76 + IClipIndexRepository.cs declaration
- Server/Services/ExportMetadata.cs (whole file)
- ExportService.cs: EscapePath (:887), QualityToQscale (:989), srtPath local + its dead cleanup branch (:139, :854)
- Server/Services/HudRendererService.cs: RenderHudFramesToPipeAsync (:180) + any now-unused private helpers it alone used
- Server/Services/SeiHudFilterBuilder.cs (whole class; remove any DI registration if present)
- Client/Models/EventFilterValues.cs: IsInFilter (:21-45) only — keep the toggle properties (bound by UI)
- Debug Console.WriteLine: Client/Components/ClipViewer/ClipViewer.Timeline.cs:32, ClipViewer.State.cs:112,:117
Guards: deletions only — no signature changes, no refactoring; if any item turns out to have a caller, skip it and report.
```

## T2 — PathSafety unification

```
Plan the path-guard unification (evidence: 01-flowcharts/f3-http-api.md, 02-duplication-report.md §A1).
Target: new static Server/Helpers/PathSafety.cs with EnsureTrailingSeparator(string) and IsUnder(string root, string fullPath), using the exact algorithm of ApiController.IsUnderRootPath (Server/Controllers/ApiController.cs:91) — GetFullPath + separator-normalized Equals-or-StartsWith, OrdinalIgnoreCase.
Rewrite call sites:
- ApiController.cs: delete EnsureTrailingSeparator (:86) + IsUnderRootPath (:91); TryGetRootFullPath (:66), ServeFile (:220), PrepareEvent (:120), DeleteEvent (:182), StartExport (:389) use PathSafety
- ApiController.ExportFile (:299) and ListExports (:346): replace no-separator StartsWith with PathSafety.IsUnder — NOTE this is a deliberate security tightening (sibling-dir bypass stops passing); keep everything else identical
- Server/Services/Decryption/ClipDecryptionService.cs: delete EnsureTrailingSeparator (:196); IsCachePath (:27) uses PathSafety.IsUnder
Guards: no new interfaces/DI — static class only; no route/response changes; do not touch DeleteExport's filename-match approach (:431, already safe).
```

## T3 — SQLite repository consolidation + VideoFile URL factory

```
Plan the SqliteClipIndexRepository internal consolidation (evidence: 01-flowcharts/f2-clip-index-storage.md, 02-duplication-report.md §B1, §A3, §A8).
In Server/Services/SqliteClipIndexRepository.cs:
- Private OpenCommandAsync() helper replacing the 11 connection-open blocks (:43-46,:80-83,:103-108,:207-212,:239-242,:322-325,:376-379,:428-431,:470-473,:519-522,:537-540,:563-574)
- Private AppendClipFilters(command, clipTypes, fromDate, toDate) replacing the 4 WHERE-builders (:244-276,:381-413,:475-489,:433-445)
- Private MapVideoFile(reader) + one shared SELECT list replacing :52-70 / :345-363
- TimeSpan.TicksPerDay for the 864000000000 literal (:450)
- Keep NormalizeDirectory as the single normalizer; delete the twin ClipsService.NormalizeDirectoryPath (Server/Services/ClipsService.cs:972) and route its callers to one shared internal helper
Add static VideoFile.BuildApiUrl(string filePath) to Shared/Models/VideoFile.cs ($"/Api/Video/{Uri.EscapeDataString(path)}"); rewrite the 4 sites: SqliteClipIndexRepository.cs:63,:356 and ClipsService.cs:1091,:1198. Property remains settable — wire format unchanged.
Guards: no SQL semantics changes (same SQL text modulo parameter names); no interface changes; transactions/PRAGMAs untouched.
```

## T4 — ClipsService parsing/grouping consolidation

```
Plan the ClipsService in-place consolidation (evidence: 01-flowcharts/f1-clip-indexing.md, 02-duplication-report.md §B2).
In Server/Services/ClipsService.cs:
- Private BuildStartDate(Match) replacing the verbatim v*-group DateTime blocks (:1056-1062, :1184-1190); fold ParseTimestampFromMatch (:796) in only if group names align — otherwise leave it
- Reuse BuildSegmentsByStartDate's per-date segment mapping (:250-255) from GetRecentClips (:996-1001)
- Make BuildClipFromEventVideos (:217) the single event-clip assembler; ParseClip (:1208) delegates to it (verify identical output incl. thumbnail/meta handling before merging)
- Private SeedKnownFiles() replacing :354-360 / :416-422
Guards: indexing output must be byte-identical (same VideoFile/Clip values); do not touch the static refresh-state design (pre-existing, flagged separately); the 3 regexes stay.
```

## T5 — Export ownership consolidation

```
Plan the export-dir ownership move (evidence: 01-flowcharts/f6-export-pipeline.md, f3-http-api.md, 02-duplication-report.md §A4, §B3).
Target: ExportService (Server/Services/ExportService.cs) becomes the single owner of the export directory.
- Move into IExportService/ExportService: export listing (ApiController.ListExports:323-374 dir enumeration + synthetic status), metadata read (TryReadExportMetadata:229 — reuse existing ffprobe service infra instead of raw Process), delete-by-jobId (DeleteExport:418-452), and keep BuildDownloadUrl (:997) as the only URL shaper (delete the controller copy :345-353)
- ApiController.ExportFile (:292-309) reuses ServeFile (:210) with the exports root — one range-serving mechanism
- Inside ExportService: AddArg(name, value) helper for the ~15 arg-pair repetitions (:251-257,:687-690,:717,:730,:742,:744,:966-974); SetState(jobId, state, percent?, url?, error?) wrapper for the 7 status+broadcast sites (:103,:146,:150,:787,:821,:831,:842)
Guards: response JSON shapes and routes unchanged; do NOT add throttling to export broadcasts (pre-existing behavior, flagged); do NOT merge with RefreshProgressService.
```

## T6 — Periodic-cleanup skeleton

```
Plan the cleanup-service consolidation (evidence: 02-duplication-report.md §A2).
Target: abstract Server/Services/PeriodicFileCleanupService : BackgroundService holding the while/try-CleanupOnce/catch-log/Task.Delay loop, the null/exists dir guard, and per-file try-delete-with-log; abstract Interval, GetTargetDirectory(), SelectFilesToDelete(files).
- ExportCleanupService.cs shrinks to the age policy (CreationTimeUtc > retentionHours)
- Decryption/DecryptedCacheCleanupService.cs shrinks to the LRU-size policy (LastAccessTimeUtc ordering until under capBytes)
Guards: identical intervals, log messages may keep their current texts; policies byte-equivalent; both stay registered as hosted services.
```

## T7 — Settings Clone de-footgun

```
Plan the Settings.Clone fix (evidence: 01-flowcharts/f7-settings.md, 02-duplication-report.md §B6).
Replace the hand-maintained 18-line Clone(Settings) mirror (Server/Providers/SettingsProvider.cs:564-582) with a MemberwiseClone-based copy (all Settings props are value types/strings — shallow is exact). Prefer a Settings.Clone() instance method on Server/Models/Settings.cs.
Guards: verify every current property is still copied (write a quick reflection-based assertion in a throwaway check or the review gate); no other SettingsProvider changes.
```

## T8 — Single-source protobuf schema

```
Plan the protobuf schema single-sourcing (evidence: 01-flowcharts/f9-sei-hud.md, 02-duplication-report.md §A6).
Target: Server/Models/SeiMetadata.proto is the one schema.
- Add a build-time copy (Client csproj MSBuild Content/CopyToOutput or gulp task) producing Client/wwwroot/js/dashcam/dashcam.proto from it
- Delete the inline PROTO_TEXT fallback (Client/wwwroot/js/dashcam/sei-parser-interop.js:7-44); the existing fetch('...dashcam.proto') path becomes the only loader — verify the fetch path is exercised today (dashcam-mp4.js:234) and error-surfaces sanely
- SeiMetadata.cs stays checked-in as-is (regeneration wiring = follow-up bundle item)
Guards: byte-compare the current 4 copies first — if any differ, STOP and flag (schema drift would make this a behavior change); Docker build must still produce the file (check Dockerfile client stage).
```

## T9 — One client JSON stack

```
Plan the client JSON unification on Newtonsoft (evidence: 01-flowcharts/f4-client-ui.md, 02-duplication-report.md §A7).
- Extend Client/Helpers/HttpClientNewtonsoftJsonHelper.cs with PostAsNewtonsoftJsonAsync<TReq,TResp> (and a ReadFromNewtonsoftJsonAsync if needed)
- Rewrite the 3 System.Text.Json sites: Index.razor.cs:550/:552 (StartExport), ExportHistory.razor:92, ExportHistoryDialog.razor:147 (GetFromJsonAsync → Newtonsoft helper)
- Also fold the manual JsonConvert at Index.razor.cs:872 (PrepareEvent) into the helper
Guards: wire format must stay accepted by the server (model binding is case-insensitive — confirm with a runtime smoke of StartExport + ListExports); do not touch server serialization.
```

## T10 — Index.razor.cs decomposition

```
Plan the mechanical decomposition of Client/Pages/Index.razor.cs (1041 lines, evidence: 01-flowcharts/f4-client-ui.md §6).
Split into concern partials mirroring ClipViewer's existing pattern: Index.razor.cs (core + selection/nav), Index.Paging.cs (virtualization, scroll math, date-picker sync :130,:760,:932,:992), Index.Refresh.cs (SignalR handlers + throttle timers :274-370), Index.Export.cs (:453-589 + dialogs), Index.Unlock.cs (encrypted unlock :844-930).
Guards: pure moves — zero body edits, zero rename; fields move with their sole-user concern or stay in core; build green after each move.
```

## Follow-up bundle (non-blocking, separate from the refactor)

- Pre-existing bugs: 02-duplication-report.md §D (throttle drift 1.2/1.5, DeleteEvent cache staleness, partial-export-on-failure, 500 ex.Message leak, Transient/static ClipsService state, unthrottled export broadcasts, client MP4 double-download, protobufjs CDN dependency)
- `SeiMetadata.cs` regeneration wiring (`<Protobuf>` build item or documented regen script)
- Record-ify `CameraFilterValues` to kill the 6-field dirty check
- `directory_path` derived column in SQLite (drop in favor of computed/`LIKE`)
```
