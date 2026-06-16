# Metadata Enrichment + TV Series / Episodes Plan

**Status:** Proposed (2026-06-15, revised). Sequenced *after* the current IMDB normalization
re-scrape (`scrape-imdb`) and Rotten Tomatoes backfill settle. Builds on the frozen-legacy-columns
convention from that effort: new data lands in **new** columns/tables; pre-scrape legacy columns
(`Runtime`, `Plot`, `Rating`, `Genre`, `Actors`, …) stay frozen.

**Scope:** (1) Capture metadata we currently fetch-and-discard or never fetch; (2) make the database
*type-aware* so we know what is a movie, a TV series, or a short; (3) model series → season →
episode so we know which episodes we hold. Episodes get their **own table**, unified with movies
under a shared **`Playable`** parent so both can carry files, playback progress, and channel slots.
Requires a second IMDB re-scrape pass that records title type and pulls episodes.

---

## 1. Goals and non-goals

Goals, in priority order:

1. **Stop discarding data we already fetch** — TMDB id, original language, country, OMDB box office.
2. **One stable re-fetch key** — persist the TMDB id so future fields (watch providers, images,
   keywords, certifications) backfill without re-running the TMDB→OMDB→IMDb→Google cascade.
3. **Type awareness** — every *title* row knows whether it is a feature, a short, a TV series, or a
   mini-series, driven by IMDB's own `titleType` (OMDB `Type` as fallback).
4. **Series/episode hierarchy** — a series is a rich title (a `Movie`); episodes live in their own
   `Episode` table and become streamable units via the `Playable` parent (§3.1).
5. **Enrich the streaming UI** — backdrops, trailers, taglines, per-file HDR/audio-format badges.

Non-goals (v1): per-country certifications beyond US MPAA; watch-provider (JustWatch) storage;
episode-level credit graphs (episodes carry lightweight metadata only — credits/genres live on the
series); automatic episode-file discovery on the NAS (Phase E, schema + scrape only here).

---

## 2. Current state (what we store vs. what we touch)

| Field | Fetched from | Stored? |
|---|---|---|
| TMDB id | TMDB (every lookup) | **No** — only `imdbID` kept |
| Original language / country | OMDB `Language` / `Country` | **No** — discarded |
| Box office / budget / revenue | OMDB `BoxOffice` (+ TMDB) | **No** |
| Tagline | TMDB / OMDB | **No** |
| Backdrop (wide hero art) | TMDB `backdrop_path` | **No** — only poster + dominant color |
| Trailer | TMDB `videos` / `YouTubeService` | **No** |
| Keywords / tags | TMDB | **No** |
| TMDB popularity / vote_count | TMDB `MovieDto` | **No** — fetched, dropped |
| **Title type** (movie/series/short) | OMDB `Type`, IMDB `titleType` | **No** — everything is a `Movie` |
| Series ↔ episode linkage, season/episode # | IMDB | **No** — no concept exists |
| HDR / audio format / tracks | Jellyfin sync | **No** — `MovieFile` has codec/resolution/size only |

Relevant invariants: `ScrapeImdbCommand` resumes on `ImdbVerifiedDate IS NULL`, never overwrites
legacy columns. The live DB has drifted from the EF snapshot before (Users IDENTITY; Viewing/
UserSettings FK behaviors) — every migration below must be checked against the **live** schema, not
just the model.

---

## 3. Schema changes

### 3.1 `Playable` — shared parent for anything streamable/schedulable (new table)

The current streaming plumbing (`MovieFile`, `MoviePlaybackProgress`, `ChannelScheduleItem`) all FK
to `Movie`. To let episodes carry files / progress / channel slots without duplicating those tables,
introduce a thin parent both movies and episodes reference:

```csharp
[Table("Playable")]
public class Playable
{
    [Key] public int Id { get; set; }       // own IDENTITY — does NOT reuse Movie.id
    public PlayableKind Kind { get; set; }   // Movie | Episode
}
```

