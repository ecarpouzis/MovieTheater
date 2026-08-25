# Books vertical — data plane (R4)

The Books vertical keeps its catalog in two SQLite files owned by `MovieTheater.BooksHost` (a Windows service on the
media host, hand-deployed like the stream gateway). This directory holds the design (`v2-model.md`), its
machine-readable contract (`v2-mapping.json`), the v1 evidence (`v1-baseline-counts.md`, `v1-data-model-audit.md`)
and the migration/verify reports.

## Projects

| Project | Role |
|---|---|
| `src/MovieTheater.Books.Db` | `BooksDb` (books.db, hot) + `BooksLegsDb` (books-legs.db, offline warehouse), entities GENERATED from `v2-mapping.json` by `scripts/books/gen/gen_entities.py`, EF migrations under `Migrations/Hot` and `Migrations/Legs`, `BooksDbOptions` (the one place a file is opened; WAL + mmap/cache pragmas), `ItemFts` (the FTS5 table, raw SQL), `DerivedTables` (the registry of derived data and the job that rebuilds each) |
| `src/MovieTheater.Books` | The engine library: `Migration/` (the v1→v2 copy-transform: `V1Source`, `TargetWriter`, `Transforms`, `MigrationEngine`, one `StageUnit` per v1 table), `Resolve/` (`InsightCurrency`, `TagFolds`, `SynopsisRules`, `ItemResolver`, `SeriesResolver`, `FtsBuilder`, `ResolvePipeline`), `Verify/` (`V1Verifier`, `HotSetReplay`), and the RUNTIME surface: `Access/` (`ItemAccess`, `MaturityFilter`), `Projections/` (`ItemSummary`, `CatalogEdm`), `Controllers/` (`CatalogController`, `BrowseController`), `Services/CacheWarmupService`, `BooksServiceExtensions` (`AddBooks`/`MapBooks`) |
| `src/MovieTheater.BooksHost` | The CliFx shell (same shape as the API's `Program`): the verbs below now, the `web` service verb in R5 |
| `src/MovieTheater.Books.Tests` | xUnit over throwaway SQLite files: the model-equals-contract tripwire, the transforms, a full migrate/resume/verify/replay on a synthetic v1 built from the real v1 DDL (`Fixtures/schema-v1.sql`) |

## Verbs (`MovieTheater.BooksHost.exe <verb>`)

Paths default to the `Books` section of `appsettings.{Environment}.json` (`DbPath`, `LegsDbPath`, `V1SourcePath`,
`CalibreLinkPath`, `CacheDir`, `ReportDir`, `V1OwnerUsername`, `OwnerUserId`); every verb also takes them as options.
The owner account — the ONE standalone-site user whose activity migrates — is configuration (`--owner` /
`Books:V1OwnerUsername`), never a literal in code.

- `web` — the service verb (R5): the identity-gated host behind the site proxy — `/healthz`, `/ping`, `/media-token`, the media plane `/m/{token}/…` (thumbnails now; pages/EPUB/download in R6). See `host-deploy.md`.
- `books-db-migrate [--db] [--legs]` — create/upgrade both files to the current EF model, set WAL, seed the `DerivedTable` registry. The only way the schema ever changes.
- `books-migrate-v1 --source <frozen v1.db> --owner <name> [--owner-user-id 1] [--target] [--legs] [--calibre-link] [--cache-dir] [--report-dir] [--stage s|s/Unit] [--batch-size 5000] [--max-batches 0] [--after rowid] [--dry-run] [--reset] [--status]` — the chunked, resumable copy-transform. One batch = one page of one unit's v1 table by rowid, committed together with its `MigrationProgress` row; prints `{ processed, remaining, nextCursor }  [unit, counts]` per batch; re-running resumes; `--dry-run` reads and counts without writing; a batch that moves no cursor stops the run. Rows the contract cannot place are counted as `unmapped`, never dropped silently; orphan series insights are exported to `<report-dir>/orphan-insights.json`.
- `books-resolve [--db] [--batch-size] [--fts]` — rebuild the derived columns from the hot file's inputs: insight currency (`IsCurrent`), the AI tag fold, `Series.Resolved*`, `Item.Resolved*` (chunked by id), then `ItemFts`. The same code the migration's `resolve`/`fts` stages run.
- `books-verify-v1 --source <frozen v1.db> [--target] [--legs] [--report] [--replay]` — independent audit: integrity, id-set preservation, per-table counts (scoped the way the units guard), the item→current-insight edge, the owner's activity, no other user copied, the series-resolution recompute diff (0 = the port reproduces v1's derivation), and with `--replay` the hot-set query replay. Non-zero exit on any failed check or flagged plan.
- `books-replay-hot-set [--db] [--report]` — time the standalone site's hot query set over books.db (facets, group heads/bands, catalog sorts, home rails, kids, novels, FTS) and read each `EXPLAIN QUERY PLAN`; flags full scans of large tables and `TEMP B-TREE` sorts.

