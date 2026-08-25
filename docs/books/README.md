# Books vertical — data plane (R4)

The Books vertical keeps its catalog in two SQLite files owned by `MovieTheater.BooksHost` (a Windows service on the
media host, hand-deployed like the stream gateway). This directory holds the design (`v2-model.md`), its
machine-readable contract (`v2-mapping.json`), the v1 evidence (`v1-baseline-counts.md`, `v1-data-model-audit.md`)
and the migration/verify reports.

## Projects

| Project | Role |
|---|---|
| `src/MovieTheater.Books.Db` | `BooksDb` (books.db, hot) + `BooksLegsDb` (books-legs.db, offline warehouse), entities GENERATED from `v2-mapping.json` by `scripts/books/gen/gen_entities.py`, EF migrations under `Migrations/Hot` and `Migrations/Legs`, `BooksDbOptions` (the one place a file is opened; WAL + mmap/cache pragmas), `ItemFts` (the FTS5 table, raw SQL), `DerivedTables` (the registry of derived data and the job that rebuilds each) |
| `src/MovieTheater.Books` | The engine library: `Migration/` (the v1→v2 copy-transform: `V1Source`, `TargetWriter`, `Transforms`, `MigrationEngine`, one `StageUnit` per v1 table), `Resolve/` (`InsightCurrency`, `TagFolds`, `SynopsisRules`, `ItemResolver`, `SeriesResolver`, `FtsBuilder`, `ResolvePipeline`), `Verify/` (`V1Verifier`, `HotSetReplay`) |
| `src/MovieTheater.BooksHost` | The CliFx shell (same shape as the API's `Program`): the verbs below now, the `web` service verb in R5 |
| `src/MovieTheater.Books.Tests` | xUnit over throwaway SQLite files: the model-equals-contract tripwire, the transforms, a full migrate/resume/verify/replay on a synthetic v1 built from the real v1 DDL (`Fixtures/schema-v1.sql`) |

## Verbs (`MovieTheater.BooksHost.exe <verb>`)

Paths default to the `Books` section of `appsettings.{Environment}.json` (`DbPath`, `LegsDbPath`, `V1SourcePath`,
`CalibreLinkPath`, `CacheDir`, `ReportDir`, `V1OwnerUsername`, `OwnerUserId`); every verb also takes them as options.
The owner account — the ONE standalone-site user whose activity migrates — is configuration (`--owner` /
`Books:V1OwnerUsername`), never a literal in code.

- `books-db-migrate [--db] [--legs]` — create/upgrade both files to the current EF model, set WAL, seed the `DerivedTable` registry. The only way the schema ever changes.
- `books-migrate-v1 --source <frozen v1.db> --owner <name> [--owner-user-id 1] [--target] [--legs] [--calibre-link] [--cache-dir] [--report-dir] [--stage s|s/Unit] [--batch-size 5000] [--max-batches 0] [--after rowid] [--dry-run] [--reset] [--status]` — the chunked, resumable copy-transform. One batch = one page of one unit's v1 table by rowid, committed together with its `MigrationProgress` row; prints `{ processed, remaining, nextCursor }  [unit, counts]` per batch; re-running resumes; `--dry-run` reads and counts without writing; a batch that moves no cursor stops the run. Rows the contract cannot place are counted as `unmapped`, never dropped silently; orphan series insights are exported to `<report-dir>/orphan-insights.json`.
- `books-resolve [--db] [--batch-size] [--fts]` — rebuild the derived columns from the hot file's inputs: insight currency (`IsCurrent`), the AI tag fold, `Series.Resolved*`, `Item.Resolved*` (chunked by id), then `ItemFts`. The same code the migration's `resolve`/`fts` stages run.
- `books-verify-v1 --source <frozen v1.db> [--target] [--legs] [--report] [--replay]` — independent audit: integrity, id-set preservation, per-table counts (scoped the way the units guard), the item→current-insight edge, the owner's activity, no other user copied, the series-resolution recompute diff (0 = the port reproduces v1's derivation), and with `--replay` the hot-set query replay. Non-zero exit on any failed check or flagged plan.
- `books-replay-hot-set [--db] [--report]` — time the standalone site's hot query set over books.db (facets, group heads/bands, catalog sorts, home rails, kids, novels, FTS) and read each `EXPLAIN QUERY PLAN`; flags full scans of large tables and `TEMP B-TREE` sorts.

## Rules the code enforces

- The EF model equals the contract (`ModelMatchesMappingTests`); edit `v2-mapping.json` (via `scripts/books/census/v2_mapping_spec.py`), regenerate, add a migration — never the entities by hand.
- Ids are preserved (`Item.Id == Comics.Id`, `Series`, `Folder`, `Publisher`) so the 141k thumbnail files and every deep link carry over.
- Derived data is rebuilt by its registered job, never hand-edited (`DerivedTables`); the browse projection joins `Item` + `Series` only.
- No synopsis text is copied: `Item.ResolvedSynopsisSource` names the leg that won; the text is read from that leg's table (and indexed into `ItemFts`) at use.
- The v1 file is opened read-only; the live standalone site is never touched by any verb.
