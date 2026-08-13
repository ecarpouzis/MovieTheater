# Family Photo Album Plan

**Status: BUILT — all eight phases (0–5, the Phase 6 Google mesh, and the Phase 7 Gallery shelf) are
implemented and green in the working tree, written 2026-08-12 from a shallow survey of the collection
root, the music/streaming verticals as precedent, and current research on Immich and the Google Photos
API. Phases 0–6 are LIVE in production; Phase 7 is not, and must not deploy before its migration is
applied.**

**Awaiting, in this order:** the commit; a StreamGateway redeploy on its host (hand-deployed — `git
push` does nothing to it); `scripts/photos-phase7-migration.sql` applied by the owner **before Phase 7
deploys** (three additive columns and one filtered index — see the Phase 7 addendum), and
`scripts/photos-phase6-migration.sql` if it is still outstanding (three additive
nullable columns, the first schema change since Phase 3 — see the Phase 6 addendum); the first real
ingest run against the collection, human-supervised; the Immich sidecar deployment
(`docs/photos-immich-setup.md`); and the dedicated Jellyfin family library plus its scan-then-sync.
Nothing in this vertical has yet been run against the NAS, the live database, or a real Takeout
archive.

Goal: a `/photos` section of the site — a Family Photo Album over `L:\7 - Photos & Video` —
visible only to family-flagged users. Timeline, folder, album, and person views; people tagging
with locally-computed suggestions; near-duplicate grouping with a master-copy pick; videos
playable in place via Jellyfin (never surfaced in the movie site); a Google Photos mesh lane.

Code comments should cite sections of this doc (`photos-plan.md §2.4`) the way the
music/arcade/streaming plans are cited.

**The prime directive (stronger here than anywhere else on the site): the pipeline NEVER writes,
renames, moves, or deletes anything under `L:\7`.** Every operation described below — dedupe,
master-pick, hiding, merging, curation — is a row or a flag in the MovieTheater DB. All file
access is read-only (`FileShare.Read`), and the only NAS write that will ever be proposed
(downloading Google-only items into a new folder) is a separate, explicitly-approved, additive
step that can never overwrite an existing file.

---

## §1 The collection (surveyed 2026-08-12, top level only)

A shallow, non-recursive listing of `L:\7 - Photos & Video` (deep counts come from Phase 1
ingest, which reports them chunk by chunk — no full walk was done for this plan):

- **~31 top-level folders + loose image files at the root.** The tree is heterogeneous:
  - **Event/place folders** — `Vacation`, `Holidays`, `Wedding`, concert and outing folders.
    These map naturally to albums.
  - **Device dumps** — phone-model backup folders, `Phone Temp Storage`, a relative's camera
    folder. No event structure; only EXIF dates give these shape.
  - **`Phone Backups (Merge Needed)`** — the duplicate problem, already named on disk.
  - **`Album Scans`** — scanned historical photos: little/no EXIF, multiple quality variants of
    the same print, unknown dates. This folder is why §2.6 (near-dupes) and §2.7 (date
    estimation) exist.
  - **An existing Google Photos copy-down folder** — prior manual exports live here; the §2.10
    mesh must treat it as already-local content, not re-download it.
  - **`Screenshots`, `Misc Photo`, `Misc Pics`, papercraft/reference folders** — clutter that
    wants a "hidden from timeline by default" curation flag, not deletion.
- Videos are mixed in among the photos (plus dedicated video folders inside event trees).

