# Music Vertical Plan

**Status: Phases 0–7 BUILT 2026-08-04; DEPLOYED + PLAYING IN PROD 2026-08-05.**
Prod bring-up took three separate fixes, each of which looked like the others: (1) the site half
went live on push but the **StreamGateway was never redeployed** — `/s/{token}/MusicFile` didn't
exist in the running binary; (2) album art written locally can never reach prod, so
`MusicImageController` now fetches and persists lazily like `ArcadeImageController` (§Phase 4);
(3) after a host reboot every track 404'd because the gateway ran as **LocalSystem, which has no
credential path to `\\Library\Public`** — see §3.1. Art is at 967/1347 albums; lyrics at
203/20,267 tracks (§Phase 5 backfill still to run).
Phase 0 ran to completion: 333 artists / 1,347 albums / 20,267 tracks ingested, 0 tag errors,
45 embedded/sidecar lyrics captured, 13 tracks (all .wma) flagged RequiresTranscode. Phase 1
verified against a local gateway: mp3 Range → 206, tampered token → 403, traversal → 403.
Phases 2–6 are E2E-verified in headless Chrome on the full local stack (28/28 checks across two
runs: playlists CRUD + drag-reorder, album art on cards/modal/bar, synced-lyrics highlighting,
Butterchurn animating while audio keeps playing, queue restored paused after reload).
Deploy prerequisites when the time comes: prod appsettings gains `MusicLibraryDir` (and optionally
`MusicImagesDir`, else art lands in the posters mount under `music_*`); the StreamGateway host
config gains `MusicRootDir` and the gateway binary is redeployed; the transcode lane additionally
needs `FfmpegPath` on the gateway host and `MusicTranscodeEnabled: true` on the site.
**Operational follow-ups (not run here):** the remote album-art pass (699 albums still artless) and
the full LRCLIB lyrics pass (~20k tracks) — both bounded, resumable CLIs; commands in §Phase 4/5.

Written 2026-08-04 from a survey of the codebase and the live collection. Code comments should
cite sections of this doc (`music-plan.md §4.2`) the way the arcade/streaming plans are cited.

Goal: a `/music` section of the site that streams Eric's audio collection from the NAS —
searchable, grouped by artist and by folder, playing every audio format present, with
playlists, lyrics, and Milkdrop-style visualization.

---

## §1 The collection (surveyed 2026-08-04)

- Root: **`L:\3 - Music`** — 333 artist folders, zero loose files at the root.
- Folder grammar mirrors the movie library:
  - Artist: `Artist (YearRange)` — e.g. `AC-DC (1975-2000)`, `Beck (1994-2008)`.
  - Album: `Artist - Album (Year)` — e.g. `AC-DC - Back in Black (1980)`. Bracket tags appear
    (`Live [Collector's] (1992)`).
- Formats (sampled 14 artists): overwhelmingly **`.mp3`**, some **`.m4a`**, some **`.flac`**
  (confirmed by Eric), folder-art **`.jpg`** sidecars. Not every artist folder is guaranteed to
  nest albums — expect loose tracks, compilations (`A State of Trance`), and odd layouts.
- Estimated scale: order 20k–60k tracks. Big enough that browse must be paged/windowed
  (arcade-style), small enough that one DB table per track is fine.

Design consequence: **grouping by artist falls out of the folder grammar even when tags are
missing**, and "group by folder" is just the raw directory tree — both views come from the same
ingest, no tag dependency for the skeleton.

## §2 Architecture decisions

### §2.1 Streaming path: token-gated direct file serving through the StreamGateway — NOT Jellyfin

The video pipeline is control-plane (site mints HMAC token) + data-plane (StreamGateway YARP →
Jellyfin). For music we keep the token/gateway shape but **cut Jellyfin out entirely**:

- Jellyfin would need a whole new library, scans are deliberately disabled, sync is manual, and
  the build is hand-patched — adding music to it buys nothing, since audio needs no HLS and no
  transcoding for ~99% of the collection.
- Browsers natively play `.mp3`, `.m4a`/AAC, `.flac`, `.ogg`, `.opus`, `.wav` via `<audio>` with
  plain HTTP Range. Direct file serving covers effectively the whole library **bit-perfect**,
  including FLAC.
