# NAS Streaming + TV Channel Plan

**Status:** Proposed (2026-06-12). Prerequisite: file locations attached to movies in the DB (see Phase 0).
**Scope:** Stream the NAS movie library through theater.carpouzis.com behind the site's existing
cookie auth, with a per-movie watch page and passive "TV channel" players. Boardgames unaffected.

---

## 1. Goals and non-goals

Goals, in priority order (from Eric):

1. **NAS health** — files are read in place, never moved or rewritten; minimal scan/IO churn.
2. **Stream performance** — local transcoding with user-selectable quality; direct-stream (no
   re-encode) whenever the source codecs allow it.
3. **Content support** — the whole library plays: any container (mkv/avi/wmv/ts/…), legacy codecs,
   DTS/TrueHD audio, PGS/VobSub subtitles, HDR sources.
4. The **site stays the product** — users log in with existing site accounts and never see another
   app. Streaming is restricted to **password-verified sessions**: legacy passwordless logins can
   browse but not stream (§3.1). Age restrictions keep working. Watching feeds the existing Seen
   tracking.
5. **TV channel mode** — passive, scheduled, join-in-progress playback you can leave on in the
   background, shared across viewers ("what's on channel 3?").

Non-goals (v1): multiple quality versions per movie, watch-party synced seeking, Chromecast,
downloads/offline, boardgame videos. All possible later; none constrain this design.

## 2. Decision: Jellyfin as a headless streaming engine

**Recommendation: yes — use Jellyfin, but only as plumbing.** Grant it read-only access to the NAS,
keep it off the public internet, and put every byte and every pixel of UI behind the MovieTheater
site. Users never know Jellyfin exists; it plays the same role SQL Server does.

Why not roll-your-own ffmpeg? An MVP (spawn ffmpeg → HLS → hls.js) is a fun weekend and works for
clean H.264 MP4s. The stated priorities are exactly where it stops being a weekend:

| Requirement | What roll-your-own actually means |
|---|---|
| "All my filetypes" | Reimplementing a codec/container compatibility matrix: DTS→AAC, TrueHD, PGS/VobSub burn-in, VC-1, interlaced sources, 10-bit HEVC, HDR tonemapping |
| Quality options | Session manager per viewer: restart-at-offset transcodes for seeking/quality switches, segment GC, abandoned-session reaping |
| NAS/CPU health | Transcode throttling (pause ffmpeg when the buffer is ahead), hardware-accel plumbing per GPU vendor |

Jellyfin ships all of that behind a stable REST API, with literal person-decades of edge cases
handled. The parts worth building yourself — the player UX, the channel scheduler, the integration
with Viewings/age-gates — are exactly the parts this plan builds.

**Hedge for the roll-my-own itch:** the backend exposes streaming through one small interface
(`IStreamSource`: resolve movie → playable HLS session; report progress; stop). Jellyfin is the
v1 implementation. If you later want to replace it with your own ffmpeg pipeline, you swap that
implementation; the player, watch page, channels, and DB schema don't change.

Why this is also the *NAS-healthiest* option: configured per §4, Jellyfin's steady state is
sequential, throttled, read-only reads of exactly the files being watched — no writes to the share,
no file moves, no background full-file reads. The reason to withhold file access from Jellyfin
disappears once it's configured as a read-only engine rather than a competing product.

## 3. Architecture

```
                       public internet
  friend ──HTTPS──► theater.carpouzis.com (ingress)
                            │
                    MovieTheater API pod (unchanged image — no ffmpeg, no NAS mount)
                    ├── /API/Stream/*      control plane (password-session policy + age gate)
                    ├── /API/Channel/*     TV schedule (same policy, server-computed)
                    └── /jellyfin/Videos/* YARP data plane: password-session gate,
                            │              inject API token, strip /jellyfin prefix
                            ▼ LAN only
                       Jellyfin (existing instance; NOT publicly exposed)
                            │ read-only mount
                            ▼
                           NAS  ◄── files stay exactly where they are
```

- **Control plane** — the site backend calls Jellyfin's API (`PlaybackInfo`, `Sessions`) with a
  server-held API key to start/stop sessions and pick streams. Movie→Jellyfin mapping lives in a
  new `MovieFile` table.
