# F1 — Clip Indexing / Scanning

Base path: `TeslaCamPlayer/src/TeslaCamPlayer.BlazorHosted/`

## Happy path

Indexing is driven by `GetClips` (`Server/Controllers/ApiController.cs:42`), not `GetClipsPaged` (pure DB read). `ClipsService.GetClipsAsync` (`Server/Services/ClipsService.cs:98`) lazy-loads `_cache` from SQLite (`GetCachedAsync :63` → `LoadVideoFilesAsync` → `BuildClipCache :875`), returns fast if cached, otherwise queues detached background refresh via `StartBackgroundRefreshIfNeeded :276` (static `_refreshGate`/`_refreshTask`/`_pendingFullRefresh` serialize one worker). `RefreshCacheWorkerAsync :313` → `RefreshFullAsync :408` / `RefreshIncrementalAsync :336`; `finally` → progress `Complete()`.

Full: seed known files `:416-422` → `EnumerateCandidatePaths :672` (recursive `*.mp4` + regex) → staleness diff (`GetAllFilePathsAsync`) → progress `Start` → memory-adaptive batch loop → `ExecuteBatchAsync :513` (`PrepareBatchAsync :581` memory gate; `ProcessBatchInternalAsync :626` Task.WhenAll; `UpsertVideoFilesAsync`) → stale prune → `UpdateCacheAsync :854`.

Per file: `ProcessVideoFileAsync :640` → `TryParseVideoFileAsync :1035` → `ParseVideoFileAsync :1048` (semaphore = CPU count `:54-55`) → `EcryptfsDecryptor.IsEncryptedFile :1066` (encrypted → duration Zero `:1070`) → hybrid duration probe (`FfProbeService.cs:70` → `Mp4DurationReader.cs:32` native mvhd tail-read; fallback ffprobe spawn `FfProbeService.cs:12` → `ParseFfProbeOutputHelper.cs:10`).

Parsing: master `[GeneratedRegex]` `FileNameRegexGenerated :1265` (named groups type/event/date/camera); `ParseClipType :1106`; `ParseCamera :1115`; separate `FolderTimestampRegex :718` + `FileTimestampRegex :722` + `ParseTimestampFromMatch :796` for incremental scan.

Grouping: event clips `ParseClip :1208` + `BuildSegmentsByStartDate :243`; recent clips `GetRecentClips :983` (5s gap tolerance `:1020-1024`); metadata `ReadEventMeta :261` → `TryReadEvent :1229` (event.json via Newtonsoft); paged path rebuilds clips separately via `BuildClipsFromVideoFiles :179` → `BuildClipFromEventVideos :217`.

## Flowchart

