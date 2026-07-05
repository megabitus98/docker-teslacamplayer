# Encrypted TeslaCam clip decryption — design

**Date:** 2026-07-05
**Status:** Approved (design), pending implementation plan
**Branch:** `feat/encrypted-clip-decryption`

## Goal

Tesla firmware 2026.20+ encrypts TeslaCam clips (eCryptfs-derived format). This adds
first-class support to docker-teslacamplayer: encrypted clips are detected during
indexing, shown in the timeline with a lock badge, and decrypted **on demand** (when the
user opens or exports an event) using the user's own Tesla account. Everything runs
natively in the .NET server — no Python, no new NuGet dependencies.

## Approved decisions

1. **Engine:** native C# port of the crypto + auth (no Python runtime in the image).
2. **Connect Tesla:** user pastes a **refresh token** into Settings; server refreshes forever.
3. **When to decrypt:** lazy, per-event, on open/export (not eager at index).
4. **Encrypted UX:** show encrypted clips with a lock badge (not hidden).

## Reference implementation

Port from the sibling repo `~/GitHub/Tesla/tesla-dashcam-decrypt`:
`tesla_decrypt.py` (body decrypt), `tesla_dashcam.py` (header parse, FEK batch, token
exchange/refresh), `auth.py` (persistence), `AUTH.md` (auth research + confirmed experiment).

## Confirmed protocol facts (do not re-derive)

### File format (per encrypted file)
- Header is **8192 bytes**; body is **4096-byte pages** starting at offset **8192**.
- `plaintext_size` = big-endian **u64 at offset 0** (final output is truncated to this).
- **Magic detect:** `be32(head[8:12]) XOR be32(head[12:16]) == 0x3C81B7F5`. Single 16-byte read.
- **Wrapped-key section at offset 4096:** `u32 key_id`, `65-byte public_key` (uncompressed
  P-256, starts `0x04`), `17 ASCII VIN`, `u64 BE timestamp`, `44-byte wrapped_key`
  (`12 nonce | 16 ciphertext | 16 GCM tag`).
- **`HasCloudKey` = `public_key[0] == 0x04`.** Files with a zeroed public key (`event.json`,
  `thumb.png`) are wrapped for the in-car `_CONSOLE` recipient only and **cannot** be
  decrypted off-vehicle — the endpoint returns `error.unable_to_verify_ownership`. Skip them.

### Body decryption (local, once FEK is known)
- `rootIV = MD5(fek)`.
- For page `N`: `pageIV = MD5(rootIV ‖ ascii(decimal(N)) zero-padded to 32 bytes)`.
- Each 4096-byte page: **AES-128-CBC, PaddingMode.None**, key = FEK (16 bytes), IV = `pageIV`.
- Concatenate decrypted pages, truncate to `plaintext_size`. Body never leaves the machine.

### FEK retrieval (cloud)
- `POST https://dashcam.tesla.com/api/1/decrypt/batch`, `Authorization: Bearer <access>`.
- Body: `{"items":[{"id":<uuid>,"vin":<str>,"key_id":<int>,"timestamp":<unix_s>,`
  `"wrapped_key":<b64>,"public_key":<b64>}]}` — one item per file, batched per event.
- Response: `{"results":[{"id":<uuid>,"key":<b64 16 bytes>,"error":<null|str>}]}`. `key` is the FEK.
- The access token must carry the `x-enc` claim (present when minted for `client_id=dashcam`).

### Auth (refresh — the only thing the server does; login is one-time & external)
- Refresh: `POST https://auth.tesla.com/oauth2/v3/token`,
  form `grant_type=refresh_token&client_id=dashcam&refresh_token=<rt>` →
  `{access_token, expires_in:28800, refresh_token(**rotated, single-use**), id_token, token_type}`.
- The stored refresh token **must** have been minted with `scope=openid profile email
  offline_access` (the SPA omits `offline_access`, which is why the original HAR had none).