`Movie` and `Episode` each gain a **unique `PlayableId` FK** to `Playable`. We do *not* fold
`Movie.id` into `Playable` — `Movie.id` is an existing IDENTITY referenced by ~8 tables, and IDENTITY
surgery on this DB has bitten before. A surrogate `PlayableId` on each side keeps `Movie.id` and the
metadata FKs untouched.

```csharp
// on Movie
public int? PlayableId { get; set; }   // unique; set for every Movie row by the migration
// on Episode (below) likewise
```

**What moves to `Playable` (the streamable/schedulable surface):**

| Table | Today | After |
|---|---|---|
| `MovieFile` → rename **`MediaFile`** | `MovieID → Movie.id` | `PlayableId → Playable.Id` |
| `MoviePlaybackProgress` | `MovieID → Movie.id` | `PlayableId → Playable.Id` |
| `ChannelScheduleItem` | `MovieID → Movie.id` | `PlayableId → Playable.Id` (channels can now air episodes) |

**What stays `Movie`-keyed** (title metadata — movies and *series*, not episodes): `MovieCredit`,
`MovieGenre`, `MoviePlotSummary`, `MoviePosterDetails`, `Viewing`. Episodes carry their own
lightweight metadata as columns (§3.3); episode-level credits are a non-goal. `Viewing`
(Seen/WantToWatch) stays at the movie/series level for v1 — episode watch is captured by
`MoviePlaybackProgress` (which now points at `Playable`, so episodes get cross-device resume +
auto-Seen for free). Series-level "Seen" rollup is an open question (§7).

### 3.2 `Movie` — new columns (additive, nullable)

```csharp
// ── Title classification (no tvEpisode — episodes are not movies) ──
public TitleType TitleType { get; set; } = TitleType.Movie;  // movie | short | tvShort | tvSeries | tvMiniSeries | tvSpecial

// ── Playable linkage ──
public int? PlayableId { get; set; }     // unique FK → Playable.Id

// ── Stable external keys ──
public int? TmdbId { get; set; }         // the re-fetch key; unlocks §3.6 backfills

// ── Enrichment (discarded today) ──
public string? Tagline { get; set; }
public string? OriginalLanguage { get; set; }
public string? Country { get; set; }
public long?   BudgetUsd { get; set; }
public long?   RevenueUsd { get; set; }
public decimal? TmdbPopularity { get; set; }
public int?    TmdbVoteCount { get; set; }

// ── Streaming UI art ──
public string? BackdropPath { get; set; }
public string? TrailerKey { get; set; }
```

A **series is a `Movie`** with `TitleType = tvSeries`/`tvMiniSeries`: it keeps the full poster /
credits / genres / plot graph. Its `Playable` row is optional — a series itself isn't usually
streamed; its episodes are.

### 3.3 `Episode` — new table

```csharp
[Table("Episode")]
public class Episode
{
    [Key] public int Id { get; set; }

    public int  SeriesMovieId { get; set; }      // FK → Movie.id (TitleType = tvSeries)
    [ForeignKey(nameof(SeriesMovieId))] public Movie Series { get; set; } = default!;

    public int? PlayableId { get; set; }         // unique FK → Playable.Id (Kind = Episode)
    [ForeignKey(nameof(PlayableId))] public Playable? Playable { get; set; }

    public int  SeasonNumber { get; set; }
    public int  EpisodeNumber { get; set; }

    // Lightweight episode metadata (episodes don't get the full credit graph)
    public string?   Title { get; set; }
    public string?   ImdbId { get; set; }        // episodes have their own tt id on IMDB
    public DateTime? AirDate { get; set; }
    public int?      RuntimeMinutes { get; set; }
    public string?   Plot { get; set; }
    public decimal?  ImdbRating { get; set; }
    public string?   StillPath { get; set; }     // episode thumbnail; UI falls back to series poster
}
```

Unique index on `(SeriesMovieId, SeasonNumber, EpisodeNumber)`. "How many episodes do I actually
have" = episodes whose `Playable` has a non-missing `MediaFile`; `Series.EpisodeCount` (§3.4) holds
the IMDB total for the "have 18 of 22" UI.

