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
- `books-thumbs [--missing] [--batch-size 200] [--max-batches N] [--reset] [--status]` — generate the MISSING cover thumbnails (`{itemId}.webp` under `Books:CacheDir`), chunked by item id and resumable; prints `{ processed, remaining, nextCursor, failed }` per batch. One mode only — see the slice 2 section below.
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

## Runtime endpoints (R6 slice 3 — user activity)

The user's own half of the vertical: where they are in a book, what they marked, what their shelf looks like and
what to read next. Everything here is keyed by `BooksIdentity.UserId(User)` — an int, never a username — and lands
in exactly two tables: `UserItemState` (one row per user × item: position AND want/favourite/hidden) and
`GroupMark` (one row per user × group type × key). Per-item user ratings are `Rating(TargetKind=Item, Source=User)`
rows, so a rating keeps its provenance like every other rating in v2. The standalone site's three parallel
want/read stores are gone; so is its per-folder ACL.

| Route | What it is |
|---|---|
| `GET /positions/{itemId}` | The reading position. Returns a start-of-book default (`lastPage: 0`, `status: "unread"`) for a book never opened — **never a 404** for an item the caller may open |
| `PUT /positions/{itemId}` | Upsert. Body `{ lastPage?, lastSpineItemIndex?, lastScrollPercent? }`; a body with none of them is a "touch" |
| `GET /positions/history?status=&skip=&top=` | The activity list, newest first, joined to `ItemSummary`. `status` = `opened` (default: in progress OR finished) \| `inprogress` \| `finished` \| `unread` \| `all` |
| `POST /positions/{itemId}/hide` | Drop the item off Last opened without unmarking it |
| `DELETE /positions/{itemId}` | Reset the POSITION only — want-to-read and favourite survive |
| `GET /marks/items?kind=want\|favorite\|read&skip=&top=` | The marked items, newest first, with their `ItemSummary` and the user's rating |
| `PUT /marks/items/{itemId}` | Body `{ wantToRead?, favorite?, rating? }` — tri-state (see below) |
| `DELETE /marks/items/{itemId}/{kind}` | Clear one mark: `want` \| `favorite` \| `rating` |
| `GET /marks/groups?groupType=series\|volume\|collection\|publisher\|decade` | The user's group marks of one type (series carry their resolved label) |
| `PUT /marks/groups/{groupType}/{key}` | Body `{ isRead?, wantToRead?, isFavorite?, rating?, notes? }` |
| `DELETE /marks/groups/{groupType}/{key}` | Remove the mark |
| `POST /marks/groups/batch` | `{ items: [{ groupType, groupKey }] }` → `{ "series::1": {…} }`. One round trip for a whole band of group heads |
| `GET /shelf/series?kind=read\|want&skip=&top=` | The shelved series as cards: name, issues held, issues finished, cover item, run years, publisher, the mark's own flags |
| `GET /shelf/continue?skip=&top=` | In-progress items, most recent first |
| `GET /shelf/last-opened?skip=&top=` | Opened items (in progress or finished), most recent first, minus the dismissed |
| `GET /suggestions?count=&seed=` | The recommender. `seed` makes a run reproducible |

Contract notes worth knowing before writing a client:

- **`lastPage: -1` is the ONLY signal that finishes a book.** It is the Read button — an explicit act. Reaching
  (or passing) the last page never auto-finishes; page `0` with nothing else is "opened the cover", which is
  `unread`, not progress. An EPUB write (a `lastSpineItemIndex`) is always progress. The stored `lastPage` after a
  finish is the book's LAST page, not `-1`, so a reader that reopens it lands at the end.
- **Every position write clears `HiddenFromHistory`** — reading something undoes a prior dismissal. A MARK write
  deliberately does not: wanting to read something is not reading it, and a want toggle must never drag a
  dismissed book back onto Last opened. A position RESET does not clear it either.
- **Tri-state fields.** `rating` and `notes` are sent as raw JSON, not as nullable scalars: **absent** means "leave
  it alone", **`null`** (or `""` for notes) means "remove it", a value means "set it". Without that distinction
  there is no way to delete a rating.
- **Series group keys are `SeriesId` strings and are validated** (404 for an unknown series, 400 for a name). A
  name-keyed mark detaches the next time `books-resolve-series` runs. Other group types take free-form keys.
- **Marking a series read fans out to its issues** — that is what makes the shelf's "3 / 12 read" mean anything.
  The fan-out is bounded and resumable like every bulk job here: at most 500 issues per call, already-finished
  issues skipped, and the response carries `issuesMarked` / `issuesRemaining` so the caller re-PUTs until
  remaining is 0. Re-running is idempotent.
- **List surfaces are gated; the by-id surfaces are gated slightly differently.** A history, a shelf, a mark list
  or a suggestion never shows a shadow duplicate or an above-ceiling item — not even in the user's own activity.
  A by-id position write is directory-tolerant (`IsExcluded && KeepInDirectory` stays writable) because the
  Directory drill genuinely lists that file and the reader can open it. Both always gate on maturity.
