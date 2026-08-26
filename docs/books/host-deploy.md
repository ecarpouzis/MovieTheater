# Books host — install, deploy, seam (R5)

The Books host (`MovieTheater.BooksHost`) is a Windows service on the media host, fronted by Caddy, reached by the
site pods through a Yarp route under the site's own origin. This page is the runbook; the design is in the plan's
R5 section and `v2-model.md`.

## Topology

```
browser ──(cookie)──► site pod ──/API/Books/* ──► Yarp ──X-MT-Identity──► https://books.<domain> ──► Caddy ──► localhost:2204
browser ──(Basic)───► site pod ──/opds/*     ──► Yarp ──X-MT-Identity──► (same)
browser ──(media token in the path)──────────────────────────────────────► https://books.<domain>/m/{token}/…
```

- `/API/Books/{**}` — policy `RequireBooksAccess` (password-verified session + `UserSettings.BooksAccess = true`), prefix stripped, `Cookie` removed, identity header stamped (`userId|username|isAdmin|ceiling|exp`, 60 s + 30 s grace).
- `/opds/{**}` — policy `RequireBooksAccessBasic` (HTTP Basic verified at the pod with the site password; the host never sees it), prefix kept, `Authorization` removed, identity header stamped.
- `/m/{token}/…` — bytes straight from the host; the token (12 h) is minted by the host at `GET /API/Books/media-token`.
- Routes exist only when the pod has `BooksHostBaseUrl` + `BooksTokenSecret`; an unconfigured pod starts exactly as before.

## Host config (`C:\BooksHost\app\appsettings.Production.json`, never in git, never copied by the deploy)

```json
{
  "Books": {
    "Urls": "http://localhost:2204",
    "SiteOrigin": "https://<site host>",
    "PublicBaseUrl": "https://books.<domain>",
    "IdentityTokenSecret": "<same value as the pod's BooksTokenSecret>",
    "MediaTokenSecret": "<host-only random secret>",
    "DbPath": "F:\\Work\\MovieTheater\\data\\books\\v2\\books.db",
    "LegsDbPath": "F:\\Work\\MovieTheater\\data\\books\\v2\\books-legs.db",
    "CacheDir": "F:\\Work\\MovieTheater\\data\\books-cache\\thumbs",
    "ReportDir": "F:\\Work\\MovieTheater\\data\\books\\v2",
    "ArchiveCacheDir": "F:\\Work\\MovieTheater\\data\\books-cache\\archives",
    "ArchiveCacheGb": 50,
    "PageJpegQuality": 82, "PageCacheLimitMb": 384, "ThumbnailQuality": 75,
    "SevenZipPath": "<7z.exe, for the RAR fallback; null = CBR via SharpCompress only>",
    "EnableTextRegions": true,
    "ComicVineApiKey": null,
    "SiteBaseUrl": "https://<site host>",
    "V1SourcePath": null, "CalibreLinkPath": "<calibre_link.json, for books-import-calibre>", "V1OwnerUsername": null, "OwnerUserId": 1
  }
}
```

- **`CacheDir` must be an MT-owned directory, never the standalone site's live cache.** `books-thumbs` and the admin thumbnail job WRITE `{id}.webp` files into it, and the parallel run (R10) keeps the two services' caches separate. Until R10's robocopy fills it, the host answers 404 for thumbnails it has not generated — expected, not a fault.
- `ArchiveCacheDir` (whole-archive copies pulled off the share while someone reads) is optional; `ArchiveCacheGb: 0` turns it off.
- `SiteBaseUrl` is what OPDS feeds print as their absolute base (`Opds:SiteBaseUrl` overrides it); missing → the forwarded origin.
- `ComicVineApiKey`, `ThumbnailQuality`, `PageJpegQuality`, `ArchiveCacheGb` are also settable from the admin panel; those writes go to `books.settings.json` beside `books.db` (`SettingsOverlayPath`), never to this file.

## First install (elevated pwsh, on the media host)