### 3.4 `Series` — series-level aggregates (new table, 1:1 with the tvSeries `Movie`)

```csharp
[Table("Series")]
public class Series
{
    [Key] public int MovieId { get; set; }       // = the tvSeries Movie.id
    [ForeignKey(nameof(MovieId))] public Movie Movie { get; set; } = default!;

    public int? SeasonCount { get; set; }
    public int? EpisodeCount { get; set; }        // IMDB total (may exceed episodes we hold)
    public SeriesStatus Status { get; set; }      // continuing | ended | unknown
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
    public string? Network { get; set; }
}
```

### 3.5 `MediaFile` (renamed `MovieFile`) — streaming technical detail (new nullable columns)

```csharp
public bool?   IsHdr { get; set; }
public string? HdrFormat { get; set; }     // "HDR10" | "DolbyVision" | "HLG"
public string? AudioLayout { get; set; }   // "5.1" | "7.1" | "Atmos" | "Stereo"
public int?    AudioChannels { get; set; }
public double? FrameRate { get; set; }
public int?    BitDepth { get; set; }
```

Optional `MediaFileTrack` child (one row per audio/subtitle track: index, language, codec, title,
default/forced) so browse can filter "has English audio" without a live Jellyfin call — serves the
recent channel track-picker / English-audio-default work. Defer if the inline columns suffice.

### 3.6 Deferred-but-cheap-once-`TmdbId`-exists

`Keyword`/`MovieKeyword`, production companies, per-country certifications, watch providers. Not
built now; listed so `TmdbId` is understood as the enabler.

---

## 4. Service / DTO changes

- **`TmdbApi`**: switch the detail lookup to TMDB's full movie endpoint with
  `append_to_response=videos,keywords,external_ids`. Extend `MovieDto` (or a new
  `TmdbMovieDetailDto`) for `tagline`, `belongs_to_collection`, `backdrop_path`, `budget`, `revenue`,
  `original_language`, `production_countries`, trailer key.
- **OMDB**: stop discarding `Type`, `Language`, `Country`, `BoxOffice` — `OmdbMovieDto` already parses
  them. `Type` is the coarse `TitleType` fallback when IMDB classification is absent.
- **Jellyfin sync** (`SyncJellyfinCommand`): map the MediaStreams it already reads (HDR/audio/
  framerate) into the new `MediaFile` columns; also fill episode files once episodes exist (Phase E).

---

## 5. IMDB re-scrape (the pass that ties it together)

The current scrape (`ImdbTitleScraper` → `ImdbScrapeResult` → `ImdbDataApplier`) captures runtime,
plot, MPAA, genres, cast/crew — but not title type, series status, or episodes. The second pass adds:

1. **`ImdbScrapeResult.TitleType`** — read IMDB's `titleType` (`movie`, `short`, `tvShort`,
   `tvSeries`, `tvMiniSeries`, `tvSpecial`, `video`). Drives `Movie.TitleType`; distinguishes shorts
   from features. (No `tvEpisode` on the `Movie` path — those become `Episode` rows.)
2. **Series fields** — on a `tvSeries` page: season/episode counts, start/end years, status → the
   `Series` row.
3. **Episodes** — enumerate the series' episodes (IMDB `/episodes?season=N` pages), creating an
   `Episode` row per `tt` with season/episode #, title, air date, runtime, rating. Each gets a
   `Playable` (Kind=Episode). This is the heavier sub-pass; gate it behind a `--episodes` flag so the
   movie/short/series classification pass can run first and cheaply.

**Re-run mechanics:** the command already supports `--rescrape`. To backfill `TitleType` library-wide,
either run a full `--rescrape` or add a narrower resume predicate (e.g. a new `ImdbTypeVerifiedDate`)
so we don't re-pull pages whose type we already know. Keep the gentle 2–5 s delays and resumability;
legacy columns stay frozen.

### 5.4 Response cache — fetch each page once, ever (politeness)