- New gateway route: `/s/{token}/MusicFile`. **As built:** the catalog stores music-root-RELATIVE
  paths (forward slashes), the token (`MusicCapabilityToken`, Core) carries
  `userId|trackId|relativePath|expires`, and the gateway joins the relative path onto its own
  `MusicRootDir` config — so no path-prefix mapping table is needed at all (the earlier
  `MusicPathMappings` idea is retired). The gateway validates the signature, **confines the
  resolved path to its music root**, and serves the file with range support and the shared
  `MusicMimeTypes` Content-Type. No DB, no Jellyfin dependency — the gateway stays dumb.
- Site endpoint: `POST /API/Music/Stream/Start` (mirrors `StreamController.Start`): auth check,
  track lookup, token mint, returns `{ url, mimeType, durationSec }`. Returns 501 when
  `StreamGatewayBaseUrl`/`MusicLibraryDir` unconfigured (house convention: degrade, don't crash).
- **Transcode fallback lane is deferred to §Phase 7.** For genuinely unplayable formats
  (`.ape`, `.wv`, `.wma`, module files) the track is marked `RequiresTranscode` at ingest and the
  UI shows it as such until the ffmpeg lane exists. Sampling suggests this set is tiny or empty.

### §2.2 Database: new `Music*` tables, unique-index-as-upsert-key

New entity files in `MovieTheater.Db\`, EF migration, fluent config in `OnModelCreating`
following house conventions (explicit `OnDelete`, filtered/covering indexes only when measured):

- **`MusicArtist`** — `Id`, `Name`, `SortName`, `FolderPath` (unique), `YearRange`.
- **`MusicAlbum`** — `Id`, `ArtistId` FK (Restrict), `Title`, `Year`, `FolderPath` (unique),
  `Tag` (bracket tag e.g. `Collector's`), `HasArt`, `DominantColor`.
- **`MusicTrack`** — `Id`, `AlbumId` FK nullable (loose/compilation tracks), `ArtistId` FK,
  `FilePath` (unique — the upsert key), `FileName`, `Extension`, `SizeBytes`, `ModifiedUtc`,
  `Title`, `TrackNo`, `DiscNo`, `DurationSec`, `Codec`, `BitrateKbps`, `SampleRateHz`,
  `TagArtist`, `TagAlbum` (raw tag values kept separate from folder-derived identity),
  `HasEmbeddedArt`, `RequiresTranscode`, `MissingSinceUtc` (drift handling, mirrors MediaFile).
- **`MusicTrackLyrics`** — `TrackId` PK/FK (Cascade), `PlainText`, `SyncedLrc`, `Source`
  (`embedded|sidecar|lrclib`), `FetchedUtc`.
- **`MusicPlaylist`** — `Id`, `OwnerUsername`, `Name`, `CreatedUtc`.
  **`MusicPlaylistItem`** — `Id`, `PlaylistId` FK (Cascade), `TrackId` FK (Restrict),
  `Position`, unique `(PlaylistId, Position)`.

**Deliberate choice: music playlists are their own tables**, not rows in the existing
`Channel`/`PlaylistItem` model — that machinery assumes video playables, TV scheduling, and the
channel player. Music needs a queue semantic (order, repeat, shuffle) the channel model doesn't
have. UI patterns (dnd-kit reorder, picker modal, collage tiles) are reused; storage is not.

Folder-view grouping needs no extra table — it's `GROUP BY` on path segments under the root.

### §2.3 Ingest: `music-ingest` CLI, chunked + resumable + dry-run (hard rule)

Mirrors `ArcadeIngestCommand` house style exactly:

- Dry-run by default, `--apply` to write. `--limit N` (artist folders per run) +
  `--after <artist-folder>` cursor; prints `{ processed, remaining, nextCursor }` per run.
  Idempotent upsert on `MusicTrack.FilePath`; **never deletes** — a vanished file sets
  `MissingSinceUtc` (only with `--reconcile`).
- **NAS access is a bounded subtree walk of `L:\3 - Music`, one artist folder per unit of
  work** — allowed under the no-full-scan rule (specific subtree ≠ drive root), and the chunk
  cursor keeps any single run small. The inventory CSV can pre-seed expectations but tag reading
  requires opening files regardless, so the walk is the source of truth here.
- Tag reading via **ATL (`z440.atl.core`)** or TagLib# (pick at implementation time; ATL is
  actively maintained, pure managed, reads duration/bitrate for everything we have). Fallback
  when tags are absent: parse the folder grammar (§1) + `NN - Title.ext` filename.