1. `.\scripts\deploy-books-host.ps1 -SkipRestart` — publishes and copies the binaries to `C:\BooksHost\app`.
2. Write `appsettings.Production.json` as above.
3. `.\scripts\install-books-host-service.ps1` — nssm service `BooksHost`, runs `MovieTheater.BooksHost.exe web` as the installing user (NAS access), `ASPNETCORE_ENVIRONMENT=Production` is its only environment variable; logs rotate under `C:\BooksHost\logs`.
4. **Hostname ruling (2026-08-25): the host takes `books.<domain>`; the standalone site moves to `longbox.<domain>`.** `books.` is the anchor A record (the other gateways CNAME onto it) and already has a certificate, so the host inherits both. In `C:\caddy\Caddyfile`: rename the existing standalone block to `longbox.<domain>` (still `reverse_proxy localhost:21938`), add `http://longbox.<domain>` to the explicit `http://` redirect list, and turn the `books.<domain>` block into the host:
   ```
   books.<domain> {
       import altsvc_clear
       reverse_proxy localhost:2204
   }
   longbox.<domain> {
       import altsvc_clear
       reverse_proxy localhost:21938
   }
   ```
   `caddy validate --config C:\caddy\Caddyfile`, reload; Caddy issues the `longbox.` certificate on first request (`caddy.log`).
5. DNS: nothing for `books.` (the anchor stays). Add `longbox` CNAME → `books.<domain>` (the same way `arcade.` is a CNAME) **before** the Caddy reload, or the standalone is unreachable until the record propagates.
5b. The site's `UserSettings.ComicSiteAccess` rows hold the standalone's URL (the NavBar's external "Comics" link): update them to `https://longbox.<domain>` at the same time (end-to-end via `SqlConnection`, count → update → recount). Any OPDS reader app pointed at the standalone's `/opds` must be re-pointed by hand.
6. Site pod secret (`MOVIETHEATER_APPSETTINGS_JSON`, per the movietheater-secret recipe): add `"BooksHostBaseUrl": "https://books.<domain>"` and `"BooksTokenSecret": "<shared>"`; restart the pods.
7. Grant: `scripts/books/migrate-books-access.sql` — **already applied 2026-08-25** (3 `ComicSiteAccess` holders → 3 `BooksAccess` rows); re-running is safe (it inserts only missing rows). The legacy row stays until R8.

## Every later deploy

`.\scripts\deploy-books-host.ps1` (elevated). It publishes, snapshots `app.bak-<label>` once, swaps everything except `appsettings*.json`, restarts, and verifies by behaviour: `/healthz` 200, `GET /ping` without identity **401** (404 = old binary), `/m/bogus/thumbs/1.webp` 403, exactly one `Access-Control-Allow-Origin`. Roll back with `-Rollback <snapshot>`.

## First real runs on the host (after the seam is proven; each is chunked/resumable and prints `{ processed, remaining, nextCursor }` per batch)

1. `MovieTheater.BooksHost.exe books-scan` — dry run first (the default): counts the adds/changes/removals it would make against the `LibraryRoot` rows (UNC paths, read-only). Then `--apply`. A file that is gone is MARKED (`Item.IsExcluded` + `ItemState.IsBroken`), never deleted.
2. `books-import-calibre --link <calibre_link.json>` — fills `BookDetail.SeriesName/SeriesIndex/Isbn` (NULL for all 22,084 books after the v1 migration).
3. `books-thumbs` — generates the missing `{id}.webp` files into `CacheDir` (or robocopy the standalone cache in first — R10 — and let this fill the gaps).
4. `books-resolve` (after 1–2) — rebuilds `Resolved*` + FTS from the new inputs.

## Proving the seam

- Host, direct: `GET https://books.<domain>/ping` → 401 (no identity header can be forged from outside).
- Through the site with a password-verified session: `GET https://<site>/API/Books/ping` → `{ userId, username, isAdmin, maturity, host: "books-host", utc }`.
- OPDS: `curl -u <user>:<site password> https://<site>/opds/ping` → the same echo; a wrong password → 401 with `WWW-Authenticate: Basic realm="Books"`.
- Media: `GET /API/Books/media-token` → `{ token, baseUrl }`; `GET {baseUrl}/m/{token}/thumbs/{id}.webp` → the cover (`Cache-Control: private`, ETag, 304 on re-request).
- After the first ping the host's `KnownIdentity` table holds that user (the cache warmer's input in R6).
