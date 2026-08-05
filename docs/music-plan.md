# Music Vertical Plan

**Status: Phases 0–7 BUILT 2026-08-04 (local verification passing; not yet deployed).**
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
Visualizer pane: canvas sized to its container via `ResizeObserver`, fullscreen button, random
preset every 30s plus prev/next and a named picker, and a friendly message when WebGL2/Web Audio
is unavailable. *Gotcha found here:* both packages are UMD, and the default export surfaces at
`module`, `module.default`, or `module.default.default` depending on dev-vs-build — resolve by
probing for the member you need (`unwrapModule`), not by guessing the interop shape.
**Verified (headless Chrome):** two canvas screenshots 1.2s apart differ; audio advances
5.41s → 11.46s across opening the visualizer and never pauses; closing it leaves playback running.
Firefox was not exercised (no local Firefox harness).

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