- Identity rule: **folder grammar wins for Artist/Album identity** (it's Eric's curation, same
  authority as the movie library); tags fill `Title`/`TrackNo`/duration and are preserved raw in
  `TagArtist`/`TagAlbum` for later reconciliation. Never rename files (hard rule).

### §2.4 API surface: paged REST under `/API/Music/*`, arcade-style

OData is effectively dead in the SPA; the arcade's paged/faceted REST is the model.

- `GET /API/Music/Browse?view=artists|albums|folders&q=&format=&decade=&page=&pageSize=60`
  — slim card DTOs, server-side paging, A–Z bucket offsets for the jump pager.
- `GET /API/Music/Artist/{id}` — albums + loose tracks. `GET /API/Music/Album/{id}` — tracklist.
- `GET /API/Music/Search?q=` — one box searching artist + album + track title (`LIKE`-based to
  start; the collection is small enough).
- `GET /API/Music/Folders?path=` — raw folder tree view (path-prefix query on `FilePath`).
- Playlist CRUD: `POST /API/Music/Playlist/{Create,{id}/AddItems,{id}/SetItems,{id}/Rename,{id}/Delete}`,
  `GET /API/Music/Playlist/Mine`, `GET /API/Music/Playlist/{id}/Items` — same verbs as the
  channel playlist API so the frontend layer is familiar.
- `GET /API/Music/Track/{id}/Lyrics`.
- All under `[Authorize(Policy = "StreamingUser")]` like the other streaming verticals.
  No age gating — it's a music collection.

### §2.5 Album art: extract-first, fetch-fallback

- At ingest (or a separate `music-art` pass): prefer folder sidecar (`cover.jpg`/`folder.jpg`/any
  jpg in the album folder), else embedded tag art. Written to the posters mount as
  `music_{albumId}.png` + `_s` thumbnail via the existing `ImageShrinkService`; dominant color
  computed with the existing `ComputeAverageColor` and stored on `MusicAlbum` — it feeds player
  theming and visualization palettes.
- New `/MusicImage/{albumId}` + `/MusicImageThumb/{albumId}` routes on the poster controller
  pattern (memory-cache, ETag, `?v=` immutable). **Must be added to the Vite dev proxy list**
  (known bite from `/ArcadeImage`).
- Lazy remote fallback for art-less albums (ArcadeImageController pattern): **Cover Art
  Archive/MusicBrainz** (free, keyless) → iTunes Search API (free) → negative-cache misses.

### §2.6 Frontend: the site's first persistent audio player

The genuinely new architecture. Every existing player dies on route change; music must not.

- **`<MusicPlayerHost>` mounted in `App.js` above the `<Switch>`** (precedent: the app-root
  `PatchedArtifactAlarm`), owning the single `<audio crossOrigin="anonymous">` element for the
  app's lifetime. Introduce the codebase's **first React context, `MusicPlayerContext`**:
  `{ queue, index, track, playing, position, play(tracks, startAt), enqueue, next, prev,
  seek, toggle, shuffle, repeat }`. Everything else in the app stays props-and-URL; the
  context exists because playback must outlive routes.
- **Mini-player bar** rendered by the host whenever a track is loaded — fixed bottom bar
  (art thumb, title/artist, transport, seek, volume, queue button), visible on every page of
  the site, not just `/music`. Clicking it routes to the full player view.
- Full player at `/music/now-playing`: big art, queue list (dnd-kit reorder), lyrics pane,
  visualizer toggle.
- `useMediaSession` is reused as-is (it only touches standard HTMLMediaElement APIs) → OS/lock
  screen metadata + transport keys work day one. Volume persists to `localStorage`
  (`music.volume`, following the arcade namespace convention).
- Library UI mirrors the arcade lobby: `data-feature="music"` theme tokens in `theme.css`, a
  `MusicNavContent` rail (search box + view toggle artists/albums/folders + format/decade
  facets), windowed grid via `useGridWindow` + `useInfiniteScroll`, A–Z pager, album modal on
  the `GameModal` shell (hero art → tracklist → pinned Play/Queue bar), NavBar switcher entry
  with icon + hue dot, gated on `hasPassword` like the other streaming sections.
