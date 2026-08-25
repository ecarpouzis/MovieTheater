# Books host — install, deploy, seam (R5)

The Books host (`MovieTheater.BooksHost`) is a Windows service on the media host, fronted by Caddy, reached by the
site pods through a Yarp route under the site's own origin. This page is the runbook; the design is in the plan's
R5 section and `v2-model.md`.

## Topology

```
browser ──(cookie)──► site pod ──/API/Books/* ──► Yarp ──X-MT-Identity──► https://books-host.<domain> ──► Caddy ──► localhost:2204
browser ──(Basic)───► site pod ──/opds/*     ──► Yarp ──X-MT-Identity──► (same)
browser ──(media token in the path)──────────────────────────────────────► https://books-host.<domain>/m/{token}/…
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
    "PublicBaseUrl": "https://books-host.<domain>",
    "IdentityTokenSecret": "<same value as the pod's BooksTokenSecret>",
    "MediaTokenSecret": "<host-only random secret>",
    "DbPath": "F:\\Work\\MovieTheater\\data\\books\\v2\\books.db",
    "LegsDbPath": "F:\\Work\\MovieTheater\\data\\books\\v2\\books-legs.db",
    "CacheDir": "<thumbnail cache root: {id}.webp files>",
    "ReportDir": "F:\\Work\\MovieTheater\\data\\books\\v2",
    "V1SourcePath": null, "CalibreLinkPath": null, "V1OwnerUsername": null, "OwnerUserId": 1
  }
}
```

## First install (elevated pwsh, on the media host)

1. `.\scripts\deploy-books-host.ps1 -SkipRestart` — publishes and copies the binaries to `C:\BooksHost\app`.
2. Write `appsettings.Production.json` as above.
3. `.\scripts\install-books-host-service.ps1` — nssm service `BooksHost`, runs `MovieTheater.BooksHost.exe web` as the installing user (NAS access), `ASPNETCORE_ENVIRONMENT=Production` is its only environment variable; logs rotate under `C:\BooksHost\logs`.
4. Caddy: add the site block and the host to the explicit `http://` redirect list, then `caddy validate --config C:\caddy\Caddyfile` and reload:
   ```
   books-host.<domain> {
       import altsvc_clear
       reverse_proxy localhost:2204
   }
   ```
5. DNS: `books-host.<domain>` CNAME → the existing anchor record (the same way `arcade.` is a CNAME).
6. Site pod secret (`MOVIETHEATER_APPSETTINGS_JSON`, per the movietheater-secret recipe): add `"BooksHostBaseUrl": "https://books-host.<domain>"` and `"BooksTokenSecret": "<shared>"`; restart the pods.
7. Grant: run `scripts/books/migrate-books-access.sql` end-to-end (count → insert → recount): every user with the legacy `ComicSiteAccess` row gets `BooksAccess = true`; the legacy row stays until R8.

## Every later deploy

`.\scripts\deploy-books-host.ps1` (elevated). It publishes, snapshots `app.bak-<label>` once, swaps everything except `appsettings*.json`, restarts, and verifies by behaviour: `/healthz` 200, `GET /ping` without identity **401** (404 = old binary), `/m/bogus/thumbs/1.webp` 403, exactly one `Access-Control-Allow-Origin`. Roll back with `-Rollback <snapshot>`.

## Proving the seam

- Host, direct: `GET https://books-host.<domain>/ping` → 401 (no identity header can be forged from outside).
- Through the site with a password-verified session: `GET https://<site>/API/Books/ping` → `{ userId, username, isAdmin, maturity, host: "books-host", utc }`.
- OPDS: `curl -u <user>:<site password> https://<site>/opds/ping` → the same echo; a wrong password → 401 with `WWW-Authenticate: Basic realm="Books"`.
- Media: `GET /API/Books/media-token` → `{ token, baseUrl }`; `GET {baseUrl}/m/{token}/thumbs/{id}.webp` → the cover (`Cache-Control: private`, ETag, 304 on re-request).
- After the first ping the host's `KnownIdentity` table holds that user (the cache warmer's input in R6).
