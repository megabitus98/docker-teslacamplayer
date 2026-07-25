# Release notes

Each section header `## vX.Y.Z` matches a git tag. The release pipeline
extracts the matching section verbatim and uses it as the release body.
If no section matches the tag, the pipeline falls back to a short
default template plus the auto-generated commit list.

---

## v0.6.0

Hardware-accelerated exports plus a wave of quality-of-life features
inspired by the Sentry Studio feature set.

### Highlights

- **Hardware-accelerated export** — exports now use GPU encoding when a
  working encoder is available (Apple VideoToolbox, NVIDIA NVENC, Intel
  QuickSync, VAAPI — probed once at startup with a real test encode, not
  just listed capabilities). Decode uses `-hwaccel auto` the same way. If
  a hardware encode fails mid-export, the export automatically retries
  once on CPU. `EXPORT_HWACCEL=off` restores the exact previous
  software-only pipeline. VAAPI/QuickSync need `--device /dev/dri`;
  NVENC needs `--gpus all` — see the README.
- **Time & date format settings** — `TIME_FORMAT` (12h/24h) and
  `DATE_FORMAT` (four layouts) drive every clock in the UI *and* the
  timestamp burned into exports, so the video always matches the screen.
- **Event-trigger camera highlight** — the tile of the camera that fired
  the event gets an accent outline and a "Triggered" chip, so you look at
  the right footage first.
- **Faster review** — playback speeds now go up to 4x.
- **Glass chrome** — the app bar, drawer and control bar get a
  frosted-glass blur (`UI_BLUR_PX`, 0 disables it entirely).
- **Update notification** — the app checks GitHub for a newer release at
  most once per day and shows a dismissible banner plus a badge on the
  version number. Dismissing one version stays dismissed until the next.
  `UPDATE_CHECK=false` disables the outbound call completely.

### Compatibility

No migration required. All new settings default to the previous behavior
or a gentle extension of it; `EXPORT_HWACCEL=off` reproduces the previous
export pipeline byte for byte. Air-gapped setups: the update check fails
silently and never surfaces an error.

## v0.5.0

Export several moments from one event into a single video, plus a defect
sweep across the export pipeline, the timeline and the build.

### Highlights

- **Multi-interval export** — pick more than one range on the timeline and
  get **one** file. Drag the markers to a range, press **Add interval** to
  commit it, repeat. Committed ranges show as green bands on the timeline
  and as deletable chips, and the duration readout is the sum of what will
  actually be exported, not the span it covers. Intervals are joined with a
  hard cut, sorted and merged if they overlap.
- Each interval keeps **its own burned-in wall clock**, so the timestamp
  jumps to the correct time at every join instead of counting straight
  through the gaps. Telemetry HUD and GPS follow the same way.
- A camera missing from one of the selected ranges no longer freezes its
  tile on the last frame for the rest of the export — the gap is filled
  with black of exactly the right length so all tiles stay in sync.

### Fixes

- Exports could silently lose up to a second off the end. The HUD overlay
  determines output length, and with non-whole-frame ranges it came out
  fractionally shorter than the video, so ffmpeg trimmed the video and
  still reported success.
- Telemetry HUD could show the wrong speed and GPS for the remainder of an
  export: if a segment failed to parse mid-selection, every later reading
  was placed early by that segment's duration. The output clock now cannot
  be skipped.
- Timeline markers no longer stick to the cursor. Releasing the mouse
  outside the timeline left the marker dragging, so the next hover — with
  no button held — moved it.
- The timeline slider now seeks when driven by the keyboard. Arrow/Home/
  End moved the thumb without moving the video, which then snapped back.
- Parsed telemetry is capped at 10 cached segments in the browser. Scrubbing
  a long Sentry event previously pinned every segment it touched (~1 MB
  each) for the lifetime of the page.
- Local and non-Windows builds no longer compile with the Windows platform
  define. On macOS this selected a Windows `ffprobe.exe` that cannot run, so
  a clip the native MP4 parser could not read was dropped from the index
  instead of falling back. `linux-arm` / `linux-musl-arm` publishes were
  affected the same way.

### Internal

- Interval and frame-grid arithmetic extracted to `ExportInterval` /
  `ExportTiming` and covered by the repo's first unit tests (27).
- Dead code removed (`ExtractSeiMessagesForTimeRange`, an unread timeline
  field) and the ffprobe registration collapsed to a single Windows /
  non-Windows branch.
- The protobuf C# for the telemetry schema is now generated from
  `SeiMetadata.proto` at build time instead of being checked in, so editing
  the schema can no longer leave the generated code silently stale. The
  generated output is byte-identical to the file it replaced.
- The camera filter is compared by value rather than field by field, and
  the audit artifacts from the architecture sweep were removed now that
  every item in them has landed.
- Container images are built on every pull request. Previously the images
  were only ever built during a release, so a build break could not surface
  until after the tag existed — which is exactly how an arm64-only failure
  slipped through: it left macOS, the unit tests and the amd64 image all
  green.

### Compatibility

No migration required. The export API keeps the existing
`startTimeUtc`/`endTimeUtc` fields — a request without the new `intervals`
list behaves exactly as before — so older clients keep working.

## v0.4.0

Server-side SEI telemetry and a wave of long-standing bug fixes from the
architecture audit.

### Highlights

- **Server-side SEI telemetry** — new `/Api/SeiData` endpoint parses the
  per-frame HUD telemetry on the server. The browser HUD now fetches a
  small JSON payload instead of re-downloading the entire MP4 for every
  clip it plays, and the protobufjs CDN dependency is gone — the player
  now works fully offline / self-contained.

