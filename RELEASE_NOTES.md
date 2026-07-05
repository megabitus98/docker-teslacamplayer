# Release notes

Each section header `## vX.Y.Z` matches a git tag. The release pipeline
extracts the matching section verbatim and uses it as the release body.
If no section matches the tag, the pipeline falls back to a short
default template plus the auto-generated commit list.

---

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