- Track pressing "play" on an album/playlist replaces the queue; "add to queue" appends. Queue
  survives navigation but not reload (v1; persistence is a polish item).

### §2.7 Lyrics: three sources, one table

1. **Embedded tags** (USLT/unsynced, SYLT/synced) — read during ingest, free.
2. **Sidecar `.lrc`** files sharing the track basename — read during ingest.
3. **LRCLIB** (`lrclib.net`) — free, keyless API with synced-LRC coverage; a chunked
   `music-lyrics` CLI enriches remaining tracks by (artist, title, album, duration) match,
   house bulk-job rules apply (bounded, resumable, resumes by TrackId cursor, never overwrites
   an `embedded`/`sidecar` row).

Player: lyrics pane parses LRC timestamps and highlights/scrolls the current line against
`audio.currentTime` (same timed-cue idea as the subtitle pipeline, far simpler — no wasm).
Unsynced lyrics render as static scrollable text.

### §2.8 Visualization: Butterchurn (actual Milkdrop 2, in WebGL)

- **`butterchurn` + `butterchurn-presets`** (npm) — the real Milkdrop 2 preset engine ported to
  WebGL. Wire `MediaElementAudioSourceNode(audio) → AnalyserNode → butterchurn visualizer`,
  render loop on rAF, preset cycling + random + named picker.
- Gotchas encoded now:
  - Creating a `MediaElementAudioSourceNode` permanently reroutes the element's audio through
    the graph — create it **lazily on first visualizer open**, then keep the graph (source →
    analyser → `audioContext.destination`) for the element's lifetime.
  - `AudioContext` must be resumed from a user gesture.
  - Element already needs `crossOrigin="anonymous"` + CORS-clean gateway responses (the gateway
    already exposes the right headers for video; verify for the audio route).
  - Lazy-load the library only when the visualizer opens (it's heavy; keep it out of the main
    chunk).
- Modes: pane in the full player, and a fullscreen mode. Album-art dominant color can seed an
  idle/fallback canvas for browsers without WebGL2.

## §3 Config additions (flat keys on `MovieTheaterConfiguration` + `appsettings.default.json`)

- `MusicLibraryDir` — `L:\3 - Music` (site-side view, used by ingest run locally).
- `MusicImagesDir` — album art mount (or reuse `MoviePostersDir` with the `music_` bucket).
- `MusicTranscodeEnabled` — hand the player the gateway's ffmpeg route for `RequiresTranscode`
  formats instead of a 409 (§Phase 7). Off by default; only useful when the gateway has ffmpeg.
- Gateway config: `MusicRootDir` — its own mount of the music share; also the confinement root.
  (No path-mapping table: the catalog is root-relative — see §2.1.)
- Gateway config: `FfmpegPath` — absolute path to ffmpeg. Unset ⇒ the transcode route 404s, which
  is the intended "feature off" state. `MusicMaxConcurrentTranscodes` (default 2) caps concurrency.

### §3.1 Who may stream, and as whom the gateway runs

**The site half — password-only.** `MusicController` carries a class-level
`[Authorize(Policy = "StreamingUser")]` = authenticated **AND** the `amr=pwd` claim, so every
`/API/Music/*` route — `Stream/Start` included — is closed to the passwordless communal login. That
is the real enforcement; the frontend only mirrors it (`userData.hasPassword` gates the nav entry,
both `/music` routes, the persistent player and its queue restore) so nobody is offered a player
whose first request would 401.

**⚠ The gateway half — the service ACCOUNT is load-bearing.** `MusicRootDir` must be a **UNC** path
(`\\Library\Public\3 - Music`), because a service can't see per-user drive mappings — but UNC alone
is not enough. Running the gateway as **LocalSystem cannot read that share at all**: `Get-Acl` on
`1 - Movies` and `3 - Music` returns the same six NAS-local SIDs on both, with **no machine account
and no Authenticated Users**; the only broad entry is Guest, and Windows blocks insecure guest
logons by default. Every track then 404s — and note the symptom is a **404, not a 403**, because
`File.Exists` returns false for access-denied too (403 means a bad signature; 404 means it couldn't
*see* the file). A restart does not help; this is not boot-order.
Run the service as an account that holds real NAS credentials — the house convention on that host,
where every arcade task already runs as a user rather than LocalSystem.
*Diagnostic that isolates it in one move, no elevation:* run the SAME exe against the SAME
`appsettings.Production.json` as the interactive user on a spare port
(`--Kestrel:Endpoints:Http:Url=http://localhost:5199` — plain `--urls` is ignored, the endpoint is
pinned in config) and hit both with one token. Interactive 206 vs service 404 rules out the secret,
the binary, the config and the path in a single comparison.