Today `ImdbTitleScraper.ScrapeAsync(page, …)` parses the live Playwright `page` and discards the
bytes, so every parser improvement means re-hitting IMDB for the whole library. A raw-response cache
turns future re-scrapes (the `TitleType`/episode passes above, and anything later) into **offline
re-parses with zero IMDB traffic** — you fetch each page once and re-derive fields from the cache
forever.

**Storage — local, never in git, never in prod:**

- **Bytes → gitignored local files.** Raw page HTML (gzipped), sharded by id:
  `data/imdb-cache/tt/01/33/tt0133093/title.html.gz`, `…/episodes-s1.html.gz`. `data/` is already
  gitignored, so the data never leaks. Store full HTML (not just parsed JSON-LD) so fields we don't
  yet extract remain recoverable. Capture point: `await page.ContentAsync()` right after the scraper
  loads each page.
- **Index → local SQLite sidecar** `data/imdb-cache/index.db`: `(imdbId, pageType, fetchedUtc,
  httpStatus, contentHash, relPath, etag)`. Answers "do I have it / how stale / unchanged?" without
  statting thousands of files. **Not** the product SQL DB — raw third-party HTML in tens-of-MB-to-GB
  volume has no business in prod backups/replication, and the scrape is a maintenance CLI run from one
  machine where both the index and bytes live together.

**Mechanics:**
- Scraper consults the index first: **hit + fresh (within a configurable TTL) → parse cached bytes,
  no network call.** Miss/stale → fetch, then write bytes + index row.
- Volatile fields (IMDb rating) can refresh past a short TTL while stable fields stay cached
  indefinitely; optionally send `If-Modified-Since`/ETag so a refresh that 304s costs nothing.
- A `--reparse` mode runs the applier over the cache with no browser at all — this is how the new
  `TitleType`/episode fields get backfilled without touching IMDB.

This caching is independent of the schema work and worth landing **before** the second re-scrape, so
that scrape populates the cache and never has to be repeated.

---

## 6. Phasing

- **Phase A — Enrichment columns + TmdbId.** §3.2 scalar columns (minus episode/series). Extend
  `TmdbApi`/`MovieDto` + OMDB applier. New lookups fill forward; a small backfill command tops up
  existing rows from TMDB by `imdbID`. No re-scrape, no `Playable`. Low-risk; do first.
- **Phase B — MediaFile technical detail.** §3.5 columns from Jellyfin sync; HDR/audio badges in the
  watch/stream UI. Independent of A. (Rename `MovieFile`→`MediaFile` here *or* defer the rename to C
  to avoid double-touching it.)