## Runtime endpoints (R6 slice 1 — catalog & browse)

Served by the `web` verb from `MovieTheater.Books` (`AddBooks()` / `MapBooks()`); the host stays a thin shell.
Every route sits under the host's fallback policy, so the site's signed identity header is required. Through the
site proxy they appear under `/API/Books/…`.

| Route | What it is |
|---|---|
| `GET /odata/catalog` | The flat catalog of `ItemSummary` rows. Query-options-only OData (the site's own mode — `[EnableQuery]`, no EDM route, no `$metadata`): `$filter`, `$orderby`, `$select`, `$skip`, `$top` (PageSize 120, MaxTop 500). Custom params: `q=` (FTS5), `directory=<folderId>` (the Directory drill — shadow duplicates included), `kind=comic\|book` |
| `GET /browse/facets` | Every facet list with counts: series, publishers, decades, events, franchises, collections, authors, artists, tags |
| `GET /browse/facet-options?field=authors\|artists\|tags&q=&skip=&top=` | The paginated, searchable long tail of one facet |
| `GET /browse/groups?groupBy=series\|publisher\|decade\|collection\|franchise` | Items grouped server-side, paginated BY GROUP (`groupsSkip/groupsTop`, `perGroupSkip/perGroupTop`, `orderby`, `q`, `$filter`, `subGroupBy=series`, `singleGroupKey`) |
| `GET /browse/group-letters?groupBy=` | `{ totalGroups, letters: [{letter, firstIndex}] }` for the A–Z rail. Shares the heads cache with `/browse/groups` |
| `GET /browse/groups/{groupBy}/{key}/items?skip=&top=` | Band continuation: more items inside one group, same sort |
| `GET /browse/series/{id}/library-rating` | `{ rating, note }` for the series modal's chip; 200 with nulls when unrated |

Contract notes worth knowing before writing a client:

- **`$filter` / `$orderby` use the camelCase JSON names** (`year eq 1987`), on both `/odata/catalog` and
  `/browse/groups` — one shared EDM (`CatalogEdm`) guarantees it. `$select` is the exception: OData's select wrapper
  emits PascalCase keys, so prefer asking for the whole row.
- **`$count=true` answers in an `X-Total-Count` header**, not in an `@odata.count` envelope: that envelope is written
  by the OData output formatter, which only engages for an EDM-routed endpoint. The header is computed through the
  same parser, so it honours `$filter`. It costs one extra COUNT — ask for it on the first page only.
- **The projection joins `Item` + `Series` only.** Raw provider fields (ComicVine, LOCG, MangaUpdates, the embedded
  ComicInfo block, the current insight's prose) come from the item detail endpoint in slice 2.
- **Per-user marks** (`wantToReadOnly`, `readOnly`) are accepted and ignored until slice 3.
- Facet and group-head payloads are memory-cached per `userId:ceiling:isAdmin` — 48 h for unfiltered signatures,
  20 min for ad-hoc search/filter ones. `CacheWarmupService` keeps the unfiltered ones hot: it polls
  `PRAGMA data_version` on a dedicated read connection, re-fingerprints the catalog tables only when something
  committed, and then invokes the real controller actions for every `KnownIdentity` row (startup delay 5 s, poll
  60 s, heartbeat 6 h; one log line per pass).

## Rules the code enforces

- The EF model equals the contract (`ModelMatchesMappingTests`); edit `v2-mapping.json` (via `scripts/books/census/v2_mapping_spec.py`), regenerate, add a migration — never the entities by hand.
- Ids are preserved (`Item.Id == Comics.Id`, `Series`, `Folder`, `Publisher`) so the 141k thumbnail files and every deep link carry over.
- Derived data is rebuilt by its registered job, never hand-edited (`DerivedTables`); the browse projection joins `Item` + `Series` only.
- No synopsis text is copied: `Item.ResolvedSynopsisSource` names the leg that won; the text is read from that leg's table (and indexed into `ItemFts`) at use.
- The v1 file is opened read-only; the live standalone site is never touched by any verb.