### Fixes

- Failed or in-progress exports no longer surface as completed downloads:
  ffmpeg writes to a temp name that is renamed only on success, and
  partial files are cleaned up on failure.
- Deleting an event now removes it from the clip index immediately
  instead of leaving a stale entry until the next full refresh.
- Export progress broadcasts are throttled (250 ms) like refresh
  progress, instead of firing on every ffmpeg progress line.
- API error responses no longer leak raw exception text.
- `ClipsService` is now a proper singleton — the ffprobe concurrency
  gate is app-wide as intended.
- HUD throttle-pedal heuristic unified across renderers (live playback
  and export previously disagreed), and the export HUD no longer
  re-inflates genuinely low pedal values to 100 %.

### Compatibility

No DB migration required; API and client remain backward compatible.
Clips without embedded SEI telemetry (pre-2026 firmware) behave as
before — the HUD simply stays hidden.

## v0.3.0

Adds native decryption of **encrypted TeslaCam clips** (firmware 2026.20+)
and a WebUI settings dialog for managing configuration without editing
environment variables.

### Highlights

- **Encrypted clip decryption** — clips Tesla encrypts on newer firmware
  (`EncryptedClips/`) are indexed with a lock badge and decrypted on
  demand when opened, using your Tesla account. Decrypted clips are
  cached under `/config/decrypted` (LRU-evicted, 10 GB cap by default).
- **Tesla account connection** — connect once with a refresh token
  (set-and-forget, auto-rotates) or paste a short-lived access token for
  a quick try. Configure it in **Settings → Tesla account** or via
  `TESLA_REFRESH_TOKEN` / `TESLA_ACCESS_TOKEN`.
- **WebUI settings dialog** — manage app configuration in the browser
  with persistent storage; environment variables still take precedence.
  Speed-unit changes now update the SEI HUD live without a full reinit.
- **Export supports encrypted clips** — exporting an encrypted event now
  decrypts it first instead of producing a black video.

### Compatibility

No DB migration required; the existing `clips.db` is reused. Decryption
is opt-in — without a Tesla token the app behaves exactly as before, and
encrypted clips simply show as locked.

### Artifacts

- **Docker (multi-arch amd64 / arm64)**:
  - `docker.io/megabitus/teslacamplayer:0.3.0`
  - `ghcr.io/megabitus98/teslacamplayer:0.3.0`
- **Windows x64**: `TeslaCamPlayer-0.3.0-Windows-x64.zip`
- **Linux x64**: `TeslaCamPlayer-0.3.0-Linux-x64.tar.gz`
- **Linux arm64**: `TeslaCamPlayer-0.3.0-Linux-arm64.tar.gz`
- **macOS x64**: `TeslaCamPlayer-0.3.0-macOS-x64.tar.gz`
- **macOS arm64**: `TeslaCamPlayer-0.3.0-macOS-arm64.tar.gz`

Self-contained archives bundle the .NET runtime; you still need
`ffmpeg` / `ffprobe` on `PATH` (and `python3` + `Pillow` if you use
the HUD renderer).

---

## v0.2.0

First release after the indexing-performance overhaul. On large
libraries the cold-cache "scan for new media" pass is **roughly 32%
faster** (now disk-bound on spinning arrays) and warm-cache refreshes
are effectively instant.

### Highlights

- **Native MP4 parser** replaces the per-file `ffprobe` process spawn —
  reads the `mvhd` atom directly with a single tail-first disk read.
  `ffprobe` is kept as a fallback for files the parser can't read.
- **Refresh is no longer O(N²)** — the per-batch full cache rebuild
  and the per-clip `Directory.Exists` check are gone. Incremental
  refreshes merge results in-place instead of reloading the entire DB.
- **No more double full-refresh on cold start** — the incremental pass
  is skipped when the DB is empty.
- **SQLite tuned for write throughput** during indexing
  (`synchronous=NORMAL`, `temp_store=MEMORY`, 20 MiB page cache).
- **SignalR refresh-status broadcasts throttled** to ~4 Hz instead of
  firing per file (previously thousands of frames per scan).
- **Regex updated** to accept event folders with truncated timestamps
  (e.g. `RecentClips/2025-12-22/...`) — picks up the upstream fix from
  TylerB260 while preserving pillar camera support.
- **New release pipeline** publishes a multi-arch Docker image
  (`amd64` + `arm64`) to Docker Hub and GHCR, plus self-contained
  binaries for Windows, Linux, and macOS.

### Compatibility

No DB migration or configuration change required. The existing
`clips.db` is reused as-is; the first refresh on the new version uses
the cached entries.

### Artifacts

- **Docker (multi-arch amd64 / arm64)**:
  - `docker.io/megabitus/teslacamplayer:0.2.0`
  - `ghcr.io/megabitus98/teslacamplayer:0.2.0`
- **Windows x64**: `TeslaCamPlayer-0.2.0-Windows-x64.zip`
- **Linux x64**: `TeslaCamPlayer-0.2.0-Linux-x64.tar.gz`
- **Linux arm64**: `TeslaCamPlayer-0.2.0-Linux-arm64.tar.gz`
- **macOS x64**: `TeslaCamPlayer-0.2.0-macOS-x64.tar.gz`
- **macOS arm64**: `TeslaCamPlayer-0.2.0-macOS-arm64.tar.gz`

Self-contained archives bundle the .NET runtime; you still need
`ffmpeg` / `ffprobe` on `PATH` (and `python3` + `Pillow` if you use
the HUD renderer).