- **Data plane** — HLS playlists and segments flow through a new YARP route. YARP already fronts
  the UI (`appsettings.json` → `ReverseProxy`), and `Startup.Configure` already runs
  authentication before `MapReverseProxy`, so an `AuthorizationPolicy` on the route gives
  password-gated streaming (§3.1) with zero new infrastructure. Jellyfin's HLS playlists use
  relative segment URLs, so a prefix-preserving proxy (`/jellyfin/Videos/{**}` → `Videos/{**}`)
  needs no playlist rewriting.

### 3.1 Streaming authorization — password-verified sessions only

Site auth now supports passwords (`User.PasswordHash`, null = legacy passwordless login;
`/API/Login` verifies with lockout; `/API/SetPassword` is self-service). Two facts drive the
design: unknown usernames still auto-create passwordless accounts, and the login cookie currently
carries only `NameIdentifier` + `Name`. So **"authenticated" is not a trust boundary — password
verification is**, and the gate must be on the *session*, not merely the account:

- **Sign-in stamps the session.** When `/API/Login` verifies a password, it adds an `amr=pwd`
  claim (authentication-method-reference) to the cookie principal. Passwordless logins get no such
  claim. `/API/SetPassword` re-issues the cookie: setting a password from a passwordless session
  adds the claim (setting it proves account control); removing the password drops it.
- **One policy guards every streaming surface.** `StreamingUser` = authenticated **and**
  `amr=pwd`, registered in `ConfigureServices`, applied to `StreamController`,
  `ChannelController`, and the YARP `/jellyfin/Videos/{**}` route. Claim checks are in-memory, so
  the per-segment hot path costs no DB query.
- **Revocation is bounded, not 30 days.** Cookies live 30 days, so the claim alone would outlive a
  removed password. A cookie `OnValidatePrincipal` hook re-checks the DB on a short interval
  (~5 min): a principal claiming `amr=pwd` whose account no longer has a password (or no longer
  exists) is rejected. Authorization runs on every playlist/segment request, so an active stream
  of a revoked user dies within that window.
- **Defense in depth on the data plane.** Stream URLs carry no tokens — auth is the HttpOnly
  cookie (SameSite=Lax covers the SPA's same-origin fetches; state-changing stream endpoints are
  JSON POSTs). The Jellyfin API key is attached server-side only and never reaches a browser.
  Nothing outside `/Videos/*` is proxied, and Jellyfin itself stays LAN-only. A shared segment URL
  is useless without a password-verified cookie: 401 anonymous, 403 passwordless.
- **UI gating is courtesy, server policy is enforcement.** The login payload already returns
  `hasPassword`; the frontend hides Watch buttons and the TV page for passwordless sessions, but
  every byte is policy-checked regardless.
- If "any password user" later proves too broad, a `CanStream` row in `UserSettings` slots into
  the same policy beside the existing `CanEditMovies` pattern.
- Remote friends' bandwidth ceiling is the house's upload speed — that's physics regardless of
  engine; the quality ladder's low rungs (and a sane default) are the mitigation.

## 4. Jellyfin configuration for NAS health (Phase 0 checklist)

Settings that make Jellyfin a polite NAS citizen — this is the part that makes "grant it access"
safe:

- [ ] Mount the share **read-only** (NFS `ro` / SMB read-only credential). Jellyfin physically
      cannot write, move, or rename media.
- [ ] Library settings: **real-time monitoring OFF** (inotify is unreliable over network mounts and
      degrades to polling), scheduled scan nightly or manual-only.
- [ ] **Trickplay OFF** and **chapter-image extraction OFF** (library + server level). These are
      the two features that read *every file end-to-end*; without them a scan only reads headers.
- [ ] Metadata/artwork saved to Jellyfin's local data dir; "save metadata into media folders" OFF
      (NFO/jpg sidecar writing) — moot on a read-only mount but keeps logs clean.
- [ ] **Transcode temp path on local SSD**, never the NAS. Enable **segment deletion** to bound
      disk use on long sessions.
- [ ] **Transcode throttling ON** — ffmpeg pauses when ~min buffer is ahead of the viewer, so an
      idle background TV tab consumes bursts, not a sustained full-rate read + encode.
- [ ] Hardware acceleration if the host has it (QSV/NVENC/VAAPI) + HDR tone-mapping. *Open
      question: what GPU does the Jellyfin host have?*
- [ ] Create a dedicated **API key** (Dashboard → API Keys) for the site; don't reuse a personal
      login. Keep Jellyfin unreachable from the internet (LAN/cluster network only).

Steady-state IO profile after this: one sequential read per active viewer, bursty under throttle;
scans touch headers only on your schedule; zero writes.

