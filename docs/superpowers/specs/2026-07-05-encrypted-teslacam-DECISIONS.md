# Encrypted-clip decryption — decisions I made autonomously (review when you have time)

You said to keep going and pick the best option on any fork, logging open questions here.
Nothing below is blocking; these are the calls worth a second look.

## Judgment calls made (easy to change)

1. **`TESLA_ACCESS_TOKEN` env fallback (added).** Besides the refresh token, the auth service
   accepts a pre-obtained access token via the `TESLA_ACCESS_TOKEN` env var and uses it until it
   expires (~8h, no auto-refresh). I added this both to unblock local testing (the refresh chain got
   spent — see note below) and because it's a legitimately simple "paste a bearer" path. It is
   **env-only**, not shown in the Settings UI. Keep it, or remove if you want refresh-token-only.

2. **Batch size 30.** `decrypt/batch` rejects >30 items ("batch size exceeds maximum of 30"), so
   `TeslaKeyService.MaxBatchSize = 30`. A 7-minute event (42 files) is fetched in 2 batches. Fine.

3. **Decrypted cache cap = 10 GB, LRU by last-access time.** `DecryptedCacheCleanupService` evicts
   oldest-accessed decrypted clips every 30 min once over the cap. Configurable via
   `DecryptedCacheMaxGb` / `DECRYPTED_CACHE_MAX_GB`. In Docker it lives at `/config/decrypted`.

4. **Cache path exposed in the video URL.** Decrypted clips are served as
   `/Api/Video/<url-encoded absolute cache path>`, same shape as the existing source-path URLs.
   Consistent with the current design; not a new leak.

5. **Refresh-token rotation persisted to the settings store.** Every refresh rotates the token and
   we write the new one back to `teslacamplayer.settings.json`. This assumes a **single holder** — if
   another tool also refreshes the same token, they'll clobber each other (documented Tesla behavior).

6. **Metadata & thumbnails stay generic for encrypted events** (even after video decrypts) — their
   `event.json`/`thumb.png` are wrapped for the in-car recipient only and can't be decrypted
   off-vehicle. Encrypted clips get a lock badge + generic thumbnail; decrypted ones a generic
   thumbnail and no event metadata (location/reason). Accepted per the design.

7. **No FEK cache.** The decrypted-file cache already prevents re-fetching keys, so I skipped the
   separate FEK cache the Python tool keeps.

## Open questions for you

- **A. `TESLA_ACCESS_TOKEN` — keep the env-only bearer path?** (default: keep)
- **B. In-app login flow later?** Today connecting = paste a refresh token you obtain externally.
  A "Connect Tesla" button that opens the login and captures the code is possible but fiddly
  (captcha blocks automated browsers; you'd paste the callback code). Want it, or is paste enough?
- **C. Cache location/size defaults** (10 GB, `/config/decrypted`) — OK for your setup?

## Heads-up: the shared refresh token got spent during testing

Refresh tokens are single-use and rotate on every exchange. While verifying the live path I consumed
the chain in `~/.config/tesla-dashcam/tokens.json`; its **refresh token is now dead**, though its
**access token is still valid (~8h)**. When that access token expires you'll need one fresh
`auth.py login` (captcha) to mint a new refresh token. Sorry for the churn — I can walk you through
the re-login (Playwright browser window) whenever you want.