- **Every list is paged** (`skip` / `top`, `top` capped at 200) and answers `{ totalCount, skip, top, … }`.
- The shelf's `issueCount` is what the LIBRARY holds (and can be gated away); `seriesIssueCount` is the run's own
  published total. A progress bar must divide by the first.
- Suggestions port the standalone `suggestions-algorithm` weight for weight, on `SeriesId` keys instead of name
  strings and without the dead `SeriesUserLists` signal. The derived half of its input (current series insights,
  `SeriesTag(Source=AI)`, per-series publishers) is identical for every caller and is memory-cached for 20 min;
  only the user's signals and the gated candidate set are per request. Ties break on the series id, so `?seed=`
  fully determines the result.
- `UserActivityQueries` (`MarkedItemIds`, `SeriesProgress`, `ReadSeriesIds`, `WantedSeriesIds`) is what the browse
  layer calls to honour `wantToReadOnly` / `readOnly` and to decorate group heads — static, no DI, composed into
  the caller's own query.

## Runtime endpoints (R6 slice 2 — items, folders, readers, media)

Served by the `web` verb from `MovieTheater.Books`, like slice 1: through the site proxy these appear under
`/API/Books/…`, and the JSON routes all sit under the host's fallback policy (the signed identity header is
required). The BYTE routes are different and deliberately so — see *The media plane* below.