```mermaid
flowchart TD
    A["GetClips<br/>ApiController.cs:42"] --> B["GetClipsAsync<br/>ClipsService.cs:98"]
    B --> C{"_cache null?<br/>ClipsService.cs:100"}
    C -- yes --> D["GetCachedAsync<br/>ClipsService.cs:63"]
    D --> E["LoadVideoFilesAsync (DB read)<br/>SqliteClipIndexRepository.cs:38"]
    E --> F["BuildClipCache<br/>ClipsService.cs:875"]
    F --> G{"cache && no refresh?<br/>ClipsService.cs:103"}
    C -- no --> G
    G -- yes --> RET["return Clip[]<br/>ClipsService.cs:104"]
    G -- no --> H["StartBackgroundRefreshIfNeeded<br/>ClipsService.cs:276"]
    H --> RET2["return _cache for first paint<br/>ClipsService.cs:115"]
    H -.spawns Task.Run.-> W["RefreshCacheWorkerAsync<br/>ClipsService.cs:313"]
    W --> MODE{"mode<br/>ClipsService.cs:317"}
    MODE -- Full --> RF["RefreshFullAsync<br/>ClipsService.cs:408"]
    MODE -- Incremental --> RI["RefreshIncrementalAsync<br/>ClipsService.cs:336"]
    W --> CMP["progress Complete()<br/>RefreshProgressService.cs:71"]
    RI --> RIMAX["GetMaxStartTicksAsync<br/>SqliteClipIndexRepository.cs:515"]
    RIMAX --> RISCAN["EnumerateCandidatePathsSince<br/>ClipsService.cs:726"]
    RISCAN --> RIPROC["ProcessBatchInternalAsync<br/>ClipsService.cs:626"]
    RIPROC --> RIUP["UpsertVideoFilesAsync<br/>SqliteClipIndexRepository.cs:88"]
    RIUP --> RIMERGE["merge into _cache<br/>ClipsService.cs:392"]
    RF --> SEED["seed knownVideoFiles<br/>ClipsService.cs:416"]
    SEED --> ENUM["EnumerateCandidatePaths<br/>ClipsService.cs:672"]
    ENUM --> ALLP["GetAllFilePathsAsync<br/>SqliteClipIndexRepository.cs:532"]
    ALLP --> STj["progress Start('full')<br/>RefreshProgressService.cs:25"]
    STj --> LOOP{"batch loop<br/>ClipsService.cs:444"}
    LOOP --> EB["ExecuteBatchAsync<br/>ClipsService.cs:513"]
    EB --> PB["PrepareBatchAsync (memory gate)<br/>ClipsService.cs:581"]
    PB --> MEMCHK{"utilization > threshold?<br/>ClipsService.cs:594"}
    MEMCHK -- yes --> GC["reduce batch / GC.Collect<br/>ClipsService.cs:807"]
    GC --> PB
    MEMCHK -- no --> PBI["ProcessBatchInternalAsync<br/>ClipsService.cs:626"]
    PBI --> PVF["ProcessVideoFileAsync<br/>ClipsService.cs:640"]
    PVF --> PARSE["ParseVideoFileAsync<br/>ClipsService.cs:1048"]
    PARSE --> ENC{"IsEncryptedFile?<br/>EcryptfsDecryptor.cs:55"}
    ENC -- yes --> ZERO["duration = Zero<br/>ClipsService.cs:1070"]
    ENC -- no --> DUR["hybrid duration probe<br/>FfProbeService.cs:70"]
    DUR --> NATIVE["Mp4DurationReader.TryReadDurationAsync<br/>Mp4DurationReader.cs:32"]
    NATIVE --> NMISS{"native hit?<br/>FfProbeService.cs:73"}
    NMISS -- no --> FFP["ffprobe spawn<br/>FfProbeService.cs:12"]
    FFP --> PFP["ParseFfProbeOutputHelper.GetDuration<br/>ParseFfProbeOutputHelper.cs:10"]
    NMISS -- yes --> VF["new VideoFile<br/>ClipsService.cs:1088"]
    ZERO --> VF
    PFP --> VF
    PVF --> INC["progress Increment() 250ms throttle<br/>RefreshProgressService.cs:44"]
    EB --> UP["UpsertVideoFilesAsync<br/>SqliteClipIndexRepository.cs:88"]
    UP --> LOOP
    LOOP -- done --> STALE["RemoveByFilePathsAsync<br/>SqliteClipIndexRepository.cs:552"]
    STALE --> UC["UpdateCacheAsync → BuildClipCache<br/>ClipsService.cs:854"]
    UC --> PRUNE["PruneMissingEventClips<br/>ClipsService.cs:914"]
    PRUNE --> DONE["_cache rebuilt (terminal)<br/>ClipsService.cs:859"]
    RIMERGE --> DONE
    F --> PC["ParseClip / GetRecentClips<br/>ClipsService.cs:1208"]
    PC --> SEG["BuildSegmentsByStartDate + ReadEventMeta<br/>ClipsService.cs:243"]
    INC -.SignalR.-> HUB["StatusHub RefreshStatusUpdated<br/>RefreshProgressService.cs:125"]
    CMP -.SignalR.-> HUB
```

## Internal duplication (feeds Phase 2)

1. **File-timestamp DateTime construction copy-pasted verbatim**: `ParseVideoFileAsync :1056-1062` ≡ `BuildDecryptedVideoFileAsync :1184-1190`; `ParseTimestampFromMatch :796` is a third variant.
2. **Six-camera `ClipVideoSegment` mapping ×2**: `BuildSegmentsByStartDate :250-255` and inline in `GetRecentClips :996-1001`.
3. **Event-clip assembly ×2**: `BuildClipFromEventVideos :217` (paged) ≈ `ParseClip :1208` (cache).
4. **Path normalization ×2 across layers**: `ClipsService.NormalizeDirectoryPath :972` ≡ `SqliteClipIndexRepository.NormalizeDirectory :698`.
5. **TeslaCam naming grammar ×3**: `FileNameRegexGenerated :1265`, `FileTimestampRegex :722`, `FolderTimestampRegex :718`.
6. **`knownVideoFiles` seeding ×2**: `RefreshIncrementalAsync :354-360` ≡ `RefreshFullAsync :416-422`.

## External dependencies

F2 (SQLite repo), F5 (RefreshProgressService → StatusHub), duration probe (Hybrid/Mp4DurationReader/ffprobe), F8 (`EcryptfsDecryptor.IsEncryptedFile`; `PrepareEncryptedEventAsync :1137` bridges to decryption), F7 (Settings: ClipsRootPath, CacheDatabasePath, Indexing* knobs).

DI note: `ClipsService` is **Transient** (`Program.cs:20-22`) but refresh state (`_cache`, `_refreshGate`, `_refreshTask`, `_pendingFullRefresh`) is **static** process-global; `_ffprobeSemaphore` is per-instance. Pre-existing concurrency smell — flag, don't fix inline.

## Confidence

High — all core files read in full. Gaps: StatusHub/Settings read by grep only; decryption internals out of scope (F8).