- **Phase C1 — Title type (low-risk metadata).** Add `TitleType`; teach the scraper (§5.1) + OMDB
  fallback; re-scrape to classify. Every `Movie` row stays a movie-shaped title (feature/short/
  series); this does **not** add non-movie rows — series are *already* in the table today (open
  question #4), just unclassified, so `TitleType` lets us finally label and filter them. Then default
  the movie-list / OData queries to `TitleType eq 'Movie'` so shorts/series don't show in the main
  grid (a one-line filter, not a structural change). Can ride alongside Phase A; gates Phase D's
  series/episode work only because it identifies the `tvSeries` rows.

  **Shorts are tagged exactly like series here** — `short`/`tvShort` classified off IMDb's
  `titleType`, hidden from the default grid, browsable as their own category. The difference from
  series is downstream, not here: a short is a **leaf title** (like a movie, no children), so it gets
  *only* this phase — no `Series`/`Episode`-style table or hierarchy (Phase D). Tag like series;
  model like a movie.
- **Phase C2 — `Playable` parent + FK cutover (the structural step).** Pure plumbing — `Movie` row
  semantics don't change; each maps 1:1 to a `Playable(Kind=Movie)`:
  1. Create `Playable`; insert one `Playable(Movie)` per existing `Movie`; add + populate
     `Movie.PlayableId`.
  2. Add `PlayableId` to `MediaFile` / `MoviePlaybackProgress` / `ChannelScheduleItem`, backfill from
     the Movie→Playable map, then retire the old `MovieID` FK columns.
  **This is the riskiest phase** — it repoints live FK columns and the code that reads them (stream
  resolver, channel scheduler, Jellyfin sync). Verify against the live schema; stage the cutover
  (add new column → backfill → flip reads → drop old) so it's reversible. The risk is the FK
  migration, not any change in what a `Movie` row means.
- **Phase D — `Series` + `Episode`.** `Series`/`Episode` tables; scraper §5.2/§5.3 (`--episodes`);
  series/episode browse UI ("Season 2 · 8 of 10"). Depends on C1 (to know the series rows) and C2
  (so episodes can be `Playable` and stream).
- **Phase E — Episode file discovery (later).** Match NAS episode files to `Episode` `Playable`s via
  `MediaFile`, tying into streaming-plan §5 — episodes then stream and can fill channels.

A, B, and C1 are independent and low-risk. **C2 (the `Playable` FK cutover) is the one structural
step** and the only place real migration risk lives; it gates D/E.

---

## 7. Open questions

1. **`Playable` FK cutover blast radius.** Repointing `MediaFile`/`MoviePlaybackProgress`/
   `ChannelScheduleItem` from `MovieID` to `PlayableId` touches the stream resolver, channel
   scheduler, sync command, and any OData/REST projection of those. Phase C needs an inventory; do the
   add→backfill→flip→drop cutover, not an in-place rename.
2. **Default-filter scope (low risk).** Shorts and series share the `Movie` table (series already do
   today), so the main grid should default to `TitleType eq 'Movie'`. Spots to touch: `/odata/Movies`,
   random picker (`RemoveFromRandom`), Viewings stats. A shared `IQueryable<Movie>` filter helper
   covers them. This is a display filter, not a structural change.
3. **Series-level Seen rollup.** `Viewing` stays movie/series-level; episode watch is only
   `MoviePlaybackProgress`. Do we synthesize a series "Seen %" from episode progress, or leave series
   Seen manual?
4. **Existing TV rows.** Some current `Movie` rows are series (imported via OMDB `Type=series`).
   Phase C reclassifies them to `tvSeries`; their episodes don't exist until Phase D's `--episodes`
   pass. Confirm that interim (series visible, no episodes) is acceptable.
5. **Language encoding** — store TMDB ISO-639-1 code, OMDB English name, or both? Code is better for
   filtering; normalize on write.
6. **Should `Viewing` also move to `Playable`?** Kept on `Movie` for v1 simplicity; revisit if
   episode-level WantToWatch is wanted.

---

## 8. Summary of new/changed types

- **New:** `Playable` entity + `PlayableKind` enum, `Episode` entity, `Series` entity, `TitleType`
  enum, `SeriesStatus` enum; optional `MediaFileTrack`, `Keyword`/`MovieKeyword`. New `DbSet`s for
  `Playable`, `Episode`, `Series`.
- **Changed:** `Movie` (+~11 columns incl. `PlayableId`, `TitleType`); `MovieFile`→`MediaFile`
  (+`PlayableId`, +6 tech columns, drop `MovieID`); `MoviePlaybackProgress` + `ChannelScheduleItem`
  (`MovieID`→`PlayableId`); `MovieDto`/new TMDB detail DTO; `ImdbScrapeResult` (+`TitleType`, series,
  episode lists), `ImdbTitleScraper`, `ImdbDataApplier`; OMDB applier; `SyncJellyfinCommand`.
- **Cache (§5.4, local-only, not in git or prod):** gitignored `data/imdb-cache/` for gzipped raw
  HTML + a SQLite `index.db` sidecar; a `--reparse` scrape mode that re-derives fields from the cache
  with no IMDB traffic. Land before the second re-scrape.
- **Migrations:** one per phase (A, B, C1, C2, D). **C2** (the `Playable` FK cutover) is the
  multi-step, staged one (add → backfill → flip → drop) for reversibility; the rest are additive.