| Route | What it is |
|---|---|
| `GET /items/{id}` | The item modal's whole payload: the `ItemSummary` every list surface already sent, plus the provenance blocks — the raw embedded ComicInfo, the parse pipeline's reading with a source per field, the series' ComicVine volume / external work / MangaUpdates facts, the item's ComicVine issue, the LOCG record (High or Medium links only), the current item and series insights with their prose, score, attribution and tags, the reading-order entry, the containment node, the collected-edition spans, and the credit / tag / provider-link rows with their sources |
| `GET /items/{id}/next` · `/prev` | The next or previous item to read. `{ via, item }` — `via: "readingOrder"` when the derived per-series order answered, `"id"` when it fell back to the next item id in the same series. 204 when there is none |
| `GET /items/{id}/pages/{n}/text-regions` | Bubble Zoom: `{ regions: [...] }` with tight and hit boxes normalized to 0–1 of the page. Best effort — past the end of a book, an unreadable file, or a format with no reader all answer an empty list |
| `POST /thumbs/batch` | `{ ids, mediaToken? }` in; a map of id to `{ url, etag }` (or null) out. Up to 500 ids. Reports what is on disk; it never generates |
| `GET /items/random?kind=` | One item, uniformly at random from what the caller may see (picked by offset, not by sorting on a random key) |
| `GET /items/latest?kind=&skip=&top=` | Most recently indexed first, id as the tiebreaker |
| `GET /items/featured?kind=&count=&seed=` | A shuffled handful of the best-rated, topped up with the newest. `seed` makes the shuffle reproducible |
| `GET /library/{kind}/publishers` | Publisher names with item counts, ids, full names and a `firstLetter` for an A–Z rail |
| `GET /library/{kind}/events` | Distinct crossover / event names with their counts |
| `GET /library/{kind}/folders?parentId=&countMode=` | The library roots, or one level of the folder tree. `countMode=subtreeItems` swaps the direct-child count for the subtree item count |
| `GET /folders/{id}?skip=&top=&orderby=` | One folder: its child folders plus the items physically inside it (paged, name-ordered by default) |
| `GET /folders/{id}/parent` | The folder one level up, for a breadcrumb. 200 with a null `parentId` at a root |
| `GET /folders/{id}/icon-info` | Whether a hand-set collection icon exists and where its bytes are. Uploading one is slice 5 (admin) |
| `GET /epub/{id}/spine` | `{ count, fixedLayout, direction, items }` — the reading order plus the two facts that decide the render mode |
| `GET /epub/{id}/toc` | The flattened table of contents, each entry carrying the **spine index** it jumps to |
| `GET /epub/{id}/chapters` | The spine with TOC labels applied and a resource URL per document (no HTML — a novel's full text is megabytes) |
| `GET /epub/{id}/chapters/{spineIndex}` | One spine document's HTML, served as `text/html` |

### The media plane (`/m/{token}/…`)

| Route | What it is |
|---|---|
| `GET /m/{token}/thumbs/{id}.webp` | The cached cover. **Zero database queries** |
| `GET /m/{token}/folders/{id}/icon` | The collection icon (`f_{id}.jpg`). Zero queries |
| `GET /m/{token}/pages/{id}/{n}?maxWidth=` | One page as JPEG. `maxWidth` is the client's viewport in device pixels |
| `GET /m/{token}/epub/{id}/{*path}` | One EPUB resource (image, font, stylesheet) with its real content type |
| `GET /m/{token}/download/{id}` | The original file, with Range support and a content-disposition file name |

Contract notes worth knowing before writing a client:

- **The token in the path IS the credential.** An image tag or a download link cannot set a header, so these
  routes are anonymous to the framework and authenticate themselves. A bad token is **403**; an item the token
  may not see is **404** — the same answer as an item that does not exist.
- **One authorization, two callers.** `ItemAccess.GetAuthorizedItemAsync` is the only gate: exclusion plus the
  maturity ceiling in a single indexed read. Every by-id JSON route calls it, and the media plane calls it via
  `MediaAccess`, which rebuilds a principal from the token's payload. A token can never widen what its holder may
  fetch — it carries the ceiling and admin flag the identity header established.
- **404 everywhere, never 403, for an item.** Item ids are sequential; a 403 would let a gated account map the
  library it is gated out of. Absent and forbidden are indistinguishable from outside.
- **Thumbnails are the one zero-DB path**, on purpose: a leaked id reveals at most a cover the holder was already
  shown, and an indexed read per card would put ~120 queries in front of every grid page.
- **A page index is an ordinal position in a fixed ordering** — `ArchiveEntryOrder` (ordinal, case-insensitive,
  on the entry's full path). Every `Item.PageCount` and every migrated `UserItemState.LastPage` was produced
  under it; changing it re-indexes the library's saved reading positions.
- **Page 0 of an EPUB means the COVER**, not spine page 0 (a novel's first spine document is routinely a title
  page). The page-byte cache and the ETag keep the two apart.
- **Readers route by magic bytes, not extension** (`ArchiveFormatSniffer`): a RAR named `.cbz` opens. `.pdf`,
  `.epub` and `.mobi` are trusted as declared — an EPUB is a ZIP but needs the EPUB reader.
- **Every response is `Cache-Control: private`** with an ETag derived from the catalog row alone (id, page,
  `maxWidth`, file mtime), so a revalidation is answered before the archive is opened.
- The item payload's `pagesUrlTemplate` leaves a `{page}` placeholder for the client to substitute — one link
  instead of one per page.

### Thumbnails

`{itemId}.webp`, 720×440, WebP lossy method 4, under `Books:CacheDir`. Ids were preserved by the migration, so
the 141k thumbnails the standalone site already generated are valid as-is and nothing regenerates them. The
spread rule runs before measuring: page 0 wider than 1.15 : 1 is a back-front wraparound and is cropped to its
right half, and it is the CROPPED size that lands in `ItemState.CoverWidth/Height`.

`books-thumbs [--missing] [--batch-size 200] [--max-batches N] [--reset] [--status]` is the CLI driver. There is
**one mode** and it is "generate missing" — "regenerate all" is not a mode; a rebuild is delete-then-generate-
missing. The job is chunked, resumable and observable: the cursor is `Item.Id` (the batch query's own ordering),
persisted in `SystemState` and committed *with* the batch's writes, so a kill costs at most one batch and a
re-run continues. Each batch prints `{ processed, remaining, nextCursor, failed }`; the loop lives in the verb,
with a stop on a batch that moves no cursor. A missing or unreadable file is **recorded** in `ItemState`
(`ThumbnailError`, and `IsBroken` only when the archive itself was the problem), never thrown — one bad file must
not stop a job with 141k to walk. `ItemState` is the only table it writes; the library file is opened read-only.

### Configuration (`Books:` section, host-only)

`CacheDir`, `PublicBaseUrl`, `MediaTokenSecret` (already present from R5), plus `ArchiveCacheDir`,
`ArchiveCacheGb` (0 = the whole-archive copy cache is off), `PageJpegQuality`, `PageCacheLimitMb`,
`ThumbnailQuality`, `SevenZipPath` (null = probe the usual install paths), `EnableTextRegions`. `WebCommand`
hands them to `AddBooks` as `BooksOptions`; a null path degrades that feature and never fails startup.

Two deliberate departures from the standalone site, stated because they change behaviour:

1. **TOC entries now resolve to spine indices when the OPF lives in a subfolder.** A spine entry's key is
   OPF-relative (`ch1.xhtml`) while a nav link's target is container-relative (`OEBPS/ch1.xhtml`); the old code
   indexed only the key, so in that (very common) layout every TOC entry resolved to `-1` and the table of
   contents was unclickable. The port indexes both names, with an unambiguous leaf-name fallback. A fixture EPUB
   pins it.
2. **Page ordering is pinned in one place and is still ordinal**, not the numeric-aware "natural" sort the slice
   brief asked for — see `ArchiveEntryOrder`. Natural sort re-orders any archive whose page numbers are not
   consistently zero-padded, which would silently move every saved reading position in those books. Changing it
   is a one-line edit there, plus a `PageCount` re-scan and a decision about existing positions.