### CLI runbook (both are bounded + resumable; the caller loops until `remaining: 0`)

Run these **from `src/MovieTheater`** — the dev `MoviePostersDir` is CWD-relative (`Posters`), so a
run started at the repo root writes art into the wrong folder.

```
cd src/MovieTheater   # ASPNETCORE_ENVIRONMENT=Development

# album art — local extraction (reads the library, writes only the images mount + art columns)
dotnet run -- music-art --apply --limit 150 --after <nextCursor>

# album art — remote fallback for albums still artless (throttled ≥1s/call, negative-caches misses)
dotnet run -- music-art --remote --apply --limit 100 --after <nextCursor>

# lyrics — LRCLIB enrichment (~2 req/s; the full catalog is an overnight run)
dotnet run -- music-lyrics --apply --limit 500 --after <nextCursor>
```

## §4 Phases

Each phase lands independently and is verified before the next starts. Status lines get updated
here as phases deploy (ABR-plan convention).

### Phase 0 — Schema + ingest (no UI) — ✅ DONE 2026-08-04
Entities §2.2 (`AddMusicTables` migration, applied), `music-ingest` CLI §2.3. Verified: dry-run
parses artists/albums/compilations correctly; `--apply` re-run → 0 inserted / all unchanged
(idempotent); full library driven to completion in 34 chunked runs (20,267 tracks, 0 tag errors).

### Phase 1 — Streaming path — ✅ DONE 2026-08-04 (local gateway; prod deploy pending)
Gateway `MusicFile` route, `MusicCapabilityToken` + `MusicMimeTypes` in Core (18 unit tests),
`POST /API/Music/Stream/Start` + catalog endpoints on `MusicController`. Verified against a local
gateway instance: Range → 206 with correct Content-Type, full GET → 200, tampered token → 403,
signed-but-escaping traversal path → 403.