Design consequences: **folder-as-album is a starting hint, not the model** (device dumps and
misc piles aren't albums); the **timeline is the primary browse surface** and needs a home for
date-unknown items; **curation flags (hide/screenshot/junk) are first-class** because the tree
was never groomed for presentation.

## §2 Architecture decisions

### §2.1 Access: a family flag, enforced server-side everywhere

- `UserSettings` row per family user: `SettingKey = "FamilyAlbum"`, `SettingValue = "true"`.
  Grantable only from the admin user-settings surface. No age/rating logic — this is a hard
  member/non-member gate, and admins are NOT implicitly members.
- ASP.NET authorization policy (`RequireFamilyAlbum`) applied to the entire `PhotosController`
  and to token minting. The React nav hides `/photos` for non-members, but the UI is never the
  gate — every API endpoint, image token, and stream start re-checks the policy.
- **Photo bytes are gated, not just metadata.** Movie posters are served openly from `/Image`;
  family photos must not be. All pixels flow through short-lived HMAC tokens (§2.2) minted only
  for family-flagged sessions. No photo route joins OData.

### §2.2 Data plane: StreamGateway routes + a pre-generated thumbnail cache (music pattern)

Prod pods have no path to the NAS — movies go through Jellyfin, music through the StreamGateway.
Photos follow the music shape (`music-plan.md §2.1`): the site is the control plane minting HMAC
capability tokens; the gateway on the NAS-adjacent host is the dumb data plane.

- New gateway routes: `/s/{token}/PhotoThumb` and `/s/{token}/PhotoOriginal`. Token carries
  `userId|assetId|relativePath|size|expires`; gateway validates the signature, confines the
  resolved path to its configured roots, serves with Range support. No DB on the gateway.
- **Thumbnails are pre-generated by the ingest CLI into a gateway-host cache directory**
  (`PhotoThumbCacheDir`), keyed by asset id + content hash: `grid` (~400px WebP) and `view`
  (~1600px WebP). The lightbox serves `view` by default; `PhotoOriginal` (the untouched NAS
  file) is an explicit "full quality / download" action. The gateway never generates thumbs —
  it stays dumb; a missing thumb is a visible ingest gap, not a lazy path.
  - Thumb generation applies the EXIF orientation flag (a naive resize ships sideways photos).
  - **Deep zoom / browser-renderability rule**: browsers render JPEG/PNG/WebP/GIF originals but
    NOT HEIC/TIFF/RAW. For renderable formats, lightbox deep-zoom uses `PhotoOriginal`; for the
    rest the thumb pass emits a third `zoom` derivative (~3200px WebP) and `PhotoOriginal`
    remains download-only. One column (`OriginalRenderable`) decides at mint time.
  - Budget note: two WebPs ≈ 150–300 KB/photo → tens of GB at 100k photos. The cache dir is
    config-placed on a local disk with room; it is derived data, deletable and rebuildable.
- Gateway deploys are manual on its host (`git push` does nothing to it) — same operational
  rule as music; the deploy checklist in §5 Phase 1 says so explicitly.

### §2.3 Videos: Jellyfin streams them; the movie site never sees them

- A **new, dedicated Jellyfin library** (homevideos type, like everything else) whose folders
  are the video-bearing subtrees of `L:\7`. Scheduled scans stay disabled; the existing
  scan-then-sync workflow applies.
- A `photos-sync-jellyfin` pass stamps `JellyfinItemId` onto `PhotoAsset` rows (matched by
  path, exactly like the movie sync). **The movie-side `sync-jellyfin` must exclude the family
  library** (by library id and by `L:\7` path prefix) so family videos can never leak into
  Movie/MiscVideo/review surfaces — that exclusion ships BEFORE the Jellyfin library is created.
- Playback reuses the existing HLS/player stack via a family-gated stream-start endpoint
  (policy from §2.1 checked before any Jellyfin URL is minted). Video poster frames are
  ffprobe/ffmpeg grabs written into the same thumb cache.
- ⚠ Known Jellyfin trap: **reserved extras folder names** (`Scenes`, `Shorts`, `Trailers`,
  `Interviews`, …) are special-cased in Jellyfin's core folder walk and their contents get
  dropped. The family tree may legitimately contain such names. Ingest audits for them and
  surfaces a report; we do NOT rename NAS folders — affected folders either get their own
  library entry path or are accepted as Jellyfin-invisible (still browsable as photos).

### §2.4 ML enrichment: Immich as a headless, disposable sidecar — our DB owns all truth

The requirement: estimate who's in each photo, where, and roughly when — **without cloud
inference**. Immich fits, used the way Jellyfin is used: headless plumbing behind the site,
never a user surface.

- **Deployment**: Immich docker-compose on the gateway host, LAN-only, never internet-exposed.
  The library is mounted **read-only via a CIFS volume using the read-only NAS credential**
  (the jellyfin-ro pattern) — Immich is then physically incapable of modifying originals,
  regardless of any UI mishap. Configured as an Immich **external library** over the mount
  (external libraries index in place; import never copies or moves files). One single Immich
  user owns the library (multi-user sharing makes Immich run ML per-user — wasteful).
- **What runs locally in Immich**: EXIF extraction, reverse geocoding (bundled offline geodata
  → city/state labels from GPS), face detection + recognition (clusters), CLIP smart-search
  embeddings, and its duplicate-candidate job. All on-box; CPU is fine to start, GPU optional.
- **`photos-sync-immich` (CLI, chunked)** pulls via the Immich API, keyed by `originalPath` ↔
  our `PhotoAsset.Path`, and writes **suggestions, never truth**:
  - face clusters → `PhotoPersonTag` rows with `Source = Suggested` + `ImmichPersonId` mapping
    onto our `FamilyPerson` table once a cluster is named;
  - GPS reverse-geocode → `LocationLabel` (source-stamped);
  - duplicate candidates → §2.6 group candidates (CLIP catches crops/recolors that pHash misses).
- **Immich is disposable.** Every feature degrades to manual: hand-tagging (§2.8) works
  identically with Immich absent, and the Immich DB can be dropped and rebuilt without losing
  anything (our DB never references it except by re-derivable mapping ids). Known risk,
  eyes-open: face recognition on external libraries has a history of flaky issues upstream —
  another reason it feeds a suggestion queue rather than writing tags.
- Operational posture (the patched-artifacts culture applies): **pin the Immich version** and
  upgrade deliberately — Immich moves fast and has broken external-library flows before. Budget
  for its own thumbnail/preview store on the host (it generates previews for everything it
  indexes; that disk is derived data, same as our thumb cache). Face-crop images for the
  suggestion UI are fetched server-side from the Immich API (never exposing Immich itself) and
  cached into our thumb cache like any other derivative.
- Alternatives weighed: **PhotoPrism** (similar read-only posture, weaker people/API surface),
  **roll-our-own ONNX** (InsightFace + CLIP in-process — full sovereignty, significant work;
  kept as the fallback if Immich misbehaves). Hand-tagging alone remains fully supported.

### §2.5 Ingest: `photos-ingest`, a chunked, resumable, read-only pipeline

Per the standing bulk-job rule: bounded work per call, `{processed, remaining, nextCursor}`
after every chunk, idempotent resume, the driver loop lives in the caller. Scanning `L:\7` (a
specific subtree) is permitted; the full-drive scan prohibition stands.

**Identity rule (load-bearing): content is identity, path is location.** Years of tags, dates,
album entries, and master picks will hang off `PhotoAsset` rows — they must survive a folder
reorganization on the NAS. `Path` is unique but mutable; `Sha256` (with size) is the durable
identity. When the inventory walk finds missing paths AND new paths in the same run, it re-pairs
them by content (`Sha256`, falling back to filename+size before hashes exist) and **re-points
`Path` on the existing row, preserving the id and everything attached to it** — the same
move-awareness the Jellyfin sync and `detect-fs-drift` earned the hard way. Ambiguous pairings
(same content at N old and M new paths) go to a review list, never auto-applied. A row is only
ever born when no missing row matches its content.

Phases, each its own resumable queue:

1. **Inventory walk** — bounded number of directories per call; cursor = last completed
   directory relative path in ordinal order, **and the page query orders by that same column**
   (the cheats-import cursor bug rule: cursor ordering must equal query ordering; sanity-check
   "done" against an independent count). Upserts `PhotoAsset` skeleton rows (path, size, mtime,
   kind by extension). Unchanged (path+size+mtime) rows short-circuit. Vanished paths get
   `MissingSinceUtc` stamped — never row deletion (drift handling mirrors `MediaFile`).
   **Re-running the walk IS the new-photo discovery mechanism** — cheap listing compare; a
   "Find new photos" admin button chains walk → metadata → thumbs for the delta.
2. **Metadata pass** — queue = rows missing metadata. EXIF via MetadataExtractor (HEIC/TIFF via
   Magick.NET where needed); videos via ffprobe. **Persist the RAW EXIF/ffprobe JSON** (the
   persist-hard-won-measurements rule: NAS reads are expensive; derived scalars are recomputed,
   raw readouts are kept), plus parsed columns: dimensions, `TakenAt` + source (§2.7), GPS,
   camera make/model.
3. **Hash pass** — SHA256 (exact identity, Google mesh) + perceptual hashes (dHash + pHash via
   ImageSharp; video: pHash of a mid-point frame). Separate queue since it re-reads bytes.
4. **Thumb pass** — queue = rows missing cache entries; writes WebPs to the cache dir (§2.2).
5. **Dupe-group pass** — §2.6, DB-only.

Every batch of new `PhotoAsset` rows carries an `IngestBatch` marker (the `ReviewBatch`
convention): bulk inserts stay reviewable and the timeline can quarantine an ingest until
approved.

### §2.6 Near-duplicates: flags, groups, and a master pick — never a file operation

- **Exact dupes**: SHA256 equality → auto-grouped (`Kind = Exact`), highest-resolution/largest
  member auto-marked master, still listed for review.
- **Near dupes** (the scanned-print problem): pHash Hamming distance ≤ threshold, computed via
  in-memory BK-tree over hash-prefix buckets per run; plus Immich CLIP candidates merged in as
  `Kind = Near` candidates with a similarity score.
- Model: `PhotoDupeGroup` (kind, status Pending/Resolved) + `PhotoDupeMember` (assetId,
  `IsMaster`, similarity). **Resolution = picking a master and resolving the group — rows and
  flags only.** Non-masters default to hidden in timeline/albums (the master represents the
  group; the lightbox offers "other copies"). Default master heuristic: highest resolution →
  largest file → EXIF-bearing; the review UI (side-by-side, zoom-synced) makes the human pick.
- `Phone Backups (Merge Needed)` gets "merged" exactly this way: cross-folder groups where the
  master wins the timeline — the folder on disk never changes.
- **A third group kind: `Variant` — same capture, different files by design.** RAW+JPEG pairs,
  Samsung motion photos (the phone-backup folders are Samsung — the paired/embedded video half),
  iPhone Live Photo `.heic`+`.mov` pairs, edited-copy exports. Auto-paired by basename +
  capture-time; the display half (JPEG/photo) is master automatically and variants never appear
  as timeline clutter. Distinct from `Near` because these need no human review and must never be
  offered for "pick the better copy".
- **Tags, dates, and captions attach to the group master.** Tagging or dating any member
  redirects the write to the master (the UI shows the redirection); browse surfaces collapse to
  masters, so one tagging pass covers every copy. Dissolving a group or changing masters moves
  nothing on disk and re-points the attachments in the same transaction.

### §2.7 Dates for scans: honest estimation, no magic

`TakenAt` (nullable) + `TakenAtSource` enum (`Exif`, `GoogleSidecar`, `FilenameParsed`,
`FolderInferred`, `Manual`, `Estimated`) + `YearMin`/`YearMax` for circa ranges ("late 80s").

- Digital photos: EXIF `DateTimeOriginal` wins; file mtime is recorded but never trusted as a
  taken-date (copies reset it). ⚠ Scanned images sometimes carry the SCANNER's EXIF date —
  scans (by folder or camera-make heuristic) never take EXIF as `Exif`-confidence.
- **Timezone policy: `TakenAt` is naive local wall-clock** (`datetime2`, no offset) — EXIF has
  no timezone, and wall-clock is what a family timeline should sort and group by ("Christmas
  morning" must not land on Dec 24 because of UTC math). Sources that supply true UTC (Takeout's
  `photoTakenTime`, video containers) are converted to wall-clock via GPS when present, else the
  home timezone, and the raw UTC value is kept alongside (`TakenAtUtcRaw`) so the conversion is
  revisitable. Never mix the two representations in one column.
- Filename/folder parsing: `Overlook 7-4-2010`-style names, `IMG_20140312_*`, folder year hints
  → `FilenameParsed`/`FolderInferred`.
- Scans: a curation UI to set year/range fast (keyboard decade/year entry across a strip of
  photos), and a **person-age hint**: if a tagged `FamilyPerson` has a birth year, the UI shows
  the implied bounds ("subject born 2012 → photo ≥ 2012") — a hint surfaced to the human, never
  an automatic write. Immich CLIP search ("birthday cake", "beach") helps FIND cohorts to date
  together; it cannot date them.
- Timeline renders date-unknown items as a dedicated shelf, not scattered at epoch 0.

### §2.8 People: a family person model, separate from the IMDb `Person` table

- `FamilyPerson`: id, display name, optional birth year (feeds §2.7 hints), optional link to a
  site `User`, optional cover face crop. **Never mixed with the IMDb `Person` credits table.**
- `PhotoPersonTag`: assetId + familyPersonId + `Source` (`Manual` / `Suggested` / `Confirmed`)
  + optional face box (x,y,w,h fractions) + confidence + `ImmichPersonId` provenance.
  Suggestions require one-click confirm/reject; nothing auto-confirms.
- **Tag queue UI**: keyboard-first review of untagged/suggested photos (accept cluster
  suggestion, type-ahead a name, box-draw optional). Naming an Immich cluster once retro-fills
  suggestions across the library — the highest-leverage flow in the whole feature.
- Person pages: photos-of-X timeline, co-occurrence ("X with Y"), and person-filtered albums.
- People names live in DB rows only — never hardcoded in code, comments, or seed data (the
  no-personal-details rule).

### §2.9 Albums: curated DB rows; folders come free as a browse view

- `PhotoAlbum` (title, slug, description, cover asset, manual date range, sort) +
  `PhotoAlbumEntry` (assetId, sort, optional caption). Multi-album membership is fine; albums
  can contain videos. Created/edited by any family member from selection mode in any view.
- The **folder view** is just the `PhotoAsset.Path` tree — zero extra modeling — and doubles as
  the "make an album from this folder" seed action (copies membership into rows; the folder
  itself is never the album's identity, so disk layout stays free to be ugly).
- Curation flags on `PhotoAsset`: `Hidden` (timeline/albums exclude; folder view shows all),
  auto-suggested for `Screenshots`/misc folders at ingest, human-confirmed batch-wise.

### §2.10 Google Photos: the API door is closed — mesh via Takeout

**Fact check (2026): the Library API lost third-party read access on 2025-03-31** — the
`photoslibrary.readonly`/`photoslibrary` scopes return 403; apps can only touch content they
themselves uploaded. The replacement Picker API is manual, session-based user selection — fine
for one-off pulls, useless for library sync. So: no nice API-driven mesh exists anymore; the
honest lane is **Google Takeout**, which Google can schedule automatically (export every 2
months, delivered to Drive) — a semi-automated cadence.

- **`photos-google-mesh` (CLI, chunked)** over a Takeout archive in a local staging dir:
  1. Parse Takeout's per-item JSON sidecars — `photoTakenTime`, GPS, description. These are
     often RICHER than the media file's own EXIF (Google strips/loses EXIF on some paths), so
     the sidecar is valuable metadata even for photos we already have.
  2. Match each Takeout item to `PhotoAsset`: filename+size → SHA256 → pHash fallback (Google
     re-encodes some media, so pixel-similarity is the safety net). Store the mapping in
     `PhotoGoogleItem` (takeout filename, taken time, matched assetId, status).
  3. Matched items: backfill `TakenAt` (`GoogleSidecar` source), GPS, descriptions onto assets
     that lack them. Flag-but-write on conflicts (the IMDb pipeline convention).
  4. Unmatched items → **Google-only review list** with thumbnails from the Takeout archive.
- **Idempotent across repeated Takeouts**: `PhotoGoogleItem` keys on (filename, takenTime,
  size) — Takeout sidecars carry no stable Google id — so re-running against next quarter's
  archive upserts matches instead of duplicating them, and an item once matched stays matched.
- **Downloading Google-only items is the one additive NAS write in this plan**, and it is
  opt-in per run: an approved command copies them from the Takeout archive into a NEW dated
  folder (e.g. `L:\7 - Photos & Video\Google Photos Sync\<year>\`), never overwriting any
  existing path, then ingests them normally. Default is report-only, and the download lane
  **refuses to run until the archive's match pass (including pHash fallback) has fully
  drained** — a half-matched archive would download photos we already own.
- The existing Google copy-down folder in the tree (§1) is already-local content; the mesh will
  naturally match much of it and expose what the manual process missed.

### §2.11 Durability: the curation is the treasure — export it

Unlike the movie DB (mostly re-derivable from IMDb + disk), this vertical's value is
**irreplaceable human labor**: person tags, hand-set dates, master picks, albums, captions.
Losing those rows loses years of family effort, and "the DB is backed up" must not be the only
answer.

- **`photos-export` (CLI, cheap, runnable anytime)**: dumps the curation tables — people, tags,
  dates+sources, albums+entries, dupe resolutions, curation flags — as versioned JSON into a
  local exports directory, **keyed by content hash + relative path** so an export can be
  re-applied to a rebuilt DB even after path churn. Small (metadata only), fast, safe to run on
  a schedule; a matching `photos-import --dry-run` proves round-trip fidelity once in CI-like
  fashion before it's ever needed in anger.
- Run it as the closing step of any heavy curation session and before any risky migration.
  Exports are NOT written to the NAS (no XMP sidecars, ever — that would be writing next to
  originals); they live with the other pipeline data under the repo's `data/` convention.

### §2.12 Shelves: the Gallery is a place, not a longer hide list (Phase 7)

The tree carries piles that are not the family record — art the owner collects, memes, reference
scrap; §1 catalogued them under `Misc Pics` and the papercraft/reference folders. The owner's
verdict on them, verbatim in intent: they "are not album material … remove them from the typical
timeline … We'll want a place to store art and memes eventually, but it isn't the timeline, put them
in another section."

**`Hidden` was the nearest existing tool and is the wrong one.** Since the Phase 4 addendum the
hidden pile is revealed only to an ADMIN, so hiding art would take it away from the family rather
than relocate it. This is the opposite instruction. So a second, orthogonal axis:

- **`PhotoShelf` on `PhotoAsset` AND on `PhotoAlbum`** — `Timeline = 0` (the family record) or
  `Archive = 1` (the Gallery). The enum value keeps the STORAGE meaning ("off the timeline"); the
  site's copy calls the section the **Gallery**, because what a family opens is a room of pictures.
- **Shelf answers *which section*; Hidden answers *may a non-admin see it at all*.** They compose,
  and **Hidden always wins** — an archived-and-hidden asset is admin-only everywhere, which is
  exactly what the NWS corner of `SAMisc` needs.
- **Where the shelf is excluded, and where it deliberately is not.** Out of the timeline, the undated
  shelf and person pages. IN the folder view (with a small badge beside the hidden and duplicate-group
  marks — it is the "what is actually on disk" surface, and there an absence is a mystery while a
  badge is an explanation) and IN every album page regardless of either shelf, which is the entire
  point: a Gallery collection must show its artwork to any family member who opens it.
- **There is no `includeArchive` opt-in, and that is the difference from hidden.** Hidden is a privacy
  boundary with an admin override; a shelf is a filing decision, and the way to see the other shelf is
  to go to it.
- **Person pages report what they left out.** Art is not the family record, so gallery pictures are off
  a person's page — but the page carries an "N in the gallery" chip when any of their tagged assets are
  archived. An exclusion nobody can see is indistinguishable from data loss, and unlike hidden this one
  has no checkbox to reveal it.
- **Shelf moves are GROUP-COHERENT** (`PhotoDupeMasters.GroupCoherentIdsAsync`). A settled duplicate
  group (Resolved, or Pending+Exact — the same predicate `Collapsed` is built from) is ONE photograph on
  the browse surfaces, so it changes shelf as a unit. Move only the master and the copies stay on the
  timeline's books while collapsed behind a card that is gone; move only a copy — which is what the
  folder view offers — and nothing visible happens at all. A pending NEAR group does not expand: nobody
  has agreed those are the same picture. The number of rows dragged along is always REPORTED, the same
  courtesy the album and tag routes pay for master redirects.
- **Museum mode.** A `PhotoAlbum.ArtistName` (nullable) on an archive album makes it an **artist
  collection** — the owner collects several. Its page is drawn as a wall: the artist in the headline,
  the album's own title beneath only when the two differ, the deeper `--photos-well` mount tone, a
  taller and airier justified grid, and a small plaque under each picture carrying a filename-derived
  title plus the artist. The title is *derived, never invented* — the §2.7 stance about dates, applied
  to names. Plain collections (memes, misc) keep the ordinary album treatment; the `/photos/gallery`
  index leads with artist collections, then the rest.
- **One album component, two shelves.** `/photos/albums` lists the family shelf, `/photos/gallery` the
  archive shelf, and the DETAIL page stays `/photos/albums/{slug}` on both — so every link anyone ever
  sent keeps working.
- **`photos-shelf` (CLI, chunked/resumable/idempotent, `--sqlite` lane, `--dry-run`)** files a subtree
  by root-relative `--path-prefix` with repeatable `--exclude-prefix`, optional `--album` (create-or-find
  by TITLE — what the operator retypes — with the slug minted server-side), optional `--artist`, and an
  optional one-directional `--hide`. These piles are identified by WHERE THEY ARE and by nothing on the
  row, so the operator names the folder and the pass files it; no heuristic distinguishes a painting from
  a photograph of a wall. Counters per rule: `{matched, shelved, already, album-entries-added, hidden,
  group-coherent}`. Reads no files at all.
- **Export/import carry both fields** (§2.11), additively — an absent shelf reads as `Timeline`, which is
  what every row written before Phase 7 meant, so an older export stays importable. The exporter's
  "assets worth carrying" predicate gained `Shelf == Archive`: a bare shelf move with no album has no
  other trace, and an export that dropped it would restore the memes onto the timeline.

**Future-facing (NOT built here):** an art-sourcing lane may later add publicly-sourcable works to
artist collections; if it does, the additive, opt-in, never-overwriting NAS-write pattern from §2.10's
download lane is the one that applies. Nothing in Phase 7 scrapes, sources or downloads anything.

## §3 Schema (new tables; EF migration in Phase 0)

| Table | Key columns (beyond id) |
|---|---|
| `PhotoAsset` | `Path` (unique, mutable — §2.5 identity rule), `SizeBytes`, `FileModifiedUtc`, `Kind` (Photo/Video), `Sha256`, `PHash`, `DHash`, `Width/Height/DurationSec`, `TakenAt` (naive local) + `TakenAtUtcRaw?`, `TakenAtSource`, `YearMin/YearMax`, `GpsLat/GpsLon`, `LocationLabel(+Source)`, `CameraMake/Model`, `OriginalRenderable`, `RawMetadataJson`, `Hidden`, `IngestBatch`, `JellyfinItemId`, `ImmichAssetId`, `FirstSeenUtc`, `MissingSinceUtc` |
| `FamilyPerson` | `Name`, `BirthYear?`, `UserId?`, `CoverAssetId?`, `ImmichPersonId?` |
| `PhotoPersonTag` | `PhotoAssetId`, `FamilyPersonId`, `Source`, `Confidence`, face box fractions |
| `PhotoDupeGroup` / `PhotoDupeMember` | kind (Exact/Near/Variant), status / `IsMaster`, similarity |
| `PhotoAlbum` / `PhotoAlbumEntry` | title, slug, cover, range / sort, caption |
| `PhotoGoogleItem` | takeout identity, sidecar JSON, `MatchedAssetId?`, status |

Indexes follow the covering-index INCLUDE rule already in use for browse queries: timeline pages
on (`Hidden`, `TakenAt` DESC) INCLUDE the card columns; person/album joins get their own.
(`TakenAt` is the naive wall-clock column per §2.7 — an earlier draft of this table said
`TakenAtUtc`, which contradicted §2.7 and lost.)

**Phase 0 addendum (built 2026-08-12):** `PhotoAsset.Path` is stored ROOT-RELATIVE with forward
slashes (`nvarchar(850)` — the unique-index key ceiling), the MusicTrack convention; the §2.2
token already carries a relative path, so nothing absolute ever enters the table. The
membership check sits behind an `IFamilyAlbumMembership` seam so the policy is testable without
the live DB. **Gate strength decision: `RequireFamilyAlbum` additionally requires the `amr=pwd`
claim (the streaming-surfaces posture) — site login is passwordless, and a username alone must
not open the family album.** Family members therefore need passwords set before Phase 1 ships;
relaxing this is a one-line change in `FamilyAlbumGate`.

**Phase 1 addendum (built 2026-08-12):** the four ingest queues need a predicate the DATABASE can
answer, so `PhotoAsset` gained `MetadataUpdatedUtc` / `HashUpdatedUtc` / `ThumbsUpdatedUtc` (stamped
whether the pass succeeded or failed — a queue that keeps its failures is an infinite retry, not a
job that terminates), `ThumbState` + `ThumbKey` + `ThumbVariants` (which derivatives exist and under
what name), and `IngestError` (last failure; a pass may only clear an error it wrote). Each queue
gets a FILTERED index on `Id` matching its predicate, so the index shrinks to empty as the queue
drains. **The queues are independent**: a failed EXIF read does not stop the same file being hashed
or thumbnailed. Decisions taken here that the plan left open:

- **HEIC/HEIF/AVIF/RAW get no derivatives in Phase 1.** MetadataExtractor reads their metadata and
  they are fully catalogued and hashed; decoding their PIXELS needs Magick.NET, a large native
  dependency added to a container that ships to prod. They carry `ThumbState = UnsupportedFormat` —
  a state the UI renders as a placeholder, distinct from `Failed`.
- **A folder year sets `YearMin`/`YearMax` only, never `TakenAt`.** A year is not a wall clock, and
  writing January 1st would pile a decade onto one day — the failure §2.7's undated shelf exists to
  prevent, wearing a more convincing date. `TakenAtSource = FolderInferred` still records where the
  hint came from, for the Phase 2 dating UI.
- **GPS date+time is Phase 1's one true-UTC source** and exercises §2.7's conversion path
  (`TakenAtUtcRaw` kept, wall-clock derived through `PhotosHomeTimeZone`).
- **Ambiguous move pairings are a JSON review artifact** under `PhotosReportDir` (default
  `data/photos`), not a new table — nothing is applied, and the same ambiguity re-reports every run
  until a human resolves it.
- **Move detection works in BOTH directions.** Source-first is the obvious case; when the
  destination folder sorts first, the candidate's same-name/same-size rows are tested for a file that
  is no longer on disk. Without that, half of all real moves would become an orphan plus a new row.

**Phase 2 addendum (built 2026-08-12):** curation, albums and the export/import lane shipped with **no
migration** — the schema already carried `Hidden`, `IngestBatch`, `PhotoAlbum`/`PhotoAlbumEntry`. The
decisions that needed making:

- **Review state lives in JSON artifacts under `PhotosReportDir`, not in a table** (`curation/
  ingest-batches.json`, `curation/hide-<batchId>.json`) — the Phase 1 precedent for review artifacts,
  and what avoids a migration against the live shared database. Consequence, stated plainly: the CLI
  writes proposals on the host that can read the collection and the site reads them to render the
  review surface, so `PhotosReportDir` must resolve to the SAME directory for both (the way
  `PhotosThumbCacheDir` already must for the gateway). When it does not, the surfaces **fail open**:
  no proposals to review and nothing quarantined. A future `PhotoCurationBatch` table would remove
  that requirement.
- **Quarantine has a baseline.** The first time the ingest-review state is materialized, every batch
  that already exists is recorded as approved; quarantine therefore describes only what arrives
  AFTERWARDS. Without that, turning the feature on would empty a timeline that had already been
  ingested — a failure indistinguishable from data loss on the one surface whose job is to show that
  nothing was lost. Above 200 unreviewed batches the timeline stops filtering and says so, rather than
  turning the hottest query on the page into a thousand-term `IN`.
- **Batch markers are grouped by DAY for review** (`photos-yyyyMMdd-HHmmss` → `photos-yyyyMMdd`): a
  chunked walk mints one marker per invocation (the Phase 1 fact), so a night's ingest is one row to
  approve instead of forty. A hand-passed `--batch-id` that is not date-shaped stands alone.
- **`photos-suggest-hide` cannot hide anything.** It writes a proposal artifact; the flag is written
  only when a family member accepts the batch on the review surface. Rules: `screenshot-folder`,
  `screenshot-filename`, `misc-folder`, `tiny-image`, `non-photo-format`, each stamped per item so a
  bad rule is one rejectable cluster rather than scattered mistakes. The keyword lists deliberately
  exclude "scan"/"scans" — proposing to hide the scanned prints would be the worst suggestion this
  pass could make. ⚠ `non-photo-format` (a PNG/GIF/BMP with no camera and no EXIF date) is the
  aggressive one on graphics-heavy trees; review it by rule, or narrow a run with `--rules`.
- **Album slugs are minted server-side and never re-minted on a retitle** — a slug is a link a family
  member may already have sent. **Reorder accepts a PARTIAL list**: the ids sent take the front and
  everything else keeps its relative order behind them, which is what dragging one card means; unknown
  and duplicate ids are dropped and counted rather than failing the call. Folder-seeded albums copy
  the whole SUBTREE and skip hidden assets.
- **Export/import key on content hash first, relative path second**, and refuse to guess: a hash that
  matches several local rows with no path agreement is reported AMBIGUOUS (the §2.5 stance). Import
  **defaults to dry-run** and needs an explicit `--apply`; a local `Manual` date is never overwritten
  by a non-Manual exported one (§2.7), and that is counted rather than silent. Both commands are
  chunked: the export resumes at section granularity, the import at `section:index`.

**Phase 3 addendum (built 2026-08-12):** dupes shipped with ONE migration —
`AddPhotoCurationBatches` (`scripts/photos-phase3-migration.sql`, purely additive: two CREATE TABLEs
and their indexes, no ALTER or DROP of anything existing). `PhotoDupeGroup`/`PhotoDupeMember` were
already there from Phase 0 and did not change. Decisions taken here:

- **Phase 2's review state moved from JSON into `PhotoCurationBatch` / `PhotoCurationBatchItem` rows,
  which closes a real prod gap**: the site pods have no path to the CLI host's `PhotosReportDir`, so
  every JSON-backed review surface renders EMPTY in prod while looking healthy. One table serves both
  uses (ingest approval + hide proposals) plus an `IngestBaseline` marker row, without which "no
  approval rows" would be ambiguous between "never materialized" and "nothing approved" — and reading
  it the second way would quarantine a whole pre-existing collection. `PhotosReportDir` keeps only the
  artifacts nobody reads across a host boundary: ambiguous-pairing reports (§2.5) and exports (§2.11).
  The export format is at **v2** (new `curation-batches.json` section, appended so an in-flight
  `section:index` cursor still resumes correctly); v1 exports still import, their missing section
  reading as zero rows.
- **Variant groups are machine-RESOLVED, not Pending.** §2.6 says they need no human review and must
  never be offered for "pick the better copy", so they are settled by the pass with `ResolvedUtc` set
  and `ResolvedByUserId` deliberately NULL — a variant pair is not somebody's judgement. Exact groups
  do start Pending (auto-mastered, still listed for confirmation) as §2.6 asks.
- **Collapse ≠ resolution.** Browse excludes non-masters of any *settled* group: Resolved of any kind,
  plus Pending `Exact` (byte equality has no judgement left in it). A Pending `Near` group collapses
  NOTHING — nobody has agreed those are the same picture yet, and hiding half a family's scans on a
  hash's say-so is what the review UI exists to prevent. `PhotoDupeMasters` holds that predicate ONCE,
  as an EF expression, and the master-redirect helper uses the same one, so "collapsed out of browse"
  and "writes redirect here" can never drift apart. Album-entry creation already routes through it
  (adding a duplicate adds the master, counted and reported); Phase 4's tagging joins it there.
- **A rejection binds the PAIR and is kind-agnostic.** "Not the same photo" is a statement about the
  photographs, not about the lane that proposed them, so a Rejected group blocks the exact lane from
  re-minting the same grouping just as it blocks the near lane. A Rejected group is a tombstone: never
  revalidated, never deleted, and its members may still join a group with a *different* photo (the
  "one active group per kind" rule counts only Pending/Resolved).
- **The near lane works on what browse SHOWS.** Its queue excludes hidden assets and the copies a
  settled group already collapsed — otherwise a phone-backup folder of a thousand identical files
  would be proposed a second time as a thousand near groups. That is also why `--pass all` runs
  exact → variant → near.
- **The hash-prefix buckets are exact, not an approximation.** Split the 64-bit pHash into
  `threshold + 1` blocks; by the pigeonhole principle two hashes within the threshold must agree
  exactly on one block, so searching only the query's own buckets misses nothing. A BK-tree per bucket
  (flat arrays, ~24 bytes/entry) prunes the rest. Cost, since it is rebuilt PER RUN: one projection
  query plus `(threshold+1) × n` inserts — tens of MB and about a second at 150k photos, paid once per
  invocation, which is why a driver loop should give the near pass a decent `--max-batches` rather
  than one batch per call. Candidate pairs per batch are capped (`--max-pairs`).
- **Cursors, per lane:** exact pages `Id` over groups (a revalidate phase, which re-tests a group whose
  members' hashes changed under it) then `Sha256` over duplicated hashes; near pages `Id`; variant pages
  `Path` and EXTENDS each batch to the end of its final directory, because the pairing key is
  directory + basename and a cluster split across a boundary would pair nothing. The greatest path is
  taken from the database's own ordering, never an ordinal max in memory — a case-insensitive server
  collation would disagree and skip the rows in between.
- **Variant pairing requires the same DIRECTORY and the same stem**, plus capture-time agreement
  whenever both halves carry a date (a Phase 1 video carries none, so a missing date permits rather
  than forbids). Matching stems across folders is how a 2007 camera's `IMG_9000.jpg` gets welded to a
  2019 phone's `IMG_9000.mp4`. Only recognized shapes pair — RAW+still, HEIC+video, still+short video —
  so two ordinary stills of the same name are left to the Exact/Near lanes rather than auto-mastered
  with no review. ⚠ **The embedded single-file motion photo is COUNTED, not grouped**: a group needs
  two rows. Detection is opportunistic (a `MotionPhoto`/`MicroVideo` key already present in the stored
  raw metadata); extracting the embedded video needs a demuxer and belongs with Phase 5.

**Phase 4 addendum (built 2026-08-12):** people, tagging, the tag queue and the Immich lane shipped with
**no migration** — `FamilyPerson` and `PhotoPersonTag` have existed since Phase 0, and the two states this
phase needed were added as ENUM VALUES on existing int columns (`PhotoTagSource.Rejected`,
`PhotoCurationBatchKind.ImmichSync`), which is a schema change of exactly zero bytes against a database
that is shared with production. The decisions that needed making:

- **Show-hidden is now ADMIN-ONLY and lives in the NAVBAR** (the owner's decision, superseding Phase 2's
  member-visible toggle and §2.9's "folder view shows all"). Any family member may still hide or unhide —
  that is ordinary curation — but the hidden pile is revealed only to an admin who is also a member. The
  rule binds **every** surface: timeline, folder tree, album pages and person pages all honour
  `includeHidden` only for an admin, because a folder tab that opted out would not be a rule, it would be
  a longer route to the same pictures. A non-admin asking is **IGNORED, not refused** — a 403 would tell a
  stale tab that there is something there to be forbidden. The checkbox state persists in `localStorage`
  like the theme and the type scope, and it is never the gate: the server ignores the parameter regardless
  of what is stored. Albums also gained the hidden exclusion §2.9 always specified; before this only the
  folder-SEEDING path honoured it, so a photo hidden after it joined an album stayed visible there.
- **An imported face cluster is a `FamilyPerson` row with an EMPTY NAME.** That is the "unnamed group of N
  faces" state, it is the only state a machine may create a person in, and it is why naming one costs no
  tag rewrite — the suggestions were always pointed at that row, so one rename fans them across the
  library (§2.8's highest-leverage flow, in one click). **Immich's own name for a cluster is deliberately
  NOT imported**: names are the family's, they live in our rows and nowhere else (§6), and a machine
  inventing one is the auto-confirmation §2.8 forbids wearing a different hat. Mapping a cluster onto
  somebody who already exists MERGES, resolving collisions in favour of the stronger claim, so a merge can
  never weaken a human's tag into a guess or revive a refusal.
- **A refused suggestion is a TOMBSTONE, not a delete** (`PhotoTagSource.Rejected`), the
  `PhotoDupeGroupStatus.Rejected` stance applied to faces and for the same reason: without the row the
  next sync re-proposes the identical face, and a queue that re-asks an answered question is a queue
  nobody opens. **An UNTAG is different and deletes outright** — "I picked the wrong person" is not "the
  recognizer is wrong", and recording the first as the second would permanently bar a machine from ever
  proposing somebody who really is in the picture. A tombstone counts as nobody being in the photo, so a
  refused photo stays in the *manual* queue.
- **Every tag write routes through `PhotoPersonTags`**, which redirects to the group master via the same
  `PhotoDupeMasters` predicate browse collapses with (§2.6) and REPORTS the redirect count. Dates redirect
  identically. A second write path that forgot is exactly how a family's tags end up on the copies nobody
  sees, which is why the controller, the batch action and the sync all call the one helper.
- **The date editor writes wall-clock as typed, and a RANGE never invents an exact date** (§2.7 + the
  Phase 1 addendum's rule, restated where a human can trigger it): January 1st would pile a decade onto
  one day while wearing a more convincing date than the undated shelf it escaped. The birth-year hint is
  computed as the LATEST birth year among the people tagged and is printed beside the field — surfaced,
  never applied.
- **The tag queue's manual lane is the default and needs no sidecar at all.** `untagged` opens first and
  `suggested` is the second tab; that ordering is §2.4's posture as a UI shape, and it is why removing
  Immich later removes a tab rather than breaking a workflow. Keyboard: **Y** accepts, **N** refuses,
  **S** skips, **← →** move.
- **`ImmichClient` pins the MAJOR version and refuses outside it** rather than mis-parsing an API it has
  never seen (mis-parsing a face box into a tag row is a silent wrong answer; refusing is a loud right
  one). The version is recorded with each run on a `PhotoCurationBatch` marker row, so "which Immich
  produced this suggestion" is answerable months later. Asset mapping is a **two-segment** root-relative
  path suffix match — a phone-backup tree is full of `IMG_0001.jpg` and a file name alone would map the
  wrong photograph — and an **ambiguous match is skipped, never guessed** (the §2.5 stance). The path
  index is built once per run (one projection query, tens of MB at 150k photos), the same cost profile the
  near lane's hash index already pays, because the alternative is a `LIKE '%…'` per Immich asset.
- **The sidecar's paged lanes cannot report a true `remaining`** — Immich answers "is there another page",
  not "how many are left" — so those lanes report 1/0 and the log says so, rather than inventing a count.
  The face lane pages OUR rows and reports a real one. Cursors are `phase:mark`, the `PhotoDupePass` shape.
- **Duplicate candidates go through `PhotoDupePass.LinkExternalNearAsync`**, a door into the near lane's
  own code rather than a second implementation, so the rejected-pair check, the one-active-group-per-kind
  invariant and the master heuristic are literally the same code. A human's "not the same photo" therefore
  blocks the sidecar exactly as it blocks pHash (§2.6's kind-agnostic rejection). Immich reports no
  distance, so members are stamped at distance 0 as a LABEL — a plausible-looking 0.97 would be read as
  something the near lane computed.
- **Face crops are fetched by the SYNC, not by the site**, into `PhotoThumbCacheDir/faces/<hash>.jpg`, and
  served through an ordinary capability token — the browser never learns a sidecar exists. Keyed by a hash
  of the cluster id so an opaque upstream id cannot escape the cache directory. With no crop the queue
  draws the stored box over our own `view` derivative; the fractions live on the tag row precisely so that
  works with Immich unreachable or thrown away, which is the §5 acceptance criterion.
- **Exercised end to end against a STAND-IN Immich**, in-process for the sync tests and over loopback HTTP
  for the client's parsing, plus a `scripts/photos-immich/fake-immich.mjs` dev fixture the CLI smoke drives.
  No test, build or smoke ever contacts a live instance. The deployment runbook is
  `docs/photos-immich-setup.md`; nothing in it runs from this repository.

**Phase 5 addendum (built 2026-08-12):** videos shipped with **no migration** — `PhotoAsset` has carried
`DurationSec`, `Width/Height`, `TakenAtUtcRaw` and `JellyfinItemId` since Phase 0, and the two states this
phase needed were added as ENUM VALUES on existing int columns (`TakenAtSource.VideoContainer`,
`PhotoCurationBatchKind.JellyfinReserved`) — the Phase 4 precedent, a schema change of exactly zero bytes
against a database shared with production. The decisions that needed making:

- **The movie-side exclusion shipped FIRST and is a PATH-PREFIX rule that needs no library id.** §2.3 named
  the order; this is why the rule is what it is. A library id is a fact about a Jellyfin server that may not
  exist yet, may be recreated, and that the item listings do not carry per item anyway — a path prefix is a
  fact about the collection, which is the thing being protected. `PhotosJellyfinLibraryId` therefore only
  WIDENS the net (its library's own `/Library/VirtualFolders` locations become further prefixes, and only
  when the setting is present, so an ordinary sync pays nothing for it). The filter is applied to **every**
  item list `JellyfinSyncService` obtains — the main sweep, the extras sweep, the alternate-version rescue
  and the per-movie re-link — rather than at the matching step, so a family video is gone before move
  detection, extras placement or the report can see it. That ordering is load-bearing: the fingerprint pass
  re-points a DB row onto an untracked item by (name, size), and a family clip sharing both with a movie
  whose file went missing would otherwise be silently adopted. ⚠ **A root that names a whole volume is
  REFUSED, not obeyed** (`Q:\`, `\\server\share`): silently emptying the movie site is a worse outcome than
  an un-excluded family library, and the check runs on the CONFIGURED root before expansion because a bare
  drive translates into a perfectly deep-looking share.
- **`photos-sync-jellyfin` fetches the library ONCE per run and chunks over that list.** Cursors are
  `phase:mark` (`i:` index into the fetched order, `c:`/`r:` our own row ids) — the `PhotoDupePass` shape.
  Both vocabularies are handled by expanding the configured root into every form the mappings can express
  and testing an incoming path as reported AND as translated, so `L:\…`, `\\server\share\…` and a Linux
  server's forward slashes all reduce to the same root-relative key; the **original casing of the relative
  part is preserved**, because the comparison is case-insensitive but the stored `Path` came from a
  filesystem walk and would not match on a case-sensitive collation. A path outside every root form is
  reported, never guessed at (the §2.5 stance). `--extra-root` adds a form no mapping describes;
  `--items-json` drives the lanes from a local listing, the `--sqlite` reasoning applied to the media server.
- **An EMPTY library answer clears nothing, and says so.** "The library reported no items" and "every video
  was deleted" are indistinguishable from the sync's position, and only one of them justifies unstamping the
  whole album — so a server that is restarting, misconfigured or mid-scan cannot empty every play button. A
  malformed `--items-json` is a clean refusal for the same reason: quietly reading as an empty list would
  turn a typo into a lane that silently did nothing while reporting success.
- **Videos are their own ingest pass (`photos-ingest --pass video`), not a branch inside metadata/thumb.**
  It needs a capability those passes do not — external binaries — and a host without them must still drain
  the photo queues, so `video` runs LAST in `--pass all` and a host with no ffprobe reports that and changes
  nothing (the rows stay exactly where Phase 1 left them, and a host that later gains the binary finds the
  work waiting). It fills BOTH halves for a video in one visit, because both come from two invocations
  against one file and reading a 4 GB clip off the NAS twice to answer two questions is the cost that
  collapses. Its queue keys on `ThumbState` (VideoDeferred/Pending) rather than a timestamp: Phase 1 already
  stamped `MetadataUpdatedUtc`/`ThumbsUpdatedUtc` on every video, so a timestamp predicate would find an
  empty queue on exactly the collection this is for. The photo thumb pass was taught not to demote a `Ready`
  video back to a placeholder — the two passes stamp different columns and would otherwise fight.
- **The binaries are bounded, killed, and never trusted.** Each invocation gets a hard ceiling (default 60s,
  `--video-timeout-seconds`) and is killed with its process tree past it; output is drained on background
  handlers, not read after the wait (the classic pipe-buffer deadlock); stdout is size-capped, parsed inside
  a `try`, and every number goes through `TryParse` with the invariant culture before it becomes a column.
  Poster frames are written into the derivative cache and deleted, never beside an original (§6).
- **A nonsensical container date is DROPPED rather than stored.** QuickTime's epoch is 1904 and an unset
  `creation_time` routinely surfaces as exactly that; a dead camera clock gives 1970. Dates outside
  1990 → now are refused, because a wall of confidently-dated 1904 clips at the oldest end of a family
  timeline is the failure §2.7's undated shelf exists to prevent wearing a more convincing date. A real one
  takes §2.7's true-UTC path (`TakenAtUtcRaw` kept, wall clock derived through `PhotosHomeTimeZone`) and is
  stamped `TakenAtSource.VideoContainer` — its own value, not `Exif`, because a reader deciding whether to
  trust a date needs to know which kind of stamp produced it. A human's `Manual`/`Estimated` answer is never
  overwritten; a filename guess is.
- **The poster is a MIDPOINT grab**, falling back to one second and then the first frame. A home video's
  opening frame is a lens cap, a lap, or black. Videos get `grid` + `view` only — no `zoom`, which exists so
  an un-renderable ORIGINAL still has a deep-zoom target, and nobody deep-zooms a poster frame.
- **`--motion-seconds` now has teeth.** §2.6 bounded a variant pair's video half at 10s but noted "videos
  carry no duration until Phase 5 runs ffprobe, so null passes". Measured on the fixtures: a still and a
  20-minute recording sharing folder+stem+time pair BEFORE the video pass and refuse afterwards; a 1.5s
  motion-photo half still pairs. So the durations arriving costs the lane none of its real pairings and
  removes the coincidental ones — the variant lane revalidates every run, so this converges without any
  re-grouping step.
- **Playback is a separate, minimal minter**, not the movie stack. `IPhotoVideoPlayback` mirrors
  `StreamController.Start`'s three steps (Jellyfin describes, the site signs, the gateway serves) and reuses
  the SAME `StreamCapabilityToken`, so the gateway's existing `/s/{token}/Videos/…` route serves this with
  no gateway change and no second copy of the item-confinement check. Deliberately absent: the age gate
  (§2.1 has no rating logic — membership IS the decision), resume bookkeeping, audio auto-selection,
  subtitle delivery, ABR, forced re-encode and the transcode-concurrency guard. The token's second field is
  a movie id and a family video has none, so it carries **0** rather than a borrowed asset id — a number
  that read as a movie id would mislead anyone who ever inspected a token. **The Jellyfin item id comes from
  the ROW, never from the request body**: accepting one would turn a family-gated endpoint into a
  general-purpose media-server proxy for anyone inside the gate.
- **"Not yet synced" is a first-class state, never a dead button.** `Card.videoSynced` drives a tile badge
  (which otherwise shows ffprobe's duration) and the lightbox renders an explained panel; the endpoint
  answers **409 with a sentence**, not 404 — the file is on disk, browsable, taggable and album-able, and
  the missing piece is a pipeline step the owner runs. The player is a small component over `createHls`
  (the site's hardened hls.js construction) rather than the Watch page's `VideoPlayer`, whose quality
  ladder, ABR, four subtitle renderers and resume machinery all solve problems a shelf of home videos does
  not have.
- **The reserved-folder audit is a REPORT with no action attached, and that is deliberate.** Its two
  remedies are a rename under the collection root (forbidden absolutely, §6) or a Jellyfin-side library
  configuration change (not this pipeline's to make), so the batch is created `Accepted` — a Pending row
  would sit in the review surface forever asking a question nobody is allowed to answer. It lives in
  `PhotoCurationBatch` rows for the Phase 3 reason (the site pods cannot read the CLI host's report
  directory, so a JSON-backed audit renders empty in prod while looking healthy), is grouped by FOLDER by
  the admin endpoint because one collision is one decision, and names the NEAREST reserved ancestor because
  that is the folder a human would act on. The list is this repository's own hard-won one — the homevideos
  migration renamed 64 movie folders `X` → `X Content` to escape it — matched WHOLE-SEGMENT and on folders
  only, since Jellyfin's rule is about directory names and a video called `Scenes.mp4` indexes fine.
- **Exercised end to end without ever touching the live server or the NAS.** The movie-side exclusion is
  proven by running the whole `JellyfinSyncService` against a canned HTTP handler (the real `JellyfinApi`
  parsing runs; the handler cannot reach a network); the family sync runs against an in-process
  `IPhotoJellyfinSource`; the ffprobe parsing is asserted against a GOLDEN readout captured from a real
  ffprobe over a synthesized clip, and the end-to-end binary lane synthesizes its own four-second clip with
  ffmpeg and skips itself when ffmpeg is absent. **`sync-jellyfin` was not run in any mode**, per the
  standing prohibition.

**Phase 6 addendum (built 2026-08-12):** the Google mesh shipped with **ONE migration** —
`AddPhotoGoogleMeshColumns` (`scripts/photos-phase6-migration.sql`, three additive nullable columns on
`PhotoGoogleItem`, no ALTER of an existing column and no DROP of anything). It is the first schema
change since Phase 3: Phases 4 and 5 got away with enum values on existing int columns, and this one
could not, because three facts §2.10 requires had nowhere on the row to live — `MatchDistance` (the
pHash distance a resemblance match was accepted at, whose presence IS the lower-confidence marker),
`Disagreements` (which fields the sidecar disagreed about, which the review surface counts and which
therefore has to be a row in the shared database rather than JSON on the CLI host — the Phase 3
lesson), and `DownloadedPath`. The decisions that needed making:

- **The sidecar's `title` is the item's identity, not the name on disk** — with one exception that
  matters. Takeout truncates long file names on disk and truncates the sidecar's own name by a
  different rule, so a directory entry is not stable across two exports of one library while the title
  is. The exception: an item that reached its sidecar through a FALLBACK keeps its own disk name,
  because that sidecar describes a different file. An `-edited` export and a live-photo's video half
  both borrow the original's metadata, and letting either claim the original's title would collide two
  rows on §2.10's identity triple the moment their sizes agreed. Pairing is directory-scoped (Takeout
  keeps an item beside its sidecar), so it needs no index of the archive, which is what keeps the scan
  pass bounded. Rungs: exact name → JSON title → the `(1)` counter moved back past the extension →
  `-edited` stripped → same stem; the first three grant ownership, the last two do not.
- **`*.supplemental-metadata.json` is matched by PREFIX**, not by an exhaustive list: the suffix is
  itself truncated to fit the same length budget, so `.supplemental-metad.json` and
  `.supplemental-me.json` are equally real. A counter may sit after the suffix too. Only DIGITS in
  parentheses count as a Takeout counter — `Wedding (Copy).jpg` is a file name, and rewriting it would
  invent a pairing.
- **A malformed sidecar is counted; an album manifest is not.** Both are skipped, but an archive is
  also full of perfectly valid JSON that is not an item (`metadata.json`, print subscriptions, shared
  album comments), and counting those as failures would make the number useless as a health signal.
  Either way the media file still becomes an item: it has a name and a size, which is enough to match
  on.
- **⚠ The video pass's 1990 date floor is deliberately NOT applied here**, and getting this wrong was
  a real bug caught by the CLI smoke rather than by a test. That floor exists because a container's
  unset `creation_time` surfaces as the QuickTime 1904 epoch; a Takeout sidecar has no such sentinel,
  and a family that uploaded scanned prints holds real 1950s and 1980s dates — the very dates §2.7's
  scanned-album problem is about. Applying the floor would have silently discarded the most valuable
  metadata in the archive. What is refused instead: a non-positive stamp, the Unix epoch DAY itself
  (the shape a zeroed field takes), and anything in the future.
- **The conflict rule, stated in both directions.** The source hierarchy is an explicit RANK TABLE,
  not the enum's numeric order — `TakenAtSource.VideoContainer` is the highest enum VALUE (Phase 5
  appended it to a live int column) while it is a peer of `Exif`, so comparing enum values would be
  right only by accident. **Sidecar WINS** when the local source is strictly weaker
  (Unknown/FolderInferred/FilenameParsed): the date is WRITTEN and, when it displaced a real date that
  disagreed, the write is FLAGGED (`takenAt-overwritten:<source>`) — flag-but-write, per §2.10, because
  a filename guess losing to Google's own record of when the shutter fired is the improvement this pass
  exists for. **Sidecar LOSES** to an equal-or-stronger source (GoogleSidecar/Exif/VideoContainer/
  Estimated/Manual): nothing is written and the disagreement is recorded (`takenAt:<source>`) and
  counted. GPS is written only where BOTH coordinates are null and `LocationLabel` only where null
  (source-stamped); coordinates carry no source column, so anything already there is treated as at
  least as strong. Tolerance is 60 minutes by default — ⚠ a photo taken in another timezone
  legitimately disagrees by hours, so travel-heavy folders will produce counts, and the count is a
  report rather than a fault.
- **The description gets no column and no migration.** `PhotoAsset` has no caption field; adding one
  would need an editor, a master-redirect and a place in the export — a feature, not a backfill. The
  text stays inside the verbatim sidecar on the item row and the asset-detail endpoint surfaces it as
  what it is, Google's, beside the photograph rather than pretending to be ours.
- **The matching cascade resolves AMBIGUITY to the lowest id, counted — deliberately not the "refuse
  rather than guess" stance the Immich lane takes.** There, a wrong map attaches a stranger's face to a
  family photograph: a visible falsehood. Here the candidates are local files of the same name and size
  — copies of one picture — and the only consequence is which copy receives a sidecar date (which §2.6
  redirects to the group master anyway). Refusing would push the item onto the Google-only list and the
  download lane would offer to fetch a photograph the family already owns, which is the precise failure
  §2.10's drain guard exists to prevent. The pHash rung reuses the near lane's threshold (8 bits) on
  purpose: "the same picture, re-encoded" is one question, and two passes answering it with different
  numbers would refuse to mesh a pair §2.6 had already grouped.
- **The local library is indexed ONCE per run** (name+size, SHA-256, and a `PhotoHashIndex` BK-tree) —
  the same up-front cost profile the near lane and the Immich lane already pay, because the alternative
  is a size filter plus a trailing-path `LIKE` per archive item, which is a scan each and makes a real
  archive unfinishable.
- **Google-only derivatives live in the cache's own `google/` namespace** and the gateway needs no
  change: it still joins a relative path onto its thumb-cache mount, and `PhotoPathConfinement` already
  enforces that a deeper path is still inside the root. The token's asset field carries **0** — a
  Takeout item has no `PhotoAsset`, which is the entire reason it is on the list, and a borrowed asset
  id would mislead anyone who inspected a token (the Phase 5 video-token stance). The thumb pass is the
  one queue here that is NOT self-draining, because "has a thumb" is a fact about a directory rather
  than a column; a second run is an existence check per row, not a re-decode.
- **The download lane is guarded three ways, all refusals.** No `PhotosGoogleSyncDir` → refuse (there
  is no default and there never will be one). Any item still Pending → refuse, because the pHash rung
  has not ruled that item out and a half-matched archive downloads photographs we already own (§2.10
  states this guard explicitly; it is checked before a byte moves). An existing destination → per-item
  error, skipped, counted, never an overwrite and never a `(1)`. Ignored items are excluded: "no" is an
  answer and the lane must not re-ask it. Files land in `<syncDir>/<wall-clock year>/`, undated ones in
  `undated/`, and are then picked up by the ordinary `photos-ingest` walk — they are NOT special-cased,
  which is what keeps this a copy rather than a second ingest path. It is armed by `--download` rather
  than by a pass name, so it cannot be typed by accident in a list of read-only passes.
- **Exercised end to end against a GENERATED archive.** The test suite manufactures every quirk with a
  known answer, and the CLI smoke ran on SQLite against a second, independently generated fixture:
  all three rungs fired (name+size 1, sha256 1, phash 3), Google-only thumbs were written, a re-run
  changed nothing (`unchanged: 7`, match processed 0), the download lane refused twice and then copied
  into `2019/` and `undated/`, and a planted collision was skipped and counted on that run and on every
  run after it. ⚠ One observed fixture artifact worth recording: the smoke's flat four-block painter
  produces low-entropy images whose pHashes collide at exactly the 8-bit threshold, so an unrelated
  picture matched by resemblance — which is also a demonstration that the recorded distance is what
  makes such a match findable. **No test, build or smoke read a real Takeout archive, the NAS, or the
  configured database, and the download lane has never run outside a temp directory.**

## §4 API + UI surface

- **API** (`/API/Photos/*`, all behind the §2.1 policy, all paged): timeline (cursor-paged by
  taken-date), folder listing, albums CRUD, person CRUD + tag confirm/reject, dupe groups +
  master pick, curation flags, token minting for thumbs/originals, video stream start,
  admin: ingest-delta trigger + progress, google-mesh review list.
- **UI** (`/photos`, Ant Design 6, own route chunk): virtualized justified-grid timeline (reuse
  the site's hardened infinite-scroll patterns), folder browser, album pages, person pages,
  lightbox (zoom/pan, EXIF panel, "other copies", video playback via the existing player),
  selection mode → album/tag/hide batch actions, tag queue, dupe review (side-by-side with
  synced zoom), scan-dating strip (§2.7). Mind the lazy-loaded-page CSS trap and the
  mini-player bottom offset — both known site gotchas.

## §5 Phases (each independently shippable)

- **Phase 0 — Gate + schema.** Migration for §3; `FamilyAlbum` setting + policy; hidden nav
  entry. Acceptance: non-family user gets 403 on every `/API/Photos` route.
- **Phase 1 — Ingest + browse.** `photos-ingest` (walk/metadata/hash/thumb queues), gateway
  `PhotoThumb`/`PhotoOriginal` routes + thumb cache, timeline + folder views + lightbox.
  Deploy notes: gateway binary hand-redeployed with new config keys; site appsettings gains the
  photo config block. Acceptance: full-tree ingest driven to completion in chunks with progress
  visible; timeline browses smoothly; a photo added to the NAS appears after "Find new photos".
- **Phase 2 — Curation + albums.** Hidden flags + suggested-hide batches; albums CRUD +
  folder-seeded albums; covers; **`photos-export`/`photos-import --dry-run` ship here, the
  moment irreplaceable rows start existing**. Acceptance: screenshots pile hidden in one review
  session; an export round-trips through import dry-run losslessly.
- **Phase 3 — Dupes.** Hash + pHash grouping passes, `Variant` auto-pairing (motion photos,
  RAW+JPEG, Live Photos), dupe review UI, master-pick defaults, timeline collapses groups to
  masters. Deliberately BEFORE mass tagging: tags land on masters (§2.6), so resolving dupes
  first means one tagging pass covers every copy instead of tagging the same print three times.
  Acceptance: the merge-needed folder's cross-folder dupes are grouped and resolvable without
  any file changing; a motion photo shows as one timeline item.
- **Phase 4 — People + Immich.** `FamilyPerson`/tag model + manual tagging + tag queue first;
  then the Immich sidecar (read-only CIFS mount, external library, single user) +
  `photos-sync-immich` suggestions + location labels. Acceptance: naming one face cluster
  fans suggestions across the library; pulling the Immich container leaves tagging fully
  functional.
- **Phase 5 — Videos.** Movie-sync exclusion first, then the Jellyfin family library, scan →
  `photos-sync-jellyfin`, gated playback, poster grabs, reserved-folder-name audit report.
  Acceptance: family videos play in `/photos`; zero family items appear in any movie-site
  surface or review queue.
- **Phase 6 — Google mesh.** Takeout staging convention + `photos-google-mesh` (report-only
  default) + review UI; the approved additive download lane last. Acceptance: a full Takeout
  meshes with per-chunk progress; conflicts flagged-but-written; google-only list reviewable.
- **Phase 7 — The Gallery shelf.** `PhotoShelf` on assets and albums + `ArtistName`; the query
  exclusions and the folder badge; `/photos/gallery` with its museum treatment for artist
  collections; the member selection-bar move (group-coherent) and the album editor's shelf toggle;
  `photos-shelf`; export/import carry both fields. Acceptance: the art piles leave the timeline
  without leaving the folder view, every family member can browse them, and re-running the whole
  CLI sequence changes nothing.

## §6 Standing rules this plan inherits (restated because they bind every phase)

- No file deletes/renames/moves/writes under `L:\7` — DB flags only; the one additive download
  lane is opt-in, non-overwriting, and separately approved. File-existence checks use literal
  APIs (`[IO.File]::Exists` / `-LiteralPath`) — the `[ ]` wildcard trap is real in this tree.
- Never a full `L:\` scan; `L:\7` subtree walks only, and even those chunked.
- Every bulk pass: bounded per call, progress-reporting, resumable, cursor ordering audited
  against the query ordering, "done" cross-checked with an independent count.
- Bulk inserts carry a batch marker and are reviewable before they join the browse surface.
- No family member names or personal paths in code/comments/commits — DB rows and config only.
- Jellyfin scans stay disabled; scan-then-sync is manual; sync-jellyfin runs are the owner's.
- The dev connection IS the live shared DB: Phase 0's migration (and every later one) is applied
  end-to-end under the established migration-ops discipline, with a `photos-export` (§2.11) run
  first once curation data exists.
- **Privacy invariant: photo tables join NOTHING global.** No OData exposure, no site-wide
  search index, no AI-insight/tagging pipelines, no recommendation or channel inputs, no
  poster-mosaic or landing-page surfaces. Family photos are reachable exclusively through the
  family-gated `/API/Photos` routes — any future feature wanting photo data starts by amending
  this section, not by joining the tables.

## §7 Open questions (none block Phase 0–1)

1. **Scale** is unknown until the Phase 1 walk reports it (guess: 50k–150k items). Thumb-cache
   sizing and Immich ML runtime follow from the real number.
2. **Takeout staging location** — local disk staging is assumed; if archives should live on the
   NAS instead, mesh reads work the same.
3. **Immich hardware** — CPU-only first; the GPU is contended by the arcade lane, so any ML
   speedup pass should be scheduled deliberately, not left always-on.
4. **Which subtrees are video-library roots** for the Jellyfin family library (whole tree vs.
   the video-heavy folders) — decidable after the ingest inventory exists.
5. Whether the loose files at the collection root should get a curation home ("Unsorted" pseudo-
   folder in the UI) — cosmetic, Phase 2.
6. **Home timezone** for §2.7's UTC→wall-clock conversion (a config value, presumably the
   household's zone) — and whether travel-heavy folders warrant GPS-based conversion from day
   one or as a later refinement.

---

## Phase 7 addendum — the Gallery shelf, as built

Written after implementation. §2.12 above is the design; this is what actually shipped, what it cost,
and the two decisions that were not obvious.

**Nothing in this phase may deploy before `scripts/photos-phase7-migration.sql` is applied.** Every
query it adds reads `[Shelf]`, and the code carries **no runtime fallback** for the column being
absent — deliberately, because a fallback is a second set of query semantics that only ever runs
during a window nobody is watching. Apply first, deploy second. The script is purely additive (three
columns, one index; no ALTER, no DROP, no data movement) and, as always in this vertical, it has never
been executed against any database. It was scaffolded on top of an unrelated `AddMusicArtistKind`
migration authored in the same working tree; the two touch disjoint tables, but
`__EFMigrationsHistory` is ordered, so that one goes first if it is still outstanding.

**The index decision.** The timeline's page query gained `AND Shelf = Timeline`, and the existing
covering index carries `Shelf` in neither its key nor its `INCLUDE` — so that predicate would have
become a residual on the hottest query in the section. The natural repair is to extend that index's
key, which is a `DROP` plus a `CREATE`, which is precisely what an additive-only migration may not
contain. The additive spelling is a SECOND covering index, keyed and `INCLUDE`-ing identically,
**filtered to `[Shelf] = 0`**: it matches the timeline/undated/person predicate exactly, it *shrinks*
as the archive grows (the same reasoning behind the three filtered ingest-queue indexes), and the
original stays for the surfaces that do not filter by shelf — the folder tree, and an admin browsing
with show-hidden on. The cost is honest and was accepted: two covering indexes mean the metadata pass
maintains both, a bounded once-per-photo write traded against an unbounded every-page read. It is a
filtered index, so writers need `SET QUOTED_IDENTIFIER ON` — a constraint this table was already
under, so Phase 7 adds no new operational rule. `PhotoAlbum.Shelf` deliberately gets **no** index:
tens of rows, and an index whose only effect is to be maintained is a cost with no reader.

**Group coherence was the non-obvious correctness bug.** The first instinct is to shelve exactly what
the member selected. That is wrong in both directions, and quietly: move only a group's master and the
copies stay on the timeline's books while collapsed behind a card that is no longer there, so the
photograph vanishes from both sections; move only a copy — which is what the folder view hands you,
since it shows every copy — and *nothing visible happens*, because that copy was already collapsed.
Either way the member presses a button and is told a lie. So both the selection bar and the CLI expand
through `PhotoDupeMasters.GroupCoherentIdsAsync`, over the same settled-group predicate `Collapsed` is
built from, and the extras are always reported. Album ENTRIES still redirect to masters, which is the
opposite motion and is correct for the opposite reason: a tag or a membership is a fact about the
photograph, while a shelf is a fact about where the photograph is filed.

**The dry run had a real gap, found by its own test.** `--dry-run` with a new `--album` reported zero
album entries, because there was no album id to add them against — i.e. it advertised a no-op for a
rule that would have created 1,609 rows. The pass now models the album as *none / pending / existing*
and counts the would-be entries in the pending case.

**Verified.** 24 new backend tests (788 total, green) and 13 new frontend tests (716 total, 55 files,
green); `npm run build` green. The CLI was smoked end to end against a **generated** SQLite fixture
built to the surveyed shape of the real tree — 2,312 files: 31 loose at the `Misc Pics` root, the five
subfolders at 221/153/261/8/17, and `SAMisc` at 1,608 plus its 1-file `NWS` corner. The real
invocation sequence reproduced every count exactly; `SA Misc` chunked into four batches with visible
per-chunk cursor progress; and the whole sequence re-run (at a *different* batch size, so chunk
boundaries could not be load-bearing) changed nothing — every write-counter absent, `already`
accounting for all 2,300 rows, albums found rather than created, no second hide. The verdict the phase
exists for, as two numbers: **the timeline went 2,312 → 12 while the folder view went 2,312 → 2,311**,
and that one is the NWS file, hidden rather than gone — an admin still sees it. No test, build or smoke
touched the NAS, the configured database, or a real photograph.

**Still open:** the migration itself, and then the real run. The exact command lines were carried in
the delivery report rather than committed here, since they name real folders.