## 5. Data model (additive only)

Three new tables, EF migrations in the established pattern (additive, legacy untouched; prod
migration runs only with explicit per-migration authorization, backup first).

```
MovieFile                          -- canonical home for "where is this movie on disk"
  Id              int PK
  MovieID         int FK → Movie (index; v1 treats one file per movie, schema allows more)
  Path            nvarchar(1024)  -- as the NAS knows it, e.g. \\nas\movies\Heat (1995)\Heat.mkv
  JellyfinItemId  nvarchar(64)?   -- filled by sync
  DurationTicks   bigint?         -- actual file duration from Jellyfin (credits included);
                                  --   channel scheduling depends on this, not IMDB runtime
  Container       nvarchar(16)?   VideoCodec nvarchar(32)?  AudioCodec nvarchar(32)?
  Width int?  Height int?  SizeBytes bigint?
  LastSyncedUtc   datetime2?      MissingSinceUtc datetime2?  -- set when sync can't find it
```

> **Status (2026-06-12):** file locations are seeded in a new `Movie.FilePath` column
> (`nvarchar(1024)`, migration `AddMovieFilePath`, 5,812/5,943 filled by the NAS mapping pass —
> see `data/movie-file-mapping.csv`). If/when `MovieFile` is introduced in Phase 1, migrate from
> that column; until then the Jellyfin sync reads `Movie.FilePath` directly.

```
MoviePlaybackProgress              -- cross-device resume + auto-Seen
  Id int PK,  UserID int FK,  MovieID int FK   (unique UserID+MovieID)
  PositionTicks bigint,  DurationTicks bigint,  UpdatedUtc datetime2,  Completed bit

Channel                            -- TV channel definitions (admin-editable)
  Id int PK,  Name,  Description?,  SortOrder int,  Enabled bit
  FilterJson nvarchar(max)         -- { genreIds:[..], genreMode:"all"|"any", yearMin?, yearMax?,
                                   --   maxMpaRatingId?, unwatchedOnly?, excludeRemoveFromRandom:true }
  Seed int,  ShuffleMode ("SeededShuffle"|"Chronological"|"ReleaseDate")
  AnchorUtc datetime2              -- schedule epoch

ChannelScheduleItem                -- materialized schedule (see §8)
  Id bigint PK,  ChannelId FK (index w/ StartUtc),  MovieID FK
  StartUtc datetime2,  EndUtc datetime2
```

New flat config keys on `MovieTheaterConfiguration` (delivered in prod via the existing
`MOVIETHEATER_APPSETTINGS_JSON` secret):

```jsonc
"JellyfinBaseUrl": "http://<jellyfin-host>:8096",
"JellyfinApiKey": "…",
"JellyfinPathMappings": [ { "DbPrefix": "\\\\nas\\movies\\", "JellyfinPrefix": "/media/movies/" } ],
"StreamingMaxConcurrentTranscodes": 0   // 0 = unlimited; friendly "theater full" error when hit
```

## 6. Backend work

**`JellyfinService`** (new, in `MovieTheater.Services`, registered in `AddMovieTheaterServices`)
wrapping the handful of Jellyfin endpoints we use: enumerate movie items with `Path` +
`MediaSources` + `ProviderIds`; `POST /Items/{id}/PlaybackInfo`; `POST /Sessions/Playing`
(start/progress/stopped); `DELETE /Videos/ActiveEncodings`; `GET /Sessions`;
`POST /Library/Media/Updated` (targeted per-path rescan). *Verify exact shapes against the
installed Jellyfin version in Phase 1 — these routes have been stable since the Emby fork, but
pin the version and smoke-test before building on them.*

**`sync-jellyfin` CLI command** (CliFx, same shape as `scrape-imdb`, ctor takes
`MovieTheaterConfiguration`): pull Jellyfin's movie items → translate paths via
`JellyfinPathMappings` (normalize separators/case) → match against `MovieFile.Path` → store
`JellyfinItemId`, `DurationTicks`, codec/tech fields. Fallback match on IMDB id via Jellyfin
`ProviderIds` for stragglers, flagged for review rather than silently trusted. Prints a two-way
diff: DB files Jellyfin doesn't have (→ `MissingSinceUtc`), Jellyfin items the DB doesn't track.
Re-runnable any time; also exposed as an admin API endpoint later if useful.