- Refresh tokens rotate on every exchange and expire after ~3 months of non-use, or on
  password reset. **Every refresh must persist the new refresh token or access is lost.**
- One-time login is done by the user with the external tool / a real browser (captcha +
  Akamai block automated browsers); the app only ever holds and refreshes the resulting token.
- **Implemented addition:** the auth service also accepts a pre-obtained access token via the
  `TESLA_ACCESS_TOKEN` env var (used until it expires, no refresh) — a simple bearer-paste path and
  the local-test seed. See the DECISIONS doc.

## Architecture

### New server services (`Server/Services`)

**`EcryptfsDecryptor`** — stateless static crypto.
- `bool IsEncrypted(ReadOnlySpan<byte> head16)`
- `EncryptedHeader ParseHeader(Stream)` → `{ PlaintextSize, KeyId, PublicKey, Vin, Timestamp,
  WrappedKey, HasCloudKey }`
- `void DecryptTo(Stream src, byte[] fek, Stream dst)`
- Ships a `SelfCheck()` that decrypts the known sample with its known FEK and asserts the
  output size + a valid MP4 `ftyp`/`moov`.

**`TeslaAuthService`** — singleton, owns the live credential.
- `Task<string> GetAccessTokenAsync()` — returns a valid access token, refreshing when within
  a safety window of expiry. **Single-flight** (one refresh at a time; concurrent callers await it).
- On rotation, persists the new refresh token via `ISettingsProvider` (atomic). Persistence
  failure → keep in-memory, log, rely on the ~24h grace window.
- Reads the refresh token from `Settings.TeslaRefreshToken`. Exposes `TeslaAuthStatus`
  `{ Connected, AudienceOk, HasXEnc, LastError, ExpiresAt }`.

**`TeslaKeyService`**
- `Task<IReadOnlyDictionary<string, byte[]>> FetchFeksAsync(IReadOnlyList<EncryptedHeader>)` —
  builds the batch request, calls `/api/1/decrypt/batch` with the bearer from `TeslaAuthService`,
  returns FEK per item id; per-item errors captured (ownership failures skipped, not fatal).

**`ClipDecryptionService`** — orchestrator.
- `Task<DecryptResult> EnsureEventDecryptedAsync(string eventDir)`:
  1. Enumerate the event's `.mp4`s; parse headers; keep only `IsEncrypted && HasCloudKey`.
  2. Map each to a cache path under `DecryptedCachePath` (source tree mirrored). Skip files
     already cached and valid.
  3. Batch-fetch FEKs → decrypt each to a temp file → atomic rename into cache.
  4. Probe decrypted durations (existing `HybridDurationProbeService`).
  5. Return updated segments (decrypted URLs + real durations) or a typed error.
- **Per-event lock** so a double-click/parallel request decrypts once.

**`DecryptedCacheCleanupService`** — background hosted service (clone of `ExportCleanupService`).
- Enforces `DecryptedCacheMaxGb` (default 10) via **LRU eviction by last-access time**.

### Cache + serving
- New setting `DecryptedCachePath` (default `<dir of CacheDatabasePath>/decrypted`), mirrors the
  source relative path.
- `ApiController.ServeFile` / `IsUnderRootPath` extended to also permit files under
  `DecryptedCachePath`. Decrypted `VideoFile.Url` points at `/Api/Video/<cache path>`.

### Indexing changes (`ClipsService`)
- In `TryParseVideoFileAsync`: read the first 16 bytes; if `EcryptfsDecryptor.IsEncrypted`,
  build `VideoFile { IsEncrypted = true, Duration = TimeSpan.Zero }` and **skip ffprobe**.
  (Fixes today's silent drop: `duration == null` currently makes `ProcessVideoFileAsync`
  return null, so encrypted files vanish from the index.)
