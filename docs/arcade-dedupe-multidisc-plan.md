# Arcade lobby: one-card-per-game + version dropdown + multi-disc

Problem: full-ingest surfaces many **effectively-duplicate cards** — the same game once per region,
revision, language, translation/trainer hack, and (worst) once per **disc**. "Final Fantasy IX"
appears 8×, "Tomb Raider" as `(v1.0)`…`(v1.6)`, "GoldenEye" as USA/Europe/Japan/`[tr de]`.

## Design (user-directed 2026-07-04)

**One card per game. Each card carries a version dropdown** listing every ROM of that game, each
entry clearly labeled with what makes it different (region, revision, edition, language, hack,
disc). Selecting an entry launches that specific ROM. This de-dupes ALL card content while keeping
every alternate reachable.

- **Cards = games**, grouped by `(System, Title)` (Title is the cleaned base name, so all regions/
  revisions/discs of a game share it). Grouping is done at **query time** — new ingests fold in
  automatically, no dedupe pass to run.
- **Filters gate cards by version existence.** A card shows iff the game has ≥1 version matching the
  active filters. Default filters = **English + non-modded** → a Japan-only game doesn't show with
  the English filter on; clearing the region filter shows every game once ("unique list of all
  cards"). The dropdown still lists ALL of a shown game's versions (so you can pick Japan from an
  English-filtered card); the **default-selected** entry is the best English release.
- **Box art shared per game** — the card requests art by a single representative version id, so all
  variants show one image and cost one fetch / one file. No wasted storage.
- **Version label** composed from the ROM tags: region · revision (`Rev B` / `v1.6`) · edition
  (`GameCube Edition`) · disc (`Disc 1`) · hack/lang. A lone clean version needs no dropdown.

"English" region set (for the default filter) = USA / World / Europe / untagged (Unknown); excludes
Japan / Asia / Other. Non-modded = `Variant = Release`.

No `IsPrimary` column is needed for this (query-time grouping); the column added in
`AddArcadeGameIsPrimary` stays dormant/reserved (kept so the EF model matches the live DB), and the
`(System, Title)` index it added powers the grouping.

## Backend (`ArcadeController.Games`)

Returns **games**, paged:
```
{ games: [ { key, title, system, artId, maxPlayers, versionCount,
             versions: [ { id, label, region, variant, year, maxPlayers } … ] } … ],
  totalCount, page, pageSize }
```
1. Age gate + row filters (system, players, search, and region/variant — default English+Release or
   the user's explicit choice) → the **match set** of qualifying rows.
2. `GROUP BY (System, Title)`, order by `MIN(SortTitle)`, page → the game keys on this page +
   `totalCount = COUNT(distinct group)`.
3. Fetch ALL age-visible versions for the paged games (superset by `System IN … AND Title IN …`,
   filtered to the exact page keys in memory) → build `versions[]`, ordered best-English-first;
   `artId` = the representative (default-selected) version's id.
Multiplayer/`maxPlayers` at the game level = max over its versions.

## Frontend (`ArcadePage`)

One `Card` per game: shared box art (`/ArcadeImage/{artId}`), a **version `Select`** (hidden when a
game has one version), and "Start room" launching `createArcadeRoom(selectedVersionId)`. The default
selection is the first (best-English) version. Live-rooms rail unchanged (rooms are per ROM).

## Multi-disc (Phase 2 — seamless disc-swap)

Discs are the one case that shouldn't be separate dropdown entries — a game's `(region, rev)` discs
fold into a single playable that swaps discs in-game:
- **ROM/JIT:** materialize an `.m3u` per multi-disc `(System, Title, region)`; `RomCache` extracts
  ALL discs + writes the `.m3u` on first play (today it extracts one archive). The version entry
  points at the `.m3u`.
- **Emulator (CloudRetro patch 0005):** pcsx_rearmed loads `.m3u` + exposes libretro Disk Control;
  patch the worker to accept a `disc` command on the data channel → `set_eject_state`/
  `set_image_index`. Add `m3u` to pcsx `roms` exts in `config.yaml`. Image rebuild (user side).
- **Wire/UI:** `cloudRetroClient.js` sends the disc command over the negotiated data channel;
  `ArcadeRoomPage` shows `Disc x/N` + a **Swap Disc** control. Feature-detect via the descriptor.

Interim (Phase 1, before `.m3u`): multi-disc discs appear as labeled dropdown entries (`USA · Disc 1`…),
launching that disc. Phase 2 folds them into one `USA` entry with in-game swap.

## Notes
- No data-layer hiding of alternates — everything stays enabled and filter-reachable. The only real
  merge is multi-disc (discs → one playable).
- Grouping is query-time; safe while the parallel ingest grows the catalog (52k+ and climbing).
- Never mass-copy discs — JIT materializes on demand (never pre-stage).