**Auth plumbing** (small, see §3.1): `amr=pwd` claim in `/API/Login`'s password-verified branch,
cookie re-issue in `/API/SetPassword`, the `StreamingUser` policy in `ConfigureServices`, and the
`OnValidatePrincipal` revalidation hook on the cookie options.

**Stream control endpoints** (`StreamController`, `[Authorize(Policy = "StreamingUser")]`, mirrors
`APIController` patterns):

- `POST /API/Stream/Start` `{ movieId, maxBitrateBps?, audioStreamIndex?, subtitleStreamIndex?,
  startSeconds? }` →
  1. Age gate: reuse the exact browse-side rule — `GetMPARatingFromMovieRating(movie.Rating)` vs
     the user's `AgeRestriction` setting (default 100). Same semantics as `GetMovie`, no drift.
  2. Optional concurrency guard via Jellyfin `/Sessions`.
  3. `PlaybackInfo` with a fixed server-side web DeviceProfile: HLS out; H.264+AAC always allowed;
     copy (direct-stream) video/audio when compatible; text subs (SRT/ASS/VTT) delivered as
     sidecar WebVTT, image subs (PGS/VobSub) burned in; `MaxStreamingBitrate` from the request.
  4. Returns `{ playSessionId, hlsUrl /* /jellyfin/Videos/… */, durationTicks, isDirectStream,
     audioTracks[], subtitleTracks[], resumePositionTicks? }`.
- `POST /API/Stream/Progress` `{ playSessionId, movieId, positionTicks, paused }` — forwards to
  Jellyfin (keeps throttling honest) + upserts `MoviePlaybackProgress`; at ≥90% watched, inserts a
  `Viewing { ViewingType: "Seen" }` for the user if absent — streaming feeds the tracker the site
  exists for.