- `VideoFile` gains `bool IsEncrypted { get; init; }`.
- `Clip` exposes `IsEncrypted` (true if any segment file is encrypted and not yet decrypted).
  Encrypted clips use the generic thumbnail (their `thumb.png` is unrecoverable).

### Data model & SQLite
- Add `is_encrypted INTEGER NOT NULL DEFAULT 0` to `video_files` via a **guarded
  `ALTER TABLE ... ADD COLUMN`** (check `PRAGMA table_info` first; the schema uses
  `CREATE TABLE IF NOT EXISTS` with no version table today).
- Persist/load `is_encrypted` in the insert/select paths.

### Settings additions (`Settings` + `AppSettings` dialog surface)
- `TeslaRefreshToken` (string, **secret** — masked, never logged).
- `DecryptedCachePath` (string).
- `DecryptedCacheMaxGb` (int, default 10).
- Saving `TeslaRefreshToken` triggers a validation refresh; result feeds `TeslaAuthStatus`.

### API surface (`ApiController`)
- `POST /Api/PrepareEvent?path=<eventDir>` → `EnsureEventDecryptedAsync`; returns the updated
  `Clip` (decrypted URLs + durations) or a typed error.
- `GET /Api/TeslaStatus` → `{ connected, audienceOk, hasXEnc, lastError }`.
- `/Api/Video` transparently serves decrypted cache files (via the extended root check).

### Client UX (Blazor + MudBlazor 6.11.1)
- Encrypted clip cards: **lock badge** overlay on the generic thumbnail.
- Opening an encrypted clip:
  - Not connected → dialog: *"These clips are encrypted. Connect your Tesla account in
    Settings to unlock."* + button to open Settings.
  - Connected → "Decrypting…" overlay → `POST /Api/PrepareEvent` → load decrypted segments →
    play. Errors → plain-language messages (reconnect / ownership / decrypt failed).
- Settings dialog: new **"Tesla account"** section (reuses the grouped `SettingsDialog`): masked
  `TeslaRefreshToken` field, a status chip (*Connected / Not connected / Reconnect needed*), and
  a one-line "how to get this token" helper.
- Tooltip explaining the permanent limitation: *thumbnail & event metadata stay on the vehicle
  and can't be recovered.*

## Error taxonomy (`DecryptResult` / `PrepareEvent`)
- `NotConnected` — no/invalid refresh token → UI prompts to connect.
- `RefreshFailed` — refresh rejected (expired/password reset) → UI prompts to reconnect.
- `OwnershipFailed` — `unable_to_verify_ownership` for a cloud-key'd file (unexpected) → surfaced;
  zeroed-key files are skipped silently, not errors.
- `HeaderInvalid` — magic/header parse failure → file treated as plaintext or skipped.
- `DecryptError` — I/O or crypto failure mid-decrypt → temp discarded, retriable.

## Security
- `TeslaRefreshToken` is a secret: masked in the UI, never logged, DB/store file perms
  tightened where possible. Access tokens live only in memory.
- Rotated refresh tokens persisted atomically (temp + rename).

## Verification
- **Unit self-check:** `EcryptfsDecryptor` decrypts the known sample (`~/Downloads/TeslaDecoded/
  TeslaCam Encrypted/.../2026-06-02_18-16-19-back.mp4`, FEK `1cd202f7d28c970898f249024d18838b`)
  → assert `plaintext_size` and valid MP4.
- **End-to-end:** paste a real refresh token → open an encrypted event → decrypts + plays;
  verify lock badge, the not-connected dialog, and each error state.

## Out of scope / known limitations
- `event.json` (metadata) and `thumb.png` (thumbnail) are **not recoverable** off-vehicle
  (in-car `_CONSOLE` recipient only). Encrypted (and decrypted) clips keep the generic
  thumbnail and have no event metadata.
- In-app OAuth login is **not** built (captcha/Akamai block automated browsers). Obtaining the
  refresh token is a one-time external step; the app only refreshes it.
- No FEK cache (the decrypted-file cache already prevents re-fetching).