### Phase 2 — Library UI + persistent player — ✅ DONE 2026-08-04 (E2E-verified: headless-Chrome
pass on the full local stack — playback via the gateway, cross-page persistence, transport/queue/
seek, both themes, mobile; 418 backend + 221 frontend tests green)
As built: `/music` page (albums grid + artists view with drill-in + loose tracks + server song
search), `MusicAlbumModal`, `MusicPlayerProvider` (the site's first context) + app-root `<audio>`
+ `MusicMiniPlayer` bottom bar with queue flyout, `MusicNavContent` rail, theme tokens, NavBar
switcher entry, MediaSession via the shared hook. Catalog strategy: artists+albums load whole and
filter client-side (BoardGames pattern) instead of the arcade windowed grid — right size for 1.3k
albums; revisit if the library grows. Folder-view-as-tree deferred (artist folders ARE the tree's
first level; albums the second).

### Phase 3 — Playlists — ✅ DONE 2026-08-04
Seven verbs on `MusicController` mirroring the channel playlist API — `Playlist/Create`,
`Playlist/Mine`, `{id}/Items`, `{id}/AddItems`, `{id}/SetItems`, `{id}/Rename`, `{id}/Delete` — all
owner-scoped through one `LoadOwnedPlaylistAsync` guard, positions rewritten dense 0..n-1 by
SetItems, and non-existent track ids dropped rather than written as dangling FKs. Frontend:
`MusicPlaylistPickerModal` (＋ on the album modal, every song row, and each queue-flyout row, plus
"Save queue as playlist"), `MusicPlaylistManageModal` (dnd-kit drag-reorder + remove + rename +
delete), and a Playlists shelf on `/music` with ▶ Play → `player.playTracks`.
**Verified:** 14/14 API checks (order preserved on create, AddItems appends, SetItems
reorders+removes, bogus ids dropped, blank rename → 400, tile carries count+lead titles, all five
mutating verbs 404 on a playlist you don't own, delete removes it) plus a browser round-trip
(create from an album → keyboard drag-reorder → save → reopen shows the new order → play).
*Isolation caveat:* the guard is a single shared code path (`p.UserId == userId`) and every verb
404s a foreign id, but a genuine two-account browser test wasn't run — only one password-verified
test account exists locally.

### Phase 4 — Album art + polish — ✅ DONE 2026-08-04
`music-art` CLI (chunked per album id, dry-run default, `--limit`/`--after`, prints
`{processed, remaining, nextCursor}`; **never overwrites an existing art file** — an album whose
file is already on the mount is only re-flagged `HasArt`). Local pass prefers a folder image
(cover/folder/front, else the largest image in the album folder), else the embedded picture from
the album's first `HasEmbeddedArt` track. Art is written as `music_{albumId}.png` (≤600px) +
`music_{albumId}_s.png` (≤300px) into `MusicImagesDir ?? MoviePostersDir`, with the thumbnail's
per-pixel mean stored as `MusicAlbum.DominantColor`. `--remote` is a separate pass over albums
still artless: MusicBrainz release search (User-Agent + ≥1s throttle) → Cover Art Archive
`front-500`, falling back to the iTunes Search API, stamping the new `MusicAlbum.ArtCheckedUtc`
negative cache hit or miss. Serving: `/MusicImage/{albumId}` + `/MusicImageThumb/{albumId}`
(memory-cache + ETag + `?v=` immutable), both added to the Vite dev proxy.
*Deviation:* thumbnails are made with an aspect-preserving ImageSharp resize rather than
`ImageShrinkService` — that service is hard-coded to the movie-poster shape (200px tall, ≤150px
wide) and would squash square album art into a portrait rectangle.
**Verified:** the local pass ran the FULL catalog in 9 chunked runs → **623/1,347 albums (46.3%)**
(≈70% folder images, ≈30% embedded tags, 5 undecodable files skipped); a bounded 25-album
`--remote` run hit **25/25** → **648/1,347 (48.1%)**. Browser: 67 art tiles among the first 120
cards, hero art in the album modal, album thumb in the mini-player, initials tile as the fallback.

### Phase 5 — Lyrics — ✅ DONE 2026-08-04
`GET /API/Music/Track/{id}/Lyrics` → `{plainText, syncedLrc, source}` or 404. `music-lyrics` CLI
queries LRCLIB by (artist, title, album, duration) with a descriptive User-Agent at ~2 req/s;
it only ever ADDS a row, so embedded/sidecar lyrics can never be overwritten, and every attempt
stamps the new `MusicTrack.LyricsCheckedUtc` negative cache. Frontend: `/music/now-playing` (linked
from the mini-player's track info) with big art, queue, transport, and a lyrics pane that parses
LRC cues (`src/Music/lrc.js`, 9 unit tests) and highlights + auto-scrolls the current line against
`audio.currentTime` — read off the element, never through context state.
**Verified:** a bounded 200-track run → 131 synced + 27 plain + 42 misses (79% hit rate),
coverage 203/20,267; in-browser the current line highlights and the pane auto-scrolls at 1:12 of a
synced track; unsynced rows render as static text; no lyrics → an explicit empty state.

### Phase 6 — Visualization — ✅ DONE 2026-08-04
Butterchurn + butterchurn-presets, dynamically imported so they land in their own chunks
(197 kB + 646 kB, both out of the main bundle). The Web Audio graph lives in `MusicPlayerContext`
as a ref (`ensureAudioGraph`): one `MediaElementAudioSourceNode` for the element's lifetime,
connected to BOTH an analyser and the destination, resumed from the toggle's click handler.
Visualizer pane: fullscreen button, random preset every 30s plus prev/next and a named picker, and
a friendly message when WebGL2/Web Audio is unavailable. *Gotcha found here:* both packages are
UMD, and the default export surfaces at `module`, `module.default`, or `module.default.default`
depending on dev-vs-build — resolve by probing for the member you need (`unwrapModule`), not by
guessing the interop shape.
**Verified (headless Chrome):** two canvas screenshots 1.2s apart differ; audio advances
5.41s → 11.46s across opening the visualizer and never pauses; closing it leaves playback running.
Firefox was not exercised (no local Firefox harness).

**⚠ Render surface — corrected 2026-08-05 (this shipped blurry).** Sizing the canvas "to its
container via ResizeObserver" is NOT enough, and was the bug: butterchurn never touches the canvas
element's `width`/`height` attributes, and its `renderToScreen` sets the GL viewport to the
width/height it was handed **in raw drawing-buffer pixels**. The backing store therefore stayed at
the HTML default **300×150** while the viewport was the CSS box, so GL clipped the viewport to the
buffer — the page showed the bottom-left corner of the frame, CSS-stretched across the box, and
fullscreen made it ~6× worse because the gap grows with the box.
The rule now: `canvas.width/height` and the numbers passed to butterchurn are the SAME device
pixels, computed by one exported `surfaceSize()` (7 regression tests), with **`pixelRatio: 1`** —
butterchurn multiplies *texsize* by `pixelRatio` but NOT the screen viewport, so any other value
desyncs the two again. Render resolution is `cssBox × devicePixelRatio` capped at ~3.7M pixels
(2560×1440) so a 4K fullscreen doesn't stall the warp mesh + three blur passes. Re-applied on
ResizeObserver, `fullscreenchange`, and window resize (the last catches a DPI change when the
window moves between monitors, which leaves the CSS box identical).
*Also fixed:* the boot effect depended on the whole context value, so the entire WebGL visualizer
was torn down and rebuilt **on every play/pause**; it now keys off the stable `ensureAudioGraph`.

**The visualizer switch lives in `MusicPlayerContext`, not on the page.** The play bar is on every
route and owns the prominent toggle, so both surfaces must read one switch or they disagree the
moment you navigate. Exactly ONE of them mounts the component — Now Playing inline in its art slot,
the bar as a fixed overlay everywhere else (it measures its own height for the overlay's offset).
Two butterchurn instances on one source would be a second GL context for no gain.

**⚠ The preset library — rebuilt 2026-08-06. Presets are STATIC ASSETS now, not a bundled module.**
`import("butterchurn-presets")` pulled a **646 kB** webpack bundle to get the **100-preset base
pack**, and that was all the presets the site had. The same npm package also ships
`presets/converted/` — **1,754 preset JSON files**, byte-identical to what the bundles hand back
(verified by comparing `getPresets()[name]` against the file). So the build publishes those instead:

- `scripts/build-butterchurn-presets.mjs` (a `prestart`/`prebuild` hook, same pattern as
  `copy-libass.mjs`) writes `public/butterchurn/presets/<slug>.json` + an `index.json` catalogue.
  **gitignored — derived from `node_modules`, never committed.** Docker gets it for free: the UI
  image runs `npm install` before `npm run build`, so the hook regenerates inside the image.
- The app fetches the ~176 kB index once per visualizer open and then **one ~5 kB preset at a time**
  (`src/Music/butterchurnPresets.js`: cache + in-flight dedupe + LRU cap + prefetch of the cycle's
  next pick). Net: **17× the presets, and the 646 kB chunk is gone.**
- **Tiers come from pack membership**, which is the upstream author's own curation: base pack =
  *Featured* (99), + Extra/Extra2/MD1 = *Classic* (394), + the rest of the corpus = *Everything*
  (1,754). The 30-second auto-cycle draws from **Classic by default** — a random pick out of the
  whole archive lands on a dud often enough to notice — while the picker still offers everything.
  Favorites (localStorage) are a fourth pool and ignore tier entirely.
- The `<select>` picker became a **searchable panel** (it lives *inside* `.music-viz`, so it comes
  along into fullscreen), plus prev/next/random, a hold-this-preset toggle, per-preset stars, and
  keyboard `← → R F L B //Esc`. Rendered rows are capped at 300; 1,750 `<div>`s is scroll jank.

*Gotchas paid for here:*
- **`warp` and `comp` are empty strings on every Milkdrop-1-era preset** (no custom shader —
  butterchurn substitutes its default), and `pixel_eqs_str` is empty whenever there are no per-pixel
  equations. A truthiness check in the publish step silently threw away **693 good presets**,
  including 18 from the base pack. Test for *presence and type*, never truthiness.
- **Slug collisions need a global set, not a per-base counter.** `_Geiss - Confetti (Kaleidoscope
  Mix)` collides with `Geiss - Confetti (Kaleidoscope Mix)` and gets the suffixed `…-mix-2`, which
  is *also* the natural slug of `_Geiss - Confetti (Kaleidoscope Mix) 2` — one file overwrote the
  other and two index rows pointed at one preset. The build now fails loudly on a duplicate slug.
- **A missing preset returns HTTP 200 + `index.html`** (SPA history fallback — the same trap as the
  prod art-upload run). Every fetch is shape-checked before it reaches `loadPreset`, or a bad object
  surfaces much later as "the visualizer broke".
- Preset names are Winamp-era filenames (`$$$ Royal - …`, `!!!---flexi + …`, brackets, commas), so
  the wire name is an ASCII slug and the display name lives in the index.

**Quality gate:** `scripts/verify-butterchurn-presets.mjs` renders **every** preset in headless
Chromium (SwiftShader — this asks whether shaders *compile* and equations *run*, a correctness
question, and says nothing about frame rate) and writes `scripts/butterchurn-denylist.json`, which
the publish step subtracts. It is chunked + resumable (`--limit`, results file as the cursor) and
deliberately **conservative: a preset is condemned only if it throws or kills the GL context, never
for looking dark** — it's driven by synthetic noise, not music, so a black frame is normal.
Re-run it if `butterchurn-presets` is ever upgraded.

### Phase 7 — Long tail — ✅ PARTIAL 2026-08-04
**Queue persistence — done.** `{queue, index}` is written to `music.queue` (capped at 500 entries)
on every change and restored on provider mount **paused**; a one-shot ref suppresses autoplay for
that first restored track. Verified: after a reload the bar is present, `audio.paused === true`,
and the stored key is intact.

**Transcode lane — built, not exercised end-to-end (no ffmpeg on this machine).** Gateway route
`/s/{token}/MusicTranscode` spawns `ffmpeg -i <file> -map a:0 -f mp3 -b:a 192k pipe:1` and streams
stdout as `audio/mpeg` with `Accept-Ranges: none`, capped by a semaphore
(`MusicMaxConcurrentTranscodes`, default 2 → 503 + Retry-After over the cap) and gated on the
gateway's `FfmpegPath`; the site hands out that route instead of a 409 when
`MusicTranscodeEnabled` is on. **The capability token is unchanged (still 4 fields)** — the ROUTE
decides the treatment, so there is no version skew between a deployed site and an older gateway,
and `MusicCapabilityTokenTests` needed no edits. Verified locally: with `FfmpegPath` unset the
transcode route 404s while `MusicFile` still 403s a bogus token (i.e. the route is genuinely off,
not merely failing). The actual ffmpeg pipe is UNVERIFIED — install ffmpeg on the gateway host,
set `FfmpegPath`, and play one of the 13 `.wma` tracks to close this out.

**Gapless playback — investigated, deliberately NOT built.** Two viable approaches. (a) *Double
`<audio>` buffer*: keep a second hidden element, mint the next track's URL ~15s before the end,
`preload="auto"` it, then swap which element is the "current" one on `ended`. Cheap, but it can't
remove the decoder gap — MP3/AAC frames carry encoder padding that the element trims, so back-to-
back album tracks still click. It also doubles the persistent-element problem: the visualizer's
`MediaElementAudioSourceNode` is bound to ONE element, so a swap needs two graphs (or a
`GainNode` crossfade between them) or the visualizer goes silent on every track change. (b) *Web
Audio scheduling*: fetch each track whole, `decodeAudioData`, and `start(when)` the next buffer at
the exact end time of the current one — genuinely gapless, and it composes with the analyser we
already build. The costs are real: whole-file downloads (a FLAC album track is 30–40 MB) instead of
Range streaming, no browser-native seek, and the transport/seek/MediaSession wiring all has to be
rebuilt on top of `AudioBufferSourceNode` since there is no longer an `<audio>` element driving it.
Recommendation: only worth doing if gapless albums become a stated requirement, and then via (b)
for the whole `/music` player rather than bolting it beside the existing element.

**Not built (unchanged from the original plan):** shuffle-by-album, play counts /
recently-played rail.

## §5 Open questions (for Eric)

1. **Access**: gate on `hasPassword` like other streaming sections (assumed), or open to all
   passwordless users?
2. **Shared listening** ("listen party", watch-party analogue): out of scope for now — worth a
   future phase?
3. Playlist sharing between users (v1 is private-per-user).
4. Should ratings/favorites exist for tracks/albums (the site rates movies; not planned here)?