- `POST /API/Stream/Stop` — reports stopped + `DELETE /Videos/ActiveEncodings` (kills ffmpeg and
  cleans segments immediately instead of waiting for Jellyfin's idle timeout).

**YARP data-plane route** (config in `appsettings.json` next to the existing catch-all, which stays
`Order: 1`):

```jsonc
"jellyfin-hls": {
  "ClusterId": "jellyfin", "Order": 0,
  "Match": { "Path": "/jellyfin/Videos/{**rest}" },   // playlists, segments, and Stream.vtt all live under /Videos
  "AuthorizationPolicy": "StreamingUser",             // authenticated + amr=pwd (§3.1); pipeline order already correct
  "Transforms": [ { "PathRemovePrefix": "/jellyfin" } ]
}
```

plus an `ITransformProvider` that stamps `X-Emby-Token` from config onto proxied requests — the
API key never reaches the browser, and nothing outside `/Videos/*` is reachable from the internet.

## 7. Frontend work

New dependency: `hls.js` (Safari falls back to native HLS). Everything else is existing stack
(React Router 5 routes in `App.js`, fetch wrappers in `MovieAPI.js`, AntD 4).

**`<VideoPlayer>`** (shared by watch page and TV): hls.js wiring, poster art backdrop,
play/pause/seek/volume/fullscreen, keyboard shortcuts, Media Session metadata (title + poster on
the OS media overlay), progress beacon every ~10s + `visibilitychange`/`pagehide` → `Stream/Stop`
(with `navigator.sendBeacon` so closing the tab still kills the transcode). Menus:

- **Quality:** Original (direct-stream/remux) / 1080p 12M / 1080p 8M / 720p 4M / 480p 1.5M.
  Switching calls `Stream/Start` again with `startSeconds = currentTime` and swaps the source —
  Jellyfin's single-variant HLS doesn't do automatic ABR, so switches are explicit
  restart-at-position, same as Jellyfin's own web client. Choice persists in `localStorage`.
- **Audio / Subtitles:** tracks from `Stream/Start`; changing audio restarts the session
  (server-side remux), text subs toggle client-side as VTT tracks.

**Watch page** (`/watch/:movieId`): player + title/year/runtime header, resume prompt when
`MoviePlaybackProgress` exists ("Resume at 1:12:40 / start over"), auto-Seen indicator. Entry
points: a **Watch** button on `MoviePage` and in `MovieModal`, rendered only when the movie has a
mapped file, the session is password-verified (`hasPassword` from the login payload — passwordless
users see a "set a password to stream" hint instead), and the age gate passes.

**TV page** (`/tv`, `/tv/:channelId`): full-bleed player that joins the current schedule item at
the live offset, auto-advances, and otherwise hides chrome. Channel switcher (click + number keys +
arrow up/down), translucent now/next overlay that fades after a few seconds, small channel "bug" in
a corner, simple EPG strip (now / next / later from the schedule API). Starts **muted** to satisfy
browser autoplay policy with a "tap to unmute" affordance; requests a Screen Wake Lock where
supported. Optional: a beat of static between items for the channel-surfing vibe. TV viewing
reports progress (for throttling) but does **not** auto-mark Seen by default — passive background
play shouldn't claim you watched Heat. (Configurable later if wanted.)

## 8. TV channel scheduling

Design goal: every viewer of a channel sees the same movie at the same offset — real broadcast
feel, shareable ("turn on channel 2").

- **Materialized schedule, not a pure function of time.** A pure deterministic
  `f(channelSeed, now)` is elegant but re-shuffles history whenever the eligible movie set changes
  (every library insert). Instead, `ChannelScheduleItem` rows are generated ahead lazily: when a
  `Now` query finds less than ~48h of future schedule, extend it (seeded shuffle of the channel's
  current eligible set — genre FKs from `MovieGenre`, year range, rating ceiling, optional
  unwatched-only, always excluding `RemoveFromRandom`; durations from `MovieFile.DurationTicks`,
  fallback `RuntimeMinutes`, skip movies with neither). Past rows are pruned after a few days.
  Already-written rows are never rewritten, so the lineup is stable, and manual curation (a
  hand-built Halloween night) becomes possible later by just inserting rows.
- **API:** `GET /API/Channel/List` (gated: a channel is visible only if its rating ceiling passes
  the user's age restriction — a shared timeline can't censor per-viewer, so restriction applies
  per-channel); `GET /API/Channel/{id}/Now` → `{ current: { movieId, offsetSeconds, endsAtUtc },
  next: [...] }` with offset computed server-side so client clock skew doesn't matter;
  `GET /API/Channel/{id}/Guide?hours=12` for the EPG. Admin CRUD gated on the existing
  `CanEditMovies` setting.
- **Client loop:** `Now` → `Stream/Start(movieId, startSeconds=offset)` → play → on `ended` (or
  `endsAtUtc` + small grace) → `Now` again. Joining mid-movie is just Jellyfin starting the
  transcode at an offset — the same mechanism as seeking.
- **Cost model:** each viewer is an independent transcode session even at the same offset. At
  friends-scale (a handful of concurrent viewers) with throttling and hardware accel this is fine;
  if it ever isn't, the escape hatch is an ErsatzTV-style shared simulated-live stream — noted as
  Phase 5, not built now.

## 9. Content support (what plays how)

| Source | Result |
|---|---|
| H.264 + AAC/MP3 (any container) | Direct-stream: container remux to HLS, **no re-encode**, near-zero CPU |
| HEVC/AV1 sources | v1: transcode → H.264 (universal). Capability-based HEVC passthrough is a Phase 5 nicety |
| VC-1 / MPEG-2 / Xvid / interlaced | Transcode (+ deinterlace) |
| AC3 / E-AC3 / DTS / TrueHD audio | Audio-only transcode to AAC where video can still be copied |
| Multiple audio tracks | Selectable in player |
| SRT / ASS / embedded text subs, sidecar .srt | WebVTT track, toggle without re-encode |
| PGS / VobSub image subs | Burned in (forces video transcode) — correct and automatic |
| HDR10 | Tone-mapped to SDR (needs GPU to be pleasant — see hardware question) |

## 10. Phases

**Phase 0 — prerequisites (Eric + an evening of config, no code)**
File locations written into `MovieFile` (table can ship first as a lone migration). Jellyfin: NAS
library added read-only with §4 checklist, API key minted, reachable from the API pod's network,
version noted. *Acceptance:* a movie plays in Jellyfin's own UI on the LAN; a full library scan
causes no NAS write and completes without trickplay/chapter jobs.

**Phase 1 — mapping (small)**
`MovieFile` migration, `JellyfinService` read paths, `sync-jellyfin` command + path mappings.
*Acceptance:* >95% of movies matched with item id + real duration; printed straggler report; unit
tests on path translation (the UNC↔mount normalization is where bugs will live).

**Phase 2 — watch streaming (the meat)**
Auth plumbing (§3.1: `amr` claim, `StreamingUser` policy, cookie revalidation), Stream
Start/Progress/Stop + age gate, YARP route + token transform, `<VideoPlayer>`, watch page, Watch
buttons. *Acceptance:* a DTS+PGS mkv and a clean mp4 both play on desktop Chrome/Firefox, iPhone
Safari, and an Android phone, remotely over TLS; quality switch resumes within ~2s of position;
closing the tab kills the ffmpeg process within ~30s (verify in Jellyfin dashboard); anonymous
fetch of a segment URL gets 401; a **passwordless session** gets 403 from both `Stream/Start` and
a direct playlist/segment fetch; removing a user's password kills their in-progress stream within
the revalidation interval; an under-age password user gets no Watch button and a 403 from
`Stream/Start`.

**Phase 3 — polish (medium)**
Resume prompts, auto-Seen at 90%, concurrency guard + friendly error, "file missing" surfacing from
sync flags, admin now-streaming view (sessions passthrough), targeted `Library/Media/Updated` poke
when a movie's file is attached/changed.

**Phase 4 — TV channels (medium-large)**
Channel + ChannelScheduleItem migrations, schedule generator + Now/Guide APIs, `/tv` page with
switcher/EPG/overlay, seed channels ("Everything", per-genre from `MovieGenre`, "Unseen by Eric").
*Acceptance:* two browsers on one channel show the same frame within ~2s; an 8-hour background run
advances through ≥4 movies without interaction; schedule survives app restart unchanged; restricted
user doesn't see the R-rated channel.

**Phase 5 — stretch shelf (explicitly not now)**
Watch-party synced playback, HEVC passthrough via capability detection, ErsatzTV-style shared
channel streams, Chromecast, bumpers/idents between channel items, channel-watch Seen opt-in.

## 11. Risks and mitigations

- **Path mapping drift** (UNC vs mount, case, separators) — single normalization function, unit
  tests, two-way sync report; never guess silently (IMDB-id fallback matches are flagged).
- **Jellyfin API surface drift** — pin the server version; the five endpoints used are old and
  stable; smoke-test in Phase 1 before anything is built on top.
- **CPU/HDR tonemapping without GPU** — measure one 4K HDR transcode in Phase 0; if the host is
  CPU-only, cap the default quality and prefer SDR sources until hardware shows up.
- **Upload bandwidth for remote friends** — default remote quality 4 Mbps; ladder goes down to
  1.5; this is a physics limit, not an architecture one.
- **Abandoned transcodes** — beacon-on-close *plus* Jellyfin's own no-progress idle kill as the
  backstop; verified explicitly in Phase 2 acceptance.
- **Cookie outlives a removed password** — 30-day cookies carry `amr=pwd`; bounded by the
  `OnValidatePrincipal` DB re-check (§3.1), so revocation takes minutes, not weeks. The in-memory
  login lockout resets on pod restart — acceptable at friends-scale.
- **Deploy-side unknowns** — the k8s manifests referenced by DEPLOYMENT.md aren't in this repo, so
  pod→Jellyfin reachability gets confirmed on the host in Phase 0 rather than assumed.

## 12. Open questions (none block Phases 0–1)

1. What hardware does the Jellyfin host have (QSV iGPU / NVIDIA / CPU-only)? Decides default
   quality ladder and HDR story.
2. Where does Jellyfin run relative to the MicroK8s host — same box, another box, container? Only
   affects how `JellyfinBaseUrl` resolves from the pod.
3. NAS protocol for the Jellyfin mount — NFS or SMB? (Either is fine; NFS tends to behave better
   for streaming reads.)
4. Expected concurrency — is "everyone watches Friday night" 3 streams or 10? Sets whether the
   concurrency guard matters in Phase 3.
5. Path format you'll store in `MovieFile.Path` — UNC (`\\nas\…`) or the NAS-local path? Either
   works; it just fixes the `JellyfinPathMappings` entry.

## 13. Alternatives considered

- **Pure roll-your-own ffmpeg** — rejected for v1 (see §2); the `IStreamSource` seam keeps it
  possible later without rework.
- **Jellyfin as the user-facing app** — rejected: separate accounts, no age-restriction tie-in, no
  Viewing integration, abandons the site as the product.
- **ErsatzTV for channels now** — deferred: another service to run, and per-viewer sessions are
  fine at this scale; revisit only if concurrent TV viewership makes shared streams worth it.
- **Direct NAS mount in the API pod + naive range-request serving** — rejected: only plays
  browser-native files (a fraction of a varied library), no quality control, and seeks in
  non-faststart files hammer the NAS with random reads.
