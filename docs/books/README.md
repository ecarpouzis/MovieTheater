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
| `GET /browse/series/{id}/run` | (R8) Every visible issue of one series in reading order, each row `{ item, readingOrder?, collection? }` — the reading-order and containment blocks the flat projection omits (the series modal's smart reading list, the shelf drawer) |
| exact facet params (R8) | `author=`, `artist=`, `tag=`, `event=` and their excludes `exAuthor= exArtist= exTag= exEvent=` — REPEATABLE query params accepted by `/odata/catalog` and the three `/browse/*` group endpoints. They filter on the ROWS the facets count (`ItemCredit` by role, `ItemTag` + `SeriesTag`, `ComicDetail.EventName`): OR within a param, AND across, exact never substring; tags take the facets' `category:value` spelling. See `Access/ExactFilters.cs`. `orderby=reading` sorts a series band by `readIndex` |

Contract notes worth knowing before writing a client:

- **`$filter` / `$orderby` use the camelCase JSON names** (`year eq 1987`), on both `/odata/catalog` and
  `/browse/groups` — one shared EDM (`CatalogEdm`) guarantees it. `$select` is the exception: OData's select wrapper
  emits PascalCase keys, so prefer asking for the whole row.
- **`$count=true` answers in an `X-Total-Count` header**, not in an `@odata.count` envelope: that envelope is written
  by the OData output formatter, which only engages for an EDM-routed endpoint. The header is computed through the
  same parser, so it honours `$filter`. It costs one extra COUNT — ask for it on the first page only.
- **The projection joins `Item` + `Series` only.** Raw provider fields (ComicVine, LOCG, MangaUpdates, the embedded
  ComicInfo block, the current insight's prose) come from the item detail endpoint in slice 2.
- **Per-user marks** (`wantToReadOnly`, `readOnly`) restrict the browse as of slice 4 — see that section.
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
| `GET /shelf/series/{id}/progress` | (R8) `{ seriesId, total, finishedCount, finishedIds, inProgressIds }` — the user's state inside one series, for the drawer's done-ticks |
| `GET /marks/items/{itemId}` | (R8) One item's marks for the caller (defaults when unmarked; 404 only when the item is not visible) |
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
  name-keyed mark detaches the next time `books-resolve --series` runs. Other group types take free-form keys.
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

## Runtime endpoints (R6 slice 6 — OPDS)

The e-reader surface. Chunky, Panels, KyBook, Moon+ Reader, Aldiko and Calibre all speak OPDS, so this one route
set turns the library into a shelf inside every reading app — with **page streaming** (OPDS-PSE), which is the
only way a 200 MB collected edition is readable over a phone connection.

**The URL scheme, and where the password is checked.** An e-reader authenticates with HTTP **Basic**, and that is
verified at the **site** (the pod), which then forwards `/opds/{**}` — prefix KEPT — to this host with the same
signed identity header every other Books route already rides. So:

| Where | URL | Auth |
|---|---|---|
| What the user types into a reader | `https://<site>/opds` | HTTP Basic, checked at the pod |
| What this host answers | `/opds/…` | the signed identity header, under the host's fallback policy |

`OpdsController` therefore parses **no credentials of its own** — it is an ordinary identity-gated controller, and
`User` is the site's user exactly as it is in `ItemsController`. `GET /opds/ping` stays the host's own minimal-API
seam proof (a literal route segment outranks the controller's `{category}` parameter, so the two coexist).

| Route | What it is |
|---|---|
| `GET /opds` | The navigation feed every reader fetches first. One entry per shelf, plus the OpenSearch link |
| `GET /opds/{category}?page=&key=` | One shelf. `page` is **1-based**; 50 entries a page. Categories: `recent`, `comics`, `books`, `series`, `publishers`, `publisher` (needs `key=`), `kids`, `want-to-read`, `in-progress` |
| `GET /opds/series/{id}?page=` | One series' issues **in reading order** (the derived `ReadIndex`, id after that) |
| `GET /opds/search?q=&page=` | The same FTS5 index and the same escaping the web catalog's `q=` uses |
| `GET /opds/opensearch.xml` | The OpenSearch description — what puts a search box in the reader |
| `GET /opds/pages/{id}/{pageNumber}` | The OPDS-PSE page target. 1-based; redirects to the media plane (see below) |

**Setting up an e-reader (the one-liner):** add an OPDS/catalog source pointing at `https://<site>/opds` with the
site username and password — every shelf, cover, download and page-by-page stream follows from that one URL.

Contract notes worth knowing before pointing a reader at it:

- **Two origins, deliberately.** FEED links are built from the SITE origin (`Opds:SiteBaseUrl`) because that is
  where an e-reader's Basic credentials are verified; BYTE links (cover, download) point straight at this host's
  media plane with a minted capability token, exactly like the SPA's. Unconfigured, the feed base falls back to
  the forwarded origin (`X-Forwarded-Proto`/`-Host`, which the site's proxy stamps) and then to the request's own
  origin — never to the documented `https://<site>` placeholder, which would hand every reader a dead link.
- **The PSE page link goes through `/opds/pages/…`, not straight to the media plane**, for two reasons that are
  each a silent failure otherwise: PSE's `{pageNumber}` is **1-based** while the media plane's page index is
  **0-based** (every page would be off by one and the last page a 404), and a media token lives 12 hours while an
  e-reader keeps a cached feed for months (a token baked into the feed would stream today and fail next week).
  That route converts the index, mints a fresh token, and 302s to the media plane — the bytes never travel
  through the catalog path.
- **`pse:count` and `pse:lastRead`.** A stream link needs BOTH a `{pageNumber}` template AND a `pse:count` in a
  declared namespace or every client ignores it and downloads the whole archive; a row with no indexed page count
  gets no stream link at all. `pse:lastRead` is 1-based: a finished book reports its last page, an in-progress one
  reports where it stopped, an untouched one reports nothing (`lastRead=1` everywhere makes every cover look
  half-read).
- **The gate is the same one everything else uses.** Every feed starts at `ExcludeHidden().ApplyMaturity(ceiling)`,
  so shadow duplicates never appear and a restricted account cannot enumerate through a reader app what the web
  catalog hides from it. That hole was real on the standalone site — it enforced folder authorisation only. The
  Kids shelf is the ceiling-0 view for ANY caller: a shelf, not a permission.
- **A shelf that would open empty is not advertised.** The root feed writes an entry only when it leads somewhere:
  no books ⇒ no Books shelf, no user id ⇒ no personal shelves. Unknown category, personal shelf without an
  identity, publisher drill without a key, and a series the caller cannot see are all **404** — absent and
  forbidden look the same from outside.
- **Configuration (`Opds:` section, host-only, all optional):** `SiteBaseUrl` (set this in production — the site
  origin, e.g. `https://<site>`), `PageSize` (50), `Title` (the catalog's name in the reader's library list),
  `Enabled` (default **on** — the site-wide policy is that a lever ships enabled and is opted out of).
  `Books:SiteBaseUrl` / `Books:EnableOpds` are accepted as aliases. No service registration was needed: the
  controller reads these per request from `IConfiguration` and constructs `OpdsFeedService` itself, so
  `BooksServiceExtensions` is untouched by this slice.
- **UTF-8, no BOM.** `Utf8StringWriter` exists because `StringWriter` hardcodes UTF-16 and `XmlWriter` takes the
  DECLARED encoding from the writer, not from `XmlWriterSettings` — a `utf-16` prolog on UTF-8 bytes entitles a
  conforming parser to reject the feed, and some readers do.

## Runtime endpoints (R6 slice 4 — explore, kids, novels)

The section's front page, the kids view, and the prose shelf. The standalone site's `ComicsController.GetHome`,
`KidsController` and `BooksController`, ported onto v2 — with Home's payload re-shaped into the **site-wide
Explore envelope** (below), because Explore is one contract for every MovieTheater section and Books is only the
first section to have a server for it.

| Route | What it is |
|---|---|
| `GET /explore?kind=comic\|book&seed=` | The section's Explore payload: `{ spotlight, rails, seed }` |
| `GET /explore/kids?seed=` | The kids landing, same envelope, ceiling forced to 0 |
| `GET /kids/browse?groupBy=series&groupsSkip=&groupsTop=&perGroupTop=` | Kid-safe shelves — one group per kid-clear series (`PerSeries` 40, `MaxSeries` 160), plus a trailing `books` group |
| `GET /kids/series/{id}/items?skip=&top=` | One kid-safe shelf's issues; 404 when the series is not kid-clear |
| `GET /novels?author=&series=&publisher=&decade=&tag=&q=&skip=&top=&orderby=&excludeTag=&minRating=&unknown=` | The books list: `{ total, skip, top, items, covers, maturity }` — (R8) `excludeTag` NOT EXISTS in the `tag` spelling, `minRating` floors the 0–100 rating, `unknown=true` = only books with no current insight; `maturity` = the current insight's 0–3 per row (null when unrated) |
| `GET /novels/facets` | `{ authors, series, publishers, decades, tags }` with counts, over the gated set |
| `GET /novels/{id}` | The same `ItemDetail` `/items/{id}` returns (same builder), 404 for anything that is not a visible book |

### `ExploreResponse` — **this is the site-wide Explore contract**

Every section's Explore endpoint returns it: Movies, Music, Arcade, Photos and Boardgames answer the same shape,
and the SPA's Explore tab is one component for all of them. Nothing in it may grow a field only Books could fill.
The C# side is `Projections/CardItem.cs`; its TypeScript twin is `src/ui/src/catalog/types.ts` (`CardItem`,
`ExploreRail`, `ExploreResponse`) — the two are meant to be diffable by eye.

```
{ spotlight: CardItem[],
  rails: [{ key, title, kind: 'strip'|'wall'|'grid', items: CardItem[], more?: { href } }],
  seed }
```

`CardItem` = `{ kind: 'comic'|'book'|'series', id, key: "{kind}:{id}", title, subtitle?, label?, year?, aspect,
imageUrl, imageThumbUrl, hue?, rating?, badges?, groupKey?, sortKey, raw }`.

- **`key`, not `id`, is what a list keys on** — ids collide across kinds (a comic 7 and a book 7 both exist).
- **`raw` is the section's own row, untouched** (an `ItemSummary` for an item card; the series facts plus its
  cover item for a series card), so a section's modal needs no second fetch and the views never read it.
- **`aspect`** is the cover's true width/height, `0.66` when unknown. **`sortKey`** is the value the rail ordered
  by (a rating, an `IndexedAt`, a series name), so a view can show a "you are here" without knowing the query.
- **`imageUrl` and `imageThumbUrl` are the same URL**: the generated 720×440 WebP IS the cover the site shows,
  there is no second rendition. They are minted server-side with a media token for the CALLER's identity (same
  ceiling, same admin flag — a token can never widen what its holder may fetch), so a card arrives ready to
  render in one round trip. A URL is minted whether or not the file exists; a missing thumbnail answers 404 and
  the client's fallback art covers it. `POST /thumbs/batch` remains the surface that reports existence.
- **An empty rail is never sent.** A heading over a blank row is worse than no heading, so `top-series` and
  `collected-editions` are simply ABSENT for `kind=book` — series identity and containment are the comics spine.
- `more.href` is relative to the Books API root and is the browse URL that lists the rail fully. It is **omitted
  when the rail's rule is not expressible in the browse vocabulary** — `collected-editions` has no `more`,
  because containment is not a `$filter` and a link that quietly led somewhere else would be worse than none.

### The rails, and where their numbers come from

Ported from `GetHome` with the thresholds, the pools and the per-rail seed salts unchanged (`ExploreController`'s
constants name every one of them):

| Rail | Rule |
|---|---|
| `spotlight` | Rated >= **75** and carrying editorial prose, one per series, top **300** ranked -> **6** picked |
| `top-series` | `Series.ResolvedRating` >= **72** and `IssueCount` >= **4**, top **140** -> **14**, each drawn with its cover issue (`kind: 'series'`) |
| `collected-editions` | `CollectionNode.ContainsCount` >= **6**, not an alternate track, one per series, top **160** -> **12** |
| `top-shelf-reads` | The COUNTERPART kind rated >= **60**, top **120** -> **14** (a comics page shows books; a books page shows comics — the standalone's `topBooks`, generalized) |
| `suggested` | `SuggestionsController.SuggestAsync` — the slice-3 recommender, composed rather than re-implemented |
| `fresh-arrivals` | Most recently indexed **28**, id as the tiebreaker. **Not** rotated: the point is that they are the newest |

- "**Carries editorial prose**" is `Item.ResolvedSynopsisSource != None` — the resolver already picked the leg
  that won the synopsis, so v2 asks one scalar where the standalone OR'd two joins.
- Every rotated rail is a **deterministic Fisher–Yates pick from a RANKED pool, seeded by the UTC day number**.
  That is what makes the page rotate once a day rather than per render, keeps it identical across renders and
  replicas, and lets `?seed=` re-roll it reproducibly. The seed is echoed in the response.
- **Caching:** the composed payload is memory-cached per `userId:ceiling:isAdmin` x kind x seed for 24 h — the
  standalone's `library_home` key on v2 vocabulary. That TTL is a backstop only: `CacheWarmupService` re-runs the
  real action for every `KnownIdentity` whenever the catalog fingerprint moves, so fresh arrivals show up within
  a poll and no visitor ever pays the assembly. `?seed=` re-rolls key separately, stay unwarmed, and expire.
  `/explore/kids` caches under a **seed-only** key: its ceiling is fixed and nothing per-user is composed, so one
  warm serves every account.

### Kids

`KidsPolicy` (`Access/KidsPolicy.cs`) is the one answer to "is this kid content", shared by `/explore/kids` and
`/kids/...` so the landing and the browse can never disagree.

- **Two gates, in order.** The admin allow-list (`KidSafeTag`, scoped by `AppliesTo` — comics are cleared by
  `audience: all-ages`, books by `audience: children`) decides INCLUSION; the blocked-audience floor then
  overrules it. The floor is `MaturityFilter.HardBlockedAbove(0)` — read from the maturity gate rather than
  copied, so raising it is one edit.
- **`teen` is deliberately not blocked**, mirroring min-wins: a kid-clear series that also reads as teen (Bone,
  Tintin, Asterix) is still kid content. Only a two-or-more-level spread (all-ages AND mature) is a contradiction.
- **The ceiling is the VIEW's, not the caller's.** A child, an adult and an admin see the same shelves — which is
  the only way the view can be checked before a child is handed it.
- Shelves are `BrowseGroupItem`s, the same shape `/browse/groups` sends, so the kids shelf and the main shelf are
  one component with a different source. Cover URLs ride in a `covers` map beside the groups: `ItemSummary` is the
  shared flat projection and must not grow a per-surface field.
- Kid-cleared BOOKS ride as a single trailing `books` group. A book carries its own clearance (`ItemTag`) and its
  own maturity (its current `Insight`), and Calibre makes roughly one folder per book, so there are no book
  shelves to build — the standalone listed them flat after the comic shelves and that order is kept.
- One deliberate change: the standalone's kids home reshuffled on every request; `/explore/kids` is **seeded**
  like every other rail, so the day's shelf is stable, reproducible and cacheable.

### Novels

- **The strictest gate in the vertical, unchanged from the standalone:** a book's maturity is on its own current
  `Insight` row, and a book with **no** maturity is hidden below ceiling 3. An unclassified book is never assumed
  safe. It lives in `MaturityFilter`, so this controller just starts from `ItemAccess.VisibleItems`.
- Filters are **exact-equality, comma-separated, OR within a facet and AND across** (the standalone's semantics),
  landing on ROWS: `author` = `ItemCredit(Source=Calibre, Role=Author)` (so Calibre's `"A & B"` is two credits and
  either name finds the book), `series` / `publisher` = `BookDetail`, `decade` = `Item.ResolvedYear` (not a
  `SUBSTR` of a date string — that is what let a malformed date invent a `0100s` facet), `tag` = an
  `ItemTag(Category, Value)` EXISTS. `?tag=genre:dystopian` pins the category, a bare `?tag=dystopian` matches any
  — and `/novels/facets` hands tags back in that composite spelling, so a chip round-trips unchanged.
- `q=` is the same FTS5 search the catalog uses. `orderby` = `author` (default: author -> series -> title) |
  `title` | `rating` | `newest` | `oldest`, and every one ends with the item id.
- Facets count the **gated** set and are NOT re-filtered by the active selections — the rail is what you could
  choose, not what you have chosen. Decades stay chronological, newest first, never count-sorted. Cached per
  caller for 48 h, like the browse facets.

### The marks filters are now wired into `/browse` (they were accepted-and-ignored in slice 1)

- `?wantToReadOnly=true` / `?readOnly=true` restrict the browse to what the caller has marked; both together AND.
- **They restrict ITEMS, not group keys.** The standalone filtered group KEYS against `GroupUserMetadata`, which
  only ever worked for the series grouping — a reader marks series, not decades, so "read only" grouped by decade
  returned nothing. In v2 a mark is an item mark (`UserItemState`) or a series mark (`GroupMark(Series)`, which
  fans out to the issues anyway), so the filter is "the items you marked, plus the items of the series you
  marked" (`UserActivityQueries.MarkedItemIds` / `ReadSeriesIds` / `WantedSeriesIds`). Heads, bands and the letter
  rail then all fall out of one filtered set and cannot disagree.
- **A mark-filtered signature is never cached.** It is per-user and changes on every click, so a cached head list
  would be wrong the moment a reader marked something. `/browse/groups`, `/browse/group-letters` and
  `/browse/groups/{groupBy}/{key}/items` all take the flags.
- Group heads now carry `userMeta` from `GroupMark` — one read of the caller's own (few) rows for the grouping's
  type, matched in memory, the same shape `POST /marks/groups/batch` uses and for the same reason. Franchise has
  no group type of its own, so its heads carry no marks.

## Runtime endpoints (R6 slice 5 — admin & providers)

The operator's half of the vertical: the jobs that BUILD the catalog, the registry that says what is derived
from what, and the reconciliation tools for when a series went wrong. Everything here is
`[Authorize(Policy = "admin")]` and sits under `/admin` (through the site proxy, `/API/Books/admin/…`).

| Route | What it is |
|---|---|
| `GET /admin/info` | The counts an operator checks first — catalog, derived tables, links, dedup — plus what this host is configured with and every job it has run |
| `GET /admin/derived` | The **derived-table registry**: each table, the verb that rebuilds it, its stored input fingerprint, when it last ran, and whether it is now `stale` |
| `GET /admin/jobs/status?kind=` · `POST /admin/jobs/{kind}/stop` | The job runner: one snapshot per kind, and a cooperative stop |
| `POST /admin/scan/start?rootId=&apply=` · `GET /admin/scan/status` · `POST /admin/scan/stop` | The library scan. **Without `apply=true` this is a PREVIEW** — would-add / would-change / would-remove, nothing written |
| `POST /admin/thumbnails/start?reset=` · `GET /admin/thumbnails/status` · `POST /admin/thumbnails/stop` | The generate-missing thumbnail pass |
| `GET /admin/broken?skip=&top=` | The files a scan or a thumbnail pass could not read, paged |
| `GET/POST/PUT/DELETE /admin/roots[/{id}]` | `LibraryRoot` CRUD. A delete is REFUSED while the root still holds items |
| `POST /admin/calibre/import?metadata=&link=&apply=` | Fill the books' Calibre-native identity (see below) |
| `POST /admin/cache/clear?apply=` | Delete GENERATED thumbnails only — the `^\d+\.webp$` guard |
| `POST/DELETE /admin/folders/{id}/icon` | The hand-made collection icon, `f_{id}.jpg` |
| `GET/PUT /admin/config` | The settings overlay — an allow-list, not a config editor (see below) |
| `GET/DELETE /admin/logs?count=&level=&afterSeq=` | The host's own log tail, newest first |
| `GET/PUT/DELETE /admin/kids-tags[/{category}/{tag}]` | The `KidSafeTag` allow-list |
| `POST /admin/recompute/{what}` | Start the job that owns one derived table: `series`, `resolve`, `tags`, `reading-order`, `containment`, `collected-editions`, `ratings` |
| `POST /admin/dedup/start` · `GET /admin/dedup` · `POST /admin/dedup/{id}/resolve` | Duplicate detection, review and resolution |
| `GET/PUT/DELETE /admin/normalization/aliases` · `POST /admin/normalization/apply?apply=` | The `TagAlias` map and the four tag-hygiene passes |
| `GET /admin/series/summary` · `/{id}/aliases` · `/link-candidates` · `/decisions` · `/namefix` · `/split-overmatch` | Series reconciliation, read side |
| `POST /admin/series/clear-link` · `set-link` · `fold` · `unify-folder` · `review` · `decisions/{id}/revert` · `prune` · `PUT /{id}/override` | Series reconciliation, write side — every one of them edits an INPUT |
| `POST /admin/comicvine/start?mode=series\|issues` · `GET /admin/comicvine/status` · `POST /admin/comicvine/stop` | The ComicVine scrape |
| `POST /admin/external/start` · `GET /admin/external/status` · `POST /admin/external/stop` | The Open Library / Google Books fallback scrape |
| `GET /admin/{kind}/events` | The live job feed as Server-Sent Events |

Contract notes worth knowing before writing a client:

- **No endpoint here loops to completion.** A start endpoint runs ONE batch inline — so the 202 carries real
  numbers rather than a promise — hands the rest to `JobRunner`'s background loop, and answers with a
  `statusUrl`. One job at a time per KIND (a second start is a **409**); a stop takes effect at the next batch
  boundary with the cursor already committed, so nothing is lost and a restart resumes.
- **A job that stops moving stops.** The runner breaks on a batch that processes nothing OR that reports the
  same cursor twice — the same no-progress guard every CLI verb has.
- **`GET /admin/{kind}/events`** sets `X-Accel-Buffering: no` (or an nginx-family proxy buffers the stream into
  silence) and writes a `: keepalive` COMMENT every 20 s (or an idle intermediary closes a connection that is
  merely waiting for the next batch). A comment is not an event, so no client sees it as data.
- **The cache clear's guard is a whitelist on the NAME**, `^\d+\.webp$`. Those are the files `books-thumbs` can
  regenerate. A collection icon is `f_{id}.jpg` and can never be regenerated, so it must survive — which is why
  the guard is not a wildcard over the directory.
- **The config overlay is an allow-list**, stored at `Books:SettingsOverlayPath` (default
  `books.settings.json` beside books.db) and written atomically. Only `ComicVineApiKey`, `ThumbnailQuality`,
  `PageJpegQuality` and `ArchiveCacheGb` are settable; an unknown key or an out-of-range number is a **400**,
  never a silent no-op. Paths and the other secrets are deliberately NOT settable — an endpoint that could
  re-point `DbPath` or `MediaTokenSecret` would let an admin account move the database or forge media tokens.
  A secret reads back as `"(set)"`, never as its value.
- **`Books:ComicVineApiKey` is plain configuration.** The standalone's per-user DPAPI key vault and its
  controller are DELETED: one shared scraper key belongs to the host, not to an account. With no key the
  scrapers run **cache-first only** and never open a socket — which is also how the tests drive them.
- **User administration is gone**: the site owns users.

### Jobs & verbs

Every long job is chunked, resumable and observable, prints `{ processed, remaining, nextCursor, … }` per batch,
and is driven to completion by its CALLER (the verb's own loop, or `JobRunner`). Each derived table is stamped
in `DerivedTable` with its fingerprint, row count and rebuild time by the job that owns it.

| Verb | What it derives | `DerivedTable` |
|---|---|---|
| `books-scan [--root] [--batch-size] [--max-batches] [--apply] [--resume] [--status]` | Walks the roots READ-ONLY and reconciles `Folder` / `Item` / `ItemState` / `ComicEmbedded` / `ComicDetail` / `BookDetail` / `ItemCredit(ComicInfo)` / `ItemTag(ComicInfo)`, then the folder aggregates and the publisher backfill | `Folder.TopFolderId/Counts` |
| `books-resolve [--series] [--tags] [--fts] [--batch-size]` | `--series` = the identity rebuild; `--tags` = the legs-reading folds; bare = insight currency, the AI fold, `Series.Resolved*`, `Item.Resolved*`, `ItemFts` | `Series`, `SeriesAlias`, `Item.SeriesId`, `ItemTag/SeriesTag(folds)`, `Insight.IsCurrent`, `Item.Resolved*`, `Series.Resolved*`, `ItemFts` |
| `books-thumbs [--missing] [--batch-size] [--max-batches] [--reset] [--status]` | The missing `{itemId}.webp` covers | — |
| `books-reading-order [--series] [--batch-size]` | `ReadingOrderEntry` — tier, number, date, dense `ReadIndex` per run | `ReadingOrderEntry` |
| `books-reading-order-audit [--out]` | A per-series CSV of coverage and which signal won | — |
| `books-containment [--batch-size]` | `CollectionNode` — levels, spans, nesting, primary track | `CollectionNode` |
| `books-collected-editions [--legs] [--batch-size]` (alias `books-locg-containment`) | `CollectedEditionSpan(Source=Locg)` from the warehouse's containment edges | `CollectedEditionSpan(Source=Locg)` |
| `books-library-ratings [--batch-size] [--resolve]` | `Rating(Source=Library)` for every series and item, then re-materializes `ResolvedRating` | `Rating(Source=Library)` |
| `books-import-calibre --metadata <metadata.db> [--link] [--apply] [--reset]` | `Item.CalibreBookId`, `BookDetail`, `ItemCredit(Calibre)`, `ItemTag(Calibre)` | — |
| `books-locg-import --file <export.jsonl> [--legs]` | `LocgComicRaw` + `LocgCreatorRaw` (legs) and the hot `LocgComic` subset | — |
| `books-locg-import-map --file <map.csv>` | `ItemProviderLink(Locg, Manual)` from offline-decided matches | — |
| `books-gcd-match --gcd <gcd.db> [--legs]` | `GcdIssue` (legs) + `ItemProviderLink(Gcd)` by ISBN then barcode | — |
| `books-mu-import --file <export.json> [--legs]` | `MuSeries` (hot) + `MuSeriesRaw` (legs) | — |
| `books-dedup [--csv] [--apply] [--reset] [--batch-size]` | `DuplicateGroup` / `DuplicateMember` with a suggested keeper | — |
| `books-fix-issue-numbers [--apply]` | Re-extracts `ComicDetail.IssueNo` from the filenames and reports what moved | — |
| `books-parse-audit [--out]` | The parse-pipeline CSV, one row per comic with a source per field | — |
| `books-series-{override,clearlink,namefix,prune,split-overmatch}` | Edits to the resolution INPUTS (and two read-only reports) | — |

Contract notes worth knowing before running any of them:

- **`books-scan` is dry-run by default**, and so are `books-dedup`, `books-import-calibre`,
  `books-fix-issue-numbers`, `books-series-namefix` and `books-series-prune`. `--apply` is the house rule.
- **A removed file is MARKED, never deleted.** `UserItemState.ItemId` is a foreign key to `Item`, so "delete
  the item but keep the reader's rows" is not a state the schema can hold — and keeping the reader's position
  and marks is the requirement that matters. A missing file gets `Item.IsExcluded = 1` (so it leaves every
  browse surface through the gate that already exists) and `ItemState.IsBroken` with reason `missing` (so the
  broken panel lists it). A later scan that finds the file clears both and the item returns whole, id and all.
- **The scan's guard**: an unreachable root refuses the whole run, and a root that goes unreachable MID-scan
  aborts the removal phase. A dropped share must never mark a library missing.
- **After a scan, run `books-resolve --series` and then `books-resolve`.** The scan writes the inputs; the
  identity and the browse scalars are derived, and the verb says so when it finishes.
- **Reconciliation edits inputs.** `clear-link` / `set-link` write `SeriesKeyLink`; `fold` / `unify-folder`
  write `ComicDetail.ParsedSeriesKey`; `override` writes `Series.DisplayNameOverride`. Each answers
  `rebuildRequired: true` and each is recorded in `SeriesInferenceDecision` with an undo payload. A cleared
  link stays as `Cleared` rather than being deleted, so the next scrape cannot re-make the same wrong match.
- **A folded-away spelling leaves an EMPTY series, not a merged one** — its own parsed key still exists, so it
  keeps its own canonical key. Removing an empty series is `books-series-prune`'s job, and prune never removes
  one a reader has marked.
- `books-import-calibre` is what finally fills **`BookDetail.SeriesName`**: it is NULL for all 22,084 migrated
  books because v1 had no column for it, so until this verb runs the novels facets have no series rail.
- The LOCG, GCD and MangaUpdates **scrapers are not ported and never will be** — they are offline Node and
  Python pipelines, and that is the right shape for them. What is ported is their CONSUME side, so nothing in
  this list opens a socket except the two provider clients.
