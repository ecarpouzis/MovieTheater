# Books v2 data model — design for the MyBooks → MovieTheater merge (R3)

**Status: at the approval gate (R3).** R4 (EF migrations + the copy-transform tool) starts only when this document is approved. Evidence: `v1-baseline-counts.md`, `v1-data-model-audit.md` (the R0 census), the gitignored artifacts under `data/books/census/`, and the MyBooks skills that describe each metadata leg. Machine-readable companion: `v2-mapping.json` (generated from `scripts/books/census/v2_mapping_spec.py`; checked by `v2_mapping_check.py`; the generated sections below are rendered by `write_v2_model.py`).

## 1. Goals, invariants, constraints

Goals: one clean, EF-migration-owned SQLite model that the ported backend lands on **once**; hot browse paths that touch two tables; every metadata leg represented in one shape; offline warehouse data out of the runtime's file; nothing the current UI shows lost.

**Invariants (verified by R4's `--verify`, not implementation details):**
1. **Ids are preserved**: `Item.Id = Comics.Id`, `Series.Id`, `Folder.Id`, `Publisher.Id`. The 140,983 thumbnail files are named by item id; `?series=<id>` deep links, `GroupMark` keys and `SeriesMerge` all depend on it.
2. **The browse projection joins `Item` + `Series` only.** Everything a card, band, facet, group or OData row needs is materialized on those two rows (`Resolved*`, `CoverAspect`, `SeriesId`, `ResolvedRating`, `ResolvedTagsCsv`, `ResolvedCreatorsCsv`). `ComicEmbedded`, `ItemState`, `ItemSignature`, `ComicDetail`, `Rating`, `Insight`, the provider records: modal/admin/job reads only.
3. **Derived rows are never hand-edited.** Every derived table/column is registered in `DerivedTable` with the job that rebuilds it and the input fingerprint that gates the rebuild; fixes edit inputs and re-run the job (the `series-reconciliation` golden rule, made structural).
4. **No FK crosses the hot/legs file boundary.** A legs row that names a missing hot id is skipped and reported, never thrown on.
5. Flat-scalar rule for the OData surface (a collection sub-projection breaks `$count`): collections reach the projection only as materialized CSV scalars (`ResolvedTagsCsv`, `ResolvedCreatorsCsv`).
6. Every long job (migration stages, resolve, folds, thumbnails, scans, scrapes) is chunked, observable and resumable with a cursor that matches its query's ordering.

Constraints carried from the program: MT `UserId` is the only user key (no user table here; per-user settings live in MT `UserSettings`); SQLite; identity arrives in the signed header; the per-folder ACL is gone (2 distinct lists over 54,418 folders — a test-account artifact — and its `LIKE` scan ran on every list query; a private-root feature, if ever wanted, lives on `LibraryRoot`).

## 2. Two files

- **`books.db` — `BooksDb`**, the only file BooksHost opens. Catalog, resolved scalars, tags, credits, links, insights, ratings, user state, the provider records the runtime reads (`CvVolume`, `CvIssue`, hot `LocgComic`, `MuSeries`, `ExternalWork`, `BarneyProg`), reconciliation/dedup state, system tables.
- **`books-legs.db` — `BooksLegsDb`**, a second DbContext with its own entities and migrations (the fifteen tables that had no EF entity in v1 finally get one). Offline warehouse: `LocgComicRaw` (all 156,839 rows incl. the 73k stubs), `LocgCreatorRaw`, `LocgContainment` (391k edges), `LocgSeries`, `GcdIssue`/`GcdSeries`, `OpenLibraryEdition`/`OpenLibraryWork`, the two `*SeriesInference` tables, Marvel, `BarcodeScan`, `ProviderResponseCache`, `LinkCandidates`, `MuSeriesRaw`, `CvVolumeRaw`. The `books-*` CLI verbs open both contexts; the two verbs that need a cross-file join (`books-locg-containment`, `books-gcd-match`) `ATTACH` on a bare `SqliteConnection` and treat a missing hot id as skip-and-report.

Why: the census shows the runtime never reads `LocgContainment`, the 73k LOCG stubs, `GcdIssue` beyond a fold, the OL ISBN tables, `ComicvineApiCaches` or any `CandidatesJson` on a settled link — together the bulk of the 741 MB — while the browse path pays for their pages and their indexes. Size attribution is in §16.

## 3. The base entity, split

`Comics` was six things in one 76-column row (audit §3, ownership map): file identity written by the scanner; the raw ComicInfo.xml block (~23 % of comics; nine columns write-only); the Calibre book block squatting in ComicInfo-shaped columns; churny state rewritten by jobs; dedup signatures; and the exclusion flags every browse query filters on. v2:

| Table | Owner (writer) | Why it is its own table |
|---|---|---|
| `Item` | scanner / Calibre importer; `Resolved*` by `books-resolve` | The catalog row: identity, the two browse-hot flags (`IsExcluded`, `KeepInDirectory`), the materialized scalars. `Path` is UNIQUE (the scanner's natural key was unindexed in v1 — `CalibreImportService` warns it is O(n·m)); `CalibreBookId` is UNIQUE-nullable (the importer's natural key). |
| `ItemState` | thumbnail jobs, broken-scan, dedup review | Rewritten in bulk by jobs; keeping it off `Item` means a thumbnail pass rewrites no catalog pages. Holds the four `CoverWidth/Height/CoverDimsComputedFor`… writers' target; `CoverAspect` on `Item` is derived from it. |
| `ItemSignature` | dedup service (comics only) | Written by raw SQL today; read only by dedup + the moved-file reconciler. |
| `ComicEmbedded` | scanner (ComicInfo.xml), **never rewritten** | The raw record. CVDB-resolved genre names go to `ItemTag(Source=Cv)` instead of overwriting `Genre` (v1 destroyed the original). The write-only columns the census found (`GTIN`, `SeriesGroup`, `AlternateCount`, `Translator`, `StoryArcNumber`, `MainCharacterOrTeam`, `MetadataVersion`) are dropped; `Imprint` (7,129 rows, 64 imprints), `AgeRating` (525 rows, a real maturity signal), `Count` (5,384), `Notes` (25,509 — scraper provenance incl. Amazon ASINs), `Locations` (3,613 CV entity names) are populated and kept. |
| `BookDetail` | Calibre importer | Calibre-native identity: ISBN (was hiding in `Identifier` with no reader), series name/index, publisher, pubdate, language, description. Authors → `ItemCredit(Source=Calibre)`, subject tags → `ItemTag(Source=Calibre)`. `IssueTitle` (a duplicate of `Title` for every book) is not carried. |
| `ComicDetail` | parse pipeline (`ParsedDetailService`), comics only by design | Parse output with typed provenance (`Confidence`, the four `*Source` enums), `FormatRaw` beside the `Format` enum (33 spellings today), and **`ParsedSeriesKey` — the resolution input**. The four v1 FKs are gone: `SeriesId` moves to `Item` (materialized, still derived); CV/External/insight linkage is series-scoped and lives on `Series`. |

## 4. Derived vs input — made structural

Inputs the reconciliation ops edit: `ComicDetail.ParsedSeriesKey`; **`SeriesKeyLink(ParsedKey, Provider ∈ {Cv, External}, ProviderKey, Status, Score, StoredTopScore, …)`** — the string-keyed table `ComicvineSeriesLinks`/`ExternalSeriesLinks` become; `Series.DisplayNameOverride`; `TagAlias`; `KidSafeTag`; `Rating(IsOverride=1)` rows; `SeriesInferenceDecision`/`SeriesMatchReview`.

Derived, each with a registered job: `Series` (+ `SeriesAlias`, `Item.SeriesId`) ← `books-resolve-series` (canonical key `cv:{id}` > `ext:{id}` > `parsed:{key}`, exactly today's `SeriesResolutionService.RebuildAsync`); `Item.Resolved*`, `Series.Resolved*`, `CoverAspect`, `ResolvedRating`, `ResolvedTagsCsv`, `ResolvedCreatorsCsv`, `ItemFts` ← `books-resolve`; `ReadingOrderEntry` ← `books-reading-order`; `CollectionNode` ← `books-containment`; tag folds (`ItemTag`/`SeriesTag` rows with `Source ∈ {AI, External, Mu, Gcd, Cv}`) ← `books-fold-tags`; `Insight.IsCurrent` ← `books-resolve-insights`; `Rating(Source=Library)` ← `books-library-ratings`. The `DerivedTable` registry row carries `RebuildJob`, `InputFingerprint` (the seven v1 `SystemState` fingerprints move here) and `LastRebuiltAt`; the admin panel shows it.

`SeriesMergeService.MergeAsync(old, new)` must touch: `SeriesAlias`, `Item.SeriesId`, `MuSeriesLink`, `Insight(SubjectKind=Series)`, `Rating(TargetKind=Series)`, `SeriesTag`, `GroupMark(Series)`, `ReadingOrderEntry.SeriesId`, `CollectionNode.SeriesId`, `CollectedEditionSpan.SeriesId`, then append `SeriesMerge` and mark reading-order/containment for recompute. Collision rule per table: survivor's row wins for links/insight-current; marks OR their flags and keep max rating; tags union.

## 5. The browse projection contract

`ItemSummary` = `Item ⋈ Series` (LEFT JOIN on `Item.SeriesId`). Fields: ids, `Kind`, file facts, `Title`/`ResolvedTitle`, `ResolvedSeries`, `Series.Name`/`DisplayNameOverride`/`IssueCount`/`YearStart`/`YearEnd`/`IsOngoing`/`Franchise`, `ResolvedPublisher`, `ResolvedYear`/`Month`/`DatePrecision`, `ResolvedRating`, `CoverAspect`, `ResolvedTagsCsv`, `ResolvedCreatorsCsv`, `ResolvedSynopsisSource`, `IsExcluded`, `KeepInDirectory`, `TopFolderId`, `PublisherId`, reading-order scalars and containment scalars. **Reading order and containment**: `ReadingOrderEntry` and `CollectionNode` are 1:1 on `ItemId`; the band/list paths read `ReadIndex`/`ReadDate` and the span scalars, so those two tables are the only additional joins the *grouped* paths may make (both PK joins). The facet path never scans `Item` text columns: facets are `GROUP BY` over `ItemTag(Category, Value)`, `SeriesTag`, `ItemCredit(Role, NormalizedName)`, `Item.TopFolderId`, `Item.PublisherId`, `Series.Id`, `ComicDetail.EventName` (indexed) and `ResolvedYear/10` — each with a covering index. That is what removes the 21 TEMP B-TREE scans the census found.

**Synopsis is not materialized; it is pointed at.** The UI keeps showing the real description exactly as today. `Item.ResolvedSynopsisSource` records which leg won the priority chain; the modal/OData projection `COALESCE`s the one text column the pointer names (`CvIssue.Description`, `ComicEmbedded.Summary`, `LocgComic.Description`, `ExternalWork.Description`, `MuSeries.Description`, `CvVolume.Deck`, `Insight.Synopsis`). Copying the winner into a `Resolved*` text column would duplicate the two largest columns in the file (45.7 + 32 MB).

**`ItemFts`** (FTS5, content-less, rowid = `Item.Id`) indexes exactly what today's `RebuildFtsAsync` indexes, on the resolved values: `ResolvedTitle`, `NormalizedTitle`, `ResolvedSeries` + `ComicEmbedded.Series`/`AltSeries` + `ComicDetail.ParsedSeriesKey`, `ComicDetail.IssueTitle`/`ComicEmbedded.Title`, `ResolvedPublisher`, `ResolvedCreatorsCsv`, `ResolvedTagsCsv`, `ComicEmbedded.Characters`/`Teams`/`Locations`/`StoryArc`, `ComicDetail.EventName`, `BookDetail.SeriesName`. **No synopsis** — today's index has none either, so search semantics are unchanged and the ~78 MB of description text stays out of the index. Rebuilt at the end of `books-resolve` (not by the scanner); per-item upsert on single-item resolve.

## 6. Resolver (`books-resolve`)

One C# resolver, replacing `ComicSummary.Project` + the client's `transformComic`/`dateUtils` cascade. Per field, best source first (from the `unified-data` skill, with junk gates preserved):

| Resolved field | Chain |
|---|---|
| `ResolvedSeries` | `Series.DisplayNameOverride` → `Series.Name` (CV volume name > External work title > parsed key) → `ComicDetail.ParsedSeriesKey` → `ComicEmbedded.Series` |
| `ResolvedTitle` | single-issue series ⇒ the series name; numbered floppy ⇒ `"{Series} Vol N #M"`; collections/one-shots keep `ComicEmbedded.Title`/`Item.Title`; books ⇒ `Item.Title` |
| `ResolvedPublisher` | `CvVolume.PublisherName` → `ComicEmbedded.Publisher` → `ComicDetail.Publisher` → `BookDetail.Publisher` → top folder name |
| `ResolvedYear/Month/DatePrecision` | `ReadingOrderEntry.ReadDate` (month only at Day/Month precision) → `ComicEmbedded.PublicationDate` → `ComicDetail.Year` → `ExternalWork.FirstPublishYear` → `BookDetail.PublishedOn` |
| `ResolvedSynopsisSource` | CV issue description → embedded summary → LOCG description → External description → MU description → CV deck → current `Insight.Synopsis`; each candidate passes `prepSynopsis` (strip HTML, LOCG spec tail, "Collects…" boilerplate under 200 chars, CV meta-cruft, per-source minimum length) |
| `ResolvedCreatorsCsv` | from `ItemCredit`: ComicInfo roles → LOCG roles (matched, quality ≥ Medium) → Calibre authors → current `Insight.Author/Artist` (series-level fallback) |
| `ResolvedTagsCsv` | union of `ItemTag` + the series' `SeriesTag` values, alias-folded, `CVDB####` tokens never present |
| `ResolvedRating` | `Rating(Source=Library)` if present → `User` → `Locg` (0–5 × 20) → current `Insight.Rating`; all stored 0–100 |
| `CoverAspect` | `ItemState.CoverWidth/Height` clamped [0.35, 1.6], default 0.66 |
| `Series.ResolvedSynopsisSource` | CV volume description → MU description → External description → current series `Insight.Synopsis` → CV deck (series-level legs only) |

Trigger: fingerprint-gated after every scan/import/fold/insight change (registry row), plus per-item on demand from the admin panel.

## 7. Tags

`ItemTag(ItemId, Category, Value, Source)` and `SeriesTag(SeriesId, Category, Value, Source)`, `Source ∈ {ComicInfo, Cv, Calibre, Locg, Gcd, External, Mu, AI}`. Sources: ComicInfo `Genre`/`Tags` split on `,`/`;`; CVDB resolutions (`Source=Cv`); Calibre subjects; GCD story genres (`Source=Gcd`, the densest genre source, ~75k comics); External subjects through the closed whitelist (`Source=External`); MU genres (`Source=Mu`); the current insight's `InsightTag` rows (`Source=AI`, only High-confidence series per today's fold rule, the >5-series threshold and the alias map). `TagAlias` folds spellings at write time. Facets: `GROUP BY (Category, Value)` with covering indexes `(Category, Value, ItemId)` / `(Category, Value, SeriesId)`. The OData `$filter` uses `Item.ResolvedTagsCsv` (`contains`) exactly as the six-way `contains(...)` does today, but against one column.

## 8. Credits — GATE-3 (decided: one `ItemCredit`)

`ItemCredit(ItemId, Source ∈ {ComicInfo, Locg, Calibre, AI}, Ordinal, Role, Name, NormalizedName, ProviderPersonId)`. Fed from `ComicEmbedded.Writers/Pencillers/Inker/Colorist/Letterer/CoverArtist/Editor` (split on `,`/`;`, `[CVID]` stripped), LOCG `CreatorsJson` for matched rows (median 427 B, p90 2 KB, max 82 KB / 1,343 entries; roles are free text incl. combos), Calibre authors (the `' & '` string), and the current series insight's `Author`/`Artist` as a series-level fallback marked `Source=AI`. Author/artist facets group on `(Role, NormalizedName)`. `LocgCreator` and `BookAuthor` from the earlier draft are gone; the raw strings stay in `ComicEmbedded`. The legs file keeps `LocgCreatorRaw` for every LOCG row so the hot subset can be re-derived.

## 9. Insights — GATE-1/2 (decided: append-only, one table)

`Insight(Id, SubjectKind ∈ {Item, Series}, SubjectId, ModelId, Rank, Confidence, Recognized, Rating, Synopsis, Author, Artist, YearBegin, YearEnd, Maturity?, ReviewFlag, SourceKey, GeneratedAt, IsCurrent)` + `InsightTag(InsightId, Category, Value)` — MT's `TitleInsight`/`TitleTag` shape. Append-only: a new model pass inserts rows; `books-resolve-insights` sets `IsCurrent` by **rank → confidence → recency** per subject (the `MODEL_RANK` map from `book_insert.py`/`insert_session*.py` becomes the `Rank` column: `openlibrary`/`calibre-tags`/`epub-jacket` = 0, model ids by their table). Nothing is overwritten; a bad pass is un-current-able. The v1 name-keyed `ClaudeSeriesMetadata` rows become `SubjectKind=Series` rows resolved through `SeriesAlias(lower(name))` with `SourceKey` = the v1 name (1,485 series carry >1 row; of the 1,079 rows whose name no longer matches a parsed key, 57 resolve through a current `Series` name / alias and are carried; the rest are artifacts of the round-3 name fixes — keys like `008 Supergirl`, `10 Justice League of America` — whose proper series already have their own rows, so they are exported to `data/books/v1/orphan-insights.json` and not carried). `ClaudeBookMetadata` rows become `SubjectKind=Item` rows (`Maturity` 0–3 from the `audience` tag). The books gate is unchanged in effect: a restricted user sees a book only if its **current** `Insight` row has `Maturity ≤ ceiling`; no current row with a `Maturity` ⇒ hidden (fail-safe). Comics keep the min-wins `audience` rule over the current series insight's tags and the `KidSafeTag` allowlist (`AppliesTo` kept: comics carry `all-ages`, books `children`). The insert scripts move to `scripts/books/` and write `Insight` rows.

## 10. Ratings

`Rating(TargetKind, TargetId, Source ∈ {User, AI, Locg, Mu, Library, Override}, Value 0–100, RawValue, RawScale, Count, Note, IsOverride, ModelId, GeneratedAt)`. Normalized at write time; provenance kept raw. ComicInfo `<Rating>` is **not** a source: 6 of 141,010 comics carry one (values 3–4) and all six already have a library rating; the raw value stays in `ComicEmbedded.Rating`. `UserRating` has 0 rows in v1 — `Source=User` exists for the per-item rating the UI can write in v2, with nothing to migrate. `books-library-ratings` (the port of `compute_library_ratings.py`: LOCG z-score-stretched + insight by confidence + MU Bayesian + awards + overrides) writes the `Library` rows; `Item.ResolvedRating` / `Series.ResolvedRating` are materialized by the resolver and are the only rating the browse path reads ("Top rated" sort, the rating facet). The modal shows the per-source rows.

## 11. Provider legs

- **`ItemProviderLink(ItemId, Provider ∈ {Cv, Locg, Gcd, Barney, Marvel, Inducks}, ProviderKey, SecondaryKey, Status, Method, MatchedKey, Confidence, Quality, StoredTopScore, Applied, AttemptCount, AttemptedAt, Error)`** — one shape for the six item-level match tables. `LinkQuality` is one enum (v1's `MatchQuality` TEXT beside `Confidence` REAL; the two `span-corroborated` rows → `High` + `Method`). `CandidatesJson` lives on the hot row **only while `Status ∈ {Pending, Multiple}`**; settled links move theirs to `LinkCandidates` (legs). `StoredTopScore` is extracted first so `ComicvineController`'s stale-match heuristic keeps working.
- **`SeriesKeyLink`** (§4) for Cv/External; **`MuSeriesLink(SeriesId…)`** because MU is matched after resolution (and the merge service re-keys it); **`MarvelSeriesLink`** in legs.
- Records: `CvVolume`, `CvIssue` (hot; the five constant-`[]` JSON columns go to `CvVolumeRaw`), **`LocgComic` hot subset** = rows referenced by an `ItemProviderLink(Locg)` and the columns the projection/modal read (`Rating`, `RatingCount`, `CoverPrice`, `IsKey`, `KeyType`, `Description`, ISBN/UPC, cover URL, story count) — the census shows 15 of 27 columns unread at runtime and half the rows are stubs; `LocgComicRaw` (legs) keeps everything. `MuSeries` hot (description/genres are projected), `ExternalWork` hot, `BarneyProg` hot (2,313 rows; reading-order input), `GcdIssue`/`GcdSeries` legs (only the genre fold reads them).
- **`CollectedEditionSpan(ItemId, Source ∈ {Locg, Gcd, Cv, Curated}, …)`** replaces the four span tables; containment precedence stays Locg → Gcd → Cv → Curated; the 1,047 curated rows are carried (`Source=Curated`; the CURATION_HANDOFF "left empty" note is stale).

## 12. User state

`UserItemState(UserId, ItemId, LastPage, LastSpineItemIndex, LastScrollPercent, Status, WantToRead, Favorite, HiddenFromHistory, UpdatedAt)` — one row per MT user × item (v1 `Bookmarks` + `ComicUserLists` + the 25 comic-typed `GroupUserMetadata` rows). Rules restated from `reading-position-unified-2026-08`: `lastPage = -1` is the **only** Finished signal; any write clears `HiddenFromHistory`; GET returns a start-of-book default, never 404. `GroupMark(UserId, GroupType ∈ {Series, Volume, Collection, Publisher, Decade}, GroupKey, …)` keeps the group-level marks the batch endpoint addresses (series keys = `SeriesId`). Migration carries only v1 user 2 → MT user 1 (715 positions: 4 unread / 235 in progress / 476 finished / 44 hidden; 3 want-to-read; 68 marks).

## 13. Enums (typed columns replacing free text)
<!-- generated:enums -->
- **Item.Kind**: Comic, Book
- **Item.ContainerFormat**: Cbz, Cbr, Pdf, Epub, Mobi, Unknown
- **ComicDetail.Format**: SingleIssue, Tpb, Hardcover, Omnibus, Annual, Special, OneShot, GraphicNovel, LimitedSeries, Weekly, Reprint, Collection, Magazine, Unknown
- **Confidence**: Unknown, Low, Medium, High
- **ComicDetail.*Source**: None, Metadata, MetadataAlt, Filename, FilenameLeadingIndex, Folder, Volume, Default, Manual
- **ReadingOrderEntry.Source**: Unordered, ComicVine, Date, IssueNo, IssueNoDate, ClaudeYear, IssueNoClaudeYear, Containment
- **DatePrecision**: None, Year, Month, Day
- **CollectionNode.Level**: Issue, Volume, Book, Omnibus
- **CollectionNode.TrackRole**: Primary, Container, Alternate
- **CollectionNode.SpanSource**: None, Inferred, ComicVine, Gcd, Locg, Curated
- **Provider**: Cv, External, Locg, Gcd, Mu, Barney, Marvel, Inducks
- **LinkStatus**: Pending, Matched, NoMatch, Multiple, Error, Manual, Cleared, Skip
- **LinkQuality**: Unknown, Low, Medium, High, Conflict
- **CollectedEditionSpan.Source**: Locg, Gcd, Cv, Curated
- **Insight.SubjectKind / Rating.TargetKind**: Item, Series
- **Rating.Source**: User, AI, Locg, Mu, Library, Override
- **ItemCredit.Source / ItemTag.Source**: ComicInfo, Cv, Calibre, Locg, Gcd, External, Mu, AI
- **UserItemState.Status**: Unread, InProgress, Finished
- **GroupMark.GroupType**: Series, Volume, Collection, Publisher, Decade
- **Item.ResolvedSynopsisSource**: None, Cv, Embedded, Locg, External, Mu, CvDeck, AI
<!-- /generated:enums -->

## 14. Entity catalog
<!-- generated:catalog -->
#### books.db — the runtime's only file (`BooksDb`)

| Table | Key | Purpose | Columns | Indexes |
|---|---|---|---|---|
| `LibraryRoot` | Id | A scanned root (comics / books; Calibre-managed or not) | `Id INTEGER, Path TEXT UNIQUE, Kind INTEGER, IsCalibre INTEGER, Enabled INTEGER` | — |
| `Folder` | Id | Folder tree (ids preserved); aggregates folded in; TopFolderId = the v1 'collection' | `Id INTEGER, RootId INTEGER FK LibraryRoot, ParentId INTEGER FK Folder NULL, Kind INTEGER, Path TEXT UNIQUE, Name TEXT, NormalizedName TEXT, Depth INTEGER, TopFolderId INTEGER, DirectChildCount INTEGER, DescendantItemCount INTEGER, FolderModifiedAt TEXT, IndexedAt TEXT, HasIcon INTEGER` | `(ParentId)`, `(TopFolderId)`, `(NormalizedName)` |
| `Publisher` | Id | Normalized publisher (ids preserved) | `Id INTEGER, Name TEXT UNIQUE, FullName TEXT` | — |
| `Item` | Id | One file = one item (ids preserved = Comics.Id). File identity + the browse-hot flags + the materialized Resolved* scalars. THE browse projection joins Item + Series only. | `Id INTEGER, RootId INTEGER FK LibraryRoot, FolderId INTEGER FK Folder, TopFolderId INTEGER, Kind INTEGER, Path TEXT UNIQUE, FileName TEXT, Extension TEXT, ContainerFormat INTEGER, FileSize INTEGER, FileModifiedAt TEXT, IndexedAt TEXT, PageCount INTEGER, Title TEXT, NormalizedTitle TEXT, CalibreBookId INTEGER UNIQUE NULL, PublisherId INTEGER FK Publisher NULL, SeriesId INTEGER FK Series NULL, IsExcluded INTEGER, KeepInDirectory INTEGER, CoverAspect REAL, ResolvedTitle TEXT, ResolvedSeries TEXT, ResolvedPublisher TEXT, ResolvedYear INTEGER, ResolvedMonth INTEGER, ResolvedDatePrecision INTEGER, ResolvedRating INTEGER, ResolvedSynopsisSource INTEGER, ResolvedCreatorsCsv TEXT, ResolvedTagsCsv TEXT, ResolvedAt TEXT` | `(Kind, ResolvedSeries, Id)`, `(Kind, ResolvedYear DESC, IndexedAt DESC, Id)`, `(Kind, ResolvedRating DESC, Id)`, `(Kind, IndexedAt DESC, Id)`, `(SeriesId, Id)`, `(FolderId)`, `(TopFolderId)`, `(PublisherId)`, `(Kind, NormalizedTitle, Id)`, `(Kind, ResolvedPublisher, Id)` |
| `ItemState` | ItemId | Churny per-item state rewritten by jobs (health, thumbnail, cover dims, exclusion detail) so the catalog row is not rewritten | `ItemId INTEGER FK Item, IsBroken INTEGER, BrokenReason TEXT, BrokenCheckedAt TEXT, ThumbnailError TEXT, ThumbnailCheckedAt TEXT, CoverWidth INTEGER, CoverHeight INTEGER, CoverDimsComputedFor TEXT, ExclusionReason TEXT, ExcludedAt TEXT` | — |
| `ItemSignature` | ItemId | Dedup signatures (comics only) | `ItemId INTEGER FK Item, ContentFingerprint TEXT, CoverPHash INTEGER, PageSignature TEXT, SignaturesComputedFor TEXT` | `(ContentFingerprint)`, `(CoverPHash)` |
| `ComicEmbedded` | ItemId | Raw ComicInfo.xml as read from the archive (comics with a ComicInfo; never rewritten - CVDB-resolved names go to ItemTag/ItemCredit) | `ItemId INTEGER FK Item, Series TEXT, Number TEXT, AltSeries TEXT, AltNumber TEXT, Volume INTEGER, Title TEXT, Summary TEXT, Publisher TEXT, Imprint TEXT, Genre TEXT, Tags TEXT, Characters TEXT, Teams TEXT, Locations TEXT, StoryArc TEXT, Web TEXT, Language TEXT, Format TEXT, PublicationDate TEXT, Writers TEXT, Pencillers TEXT, Inker TEXT, Colorist TEXT, Letterer TEXT, CoverArtist TEXT, Editor TEXT, BlackAndWhite INTEGER, Manga TEXT, Rating INTEGER, Identifier TEXT, Notes TEXT, Count INTEGER, AgeRating TEXT` | — |
| `BookDetail` | ItemId | Calibre-native identity of a book (authors -> ItemCredit, subject tags -> ItemTag) | `ItemId INTEGER FK Item, Isbn TEXT, SeriesName TEXT, SeriesIndex REAL, Publisher TEXT, PublishedOn TEXT, Language TEXT, Description TEXT` | `(Isbn)`, `(SeriesName, SeriesIndex)` |
| `ComicDetail` | ItemId | Parse-pipeline output (comics only). ParsedSeriesKey is the resolution INPUT; SeriesId on Item is DERIVED from it | `ItemId INTEGER FK Item, ParsedSeriesKey TEXT, IssueNo TEXT, Year INTEGER, VolumeNo INTEGER, Publisher TEXT, Format INTEGER, FormatRaw TEXT, IsCollection INTEGER, EventName TEXT, IssueTitle TEXT, Confidence INTEGER, SeriesSource INTEGER, IssueSource INTEGER, YearSource INTEGER, PublisherSource INTEGER, FolderSeries TEXT, FolderYear INTEGER, ParseNotes TEXT, ParsedAt TEXT` | `(ParsedSeriesKey)`, `(EventName)`, `(Year)` |
| `Series` | Id | DERIVED canonical series (ids preserved) + the series-scoped facts every issue used to carry; rebuilt by books-resolve-series from ComicDetail.ParsedSeriesKey + SeriesKeyLink | `Id INTEGER, ParsedKey TEXT, CanonicalKey TEXT UNIQUE, Name TEXT, DisplayNameOverride TEXT, IssueCount INTEGER, YearStart INTEGER, YearEnd INTEGER, IsOngoing INTEGER, Franchise TEXT, PublisherId INTEGER NULL, CvVolumeId INTEGER NULL, ExternalWorkId INTEGER NULL, MuSeriesId INTEGER NULL, ResolvedSynopsisSource INTEGER, ResolvedRating INTEGER, ResolvedAt TEXT` | `(Name, Id)`, `(ParsedKey)`, `(Franchise)`, `(ResolvedRating DESC, Id)` |
| `SeriesAlias` | ParsedKey | Every parsed spelling -> its canonical Series (DERIVED) | `ParsedKey TEXT, SeriesId INTEGER FK Series` | `(SeriesId)` |
| `SeriesMerge` | OldSeriesId | Old-id redirect for merged series (all 44,261 v1 rows carried; ?series=<oldId> resolves through it) | `OldSeriesId INTEGER, NewSeriesId INTEGER, MergedAt TEXT` | — |
| `SeriesKeyLink` | ParsedKey, Provider | Series-level provider link keyed by the PARSED KEY (a resolution INPUT the reconciliation ops edit) - Provider in {Cv, External} | `ParsedKey TEXT, Provider INTEGER, ProviderKey INTEGER, Status INTEGER, Score INTEGER, StoredTopScore INTEGER, AttemptCount INTEGER, AttemptedAt TEXT, Error TEXT` | `(Provider, ProviderKey)`, `(Status)` |
| `MuSeriesLink` | SeriesId | MangaUpdates link (matched AFTER resolution, so keyed by SeriesId; SeriesMergeService re-keys it) | `SeriesId INTEGER FK Series, MuSeriesId INTEGER NULL, Status INTEGER, Method TEXT, Confidence REAL, MatchedKey TEXT, CreatedAt TEXT` | — |
| `ItemProviderLink` | ItemId, Provider | One shape for every item-level leg: Cv, Locg, Gcd, Barney, Marvel, Inducks | `ItemId INTEGER FK Item, Provider INTEGER, ProviderKey TEXT, SecondaryKey TEXT, Status INTEGER, Method TEXT, MatchedKey TEXT, Confidence REAL, Quality INTEGER, StoredTopScore INTEGER, Applied INTEGER, AttemptCount INTEGER, AttemptedAt TEXT, Error TEXT` | `(Provider, ProviderKey)`, `(Provider, Status)` |
| `ItemCredit` | ItemId, Source, Ordinal | Who made it - one shape for ComicInfo creators, LOCG creators, Calibre authors (Source in {ComicInfo, Locg, Calibre, AI}); facets GROUP BY (Role, NormalizedName) | `ItemId INTEGER FK Item, Source INTEGER, Ordinal INTEGER, Role TEXT, Name TEXT, NormalizedName TEXT, ProviderPersonId TEXT` | `(Role, NormalizedName, ItemId)` |
| `ItemTag` | ItemId, Category, Value, Source | Item-level tags with provenance (ComicInfo genre/tags, Calibre subjects, CVDB-resolved names, GCD story genres, insight tags folded) | `ItemId INTEGER FK Item, Category TEXT, Value TEXT, Source INTEGER` | `(Category, Value, ItemId)` |
| `SeriesTag` | SeriesId, Category, Value, Source | Series-level tags (AI insight tags, External subjects, MU genres, CV concepts) | `SeriesId INTEGER FK Series, Category TEXT, Value TEXT, Source INTEGER` | `(Category, Value, SeriesId)` |
| `TagAlias` | Category, AliasTag | Alias -> canonical tag | `Category TEXT, AliasTag TEXT, CanonicalTag TEXT, Source TEXT` | — |
| `KidSafeTag` | Category, Tag | Kids allowlist (AppliesTo: comic/book/both) | `Category TEXT, Tag TEXT, AppliesTo TEXT, UpdatedAt TEXT` | — |
| `Insight` | Id | Append-only model/provider-generated metadata for an Item or a Series (GATE-1/2): rank -> confidence -> recency SELECTS the current row (IsCurrent); nothing overwritten. Maturity lives here for books. | `Id INTEGER, SubjectKind INTEGER, SubjectId INTEGER, ModelId TEXT, Rank INTEGER, Confidence INTEGER, Recognized INTEGER, Rating INTEGER, Synopsis TEXT, Author TEXT, Artist TEXT, YearBegin INTEGER, YearEnd INTEGER, Maturity INTEGER NULL, ReviewFlag TEXT, SourceKey TEXT, GeneratedAt TEXT, IsCurrent INTEGER` | `(SubjectKind, SubjectId, IsCurrent)`, `(SubjectKind, Maturity) WHERE IsCurrent = 1` |
| `InsightTag` | InsightId, Category, Value | Tags of one insight row (folded into ItemTag/SeriesTag with Source=AI when the row is current) | `InsightId INTEGER FK Insight, Category TEXT, Value TEXT` | — |
| `Rating` | TargetKind, TargetId, Source | Every rating with provenance, normalized to 0-100 at write time (RawValue/RawScale kept); Item/Series.ResolvedRating is materialized from it - browse never joins this | `TargetKind INTEGER, TargetId INTEGER, Source INTEGER, Value INTEGER, RawValue REAL, RawScale TEXT, Count INTEGER, Note TEXT, IsOverride INTEGER, ModelId TEXT, GeneratedAt TEXT` | — |
| `ReadingOrderEntry` | ItemId | DERIVED per-issue reading position (books-reading-order) | `ItemId INTEGER FK Item, SeriesId INTEGER, ReadTier INTEGER, ReadNumber REAL, ReadNumberSuffix REAL, ReadDate TEXT, ReadDatePrecision INTEGER, ReadIndex INTEGER, ReadCount INTEGER, Source INTEGER, Confidence INTEGER, Notes TEXT, ComputedAt TEXT` | `(SeriesId, ReadIndex)` |
| `CollectionNode` | ItemId | DERIVED containment (books-containment); ParentItemId -> Item | `ItemId INTEGER FK Item, SeriesId INTEGER, Level INTEGER, TrackRole INTEGER, SpanStart INTEGER, SpanEnd INTEGER, ContainsCount INTEGER, ParentItemId INTEGER FK Item NULL, SpanSource INTEGER, SpanLabel TEXT` | `(SeriesId)`, `(ParentItemId)`, `(ContainsCount DESC)` |
| `CollectedEditionSpan` | ItemId, Source | Collected-edition spans from the four sources (Locg, Gcd, Cv, Curated) - containment precedence Locg > Gcd > Cv > Curated | `ItemId INTEGER FK Item, Source INTEGER, SeriesId INTEGER, IssueStart REAL, IssueEnd REAL, EditionTitle TEXT, ProviderRef TEXT, Contiguous INTEGER, Confidence REAL, Note TEXT, CreatedAt TEXT` | `(SeriesId)` |
| `CvVolume` | Id | ComicVine volume (series-level facts the modal shows) | `Id INTEGER, Name TEXT, StartYear INTEGER, PublisherName TEXT, CountOfIssues INTEGER, Deck TEXT, Description TEXT, ImageUrl TEXT, SiteDetailUrl TEXT, FetchedAt TEXT` | — |
| `CvIssue` | Id | ComicVine issue (cover dates feed reading order; description is a synopsis source) | `Id INTEGER, VolumeId INTEGER, Name TEXT, IssueNumber TEXT, CoverDate TEXT, StoreDate TEXT, Deck TEXT, Description TEXT, ImageUrl TEXT, SiteDetailUrl TEXT, FetchedAt TEXT` | `(VolumeId)` |
| `LocgComic` | LocgComicId | LOCG record, HOT SUBSET: only rows an ItemProviderLink(Locg) references and only the columns the modal/projection read (GATE question 15) | `LocgComicId INTEGER, LocgSeriesId INTEGER, SeriesName TEXT, Title TEXT, IssueNumber TEXT, Format TEXT, CoverDate TEXT, PageCount INTEGER, Description TEXT, CommunityRating REAL, RatingCount INTEGER, IsKey INTEGER, KeyType TEXT, Isbn TEXT, Upc TEXT, CoverPrice TEXT, CoverUrl TEXT, StoryCount INTEGER, ScrapedAt TEXT` | — |
| `MuSeries` | Id | MangaUpdates series (runtime leg: description + genres) | `Id INTEGER, Title TEXT, Year INTEGER, Type TEXT, Status TEXT, Completed INTEGER, Description TEXT, BayesianRating REAL, Url TEXT, ScrapedAt TEXT` | — |
| `ExternalWork` | Id | Open Library / Google Books work (the ComicVine-miss fallback leg) | `Id INTEGER, Provider TEXT, ProviderKey TEXT, Title TEXT, Authors TEXT, Publisher TEXT, FirstPublishYear INTEGER, Description TEXT, CoverImageUrl TEXT, Isbn TEXT, InfoUrl TEXT, FetchedAt TEXT` | `UNIQUE (Provider, ProviderKey)` |
| `BarneyProg` | ProgNo | 2000AD prog dates (reading-order recompute input; 2,313 rows) | `ProgNo INTEGER, CoverDate TEXT, Price TEXT, StripsJson TEXT, ScrapedAt TEXT` | — |
| `CvdbResolution` | CvdbTag | CVDB#### -> ComicVine entity name | `CvdbTag TEXT, ComicvineId INTEGER, ResolvedName TEXT, EntityType TEXT, Status TEXT, ResolvedAt TEXT` | — |
| `SeriesInferenceDecision` | Id | Reconciliation audit log + review queue (reversible) | `Id INTEGER, SeriesKey TEXT, Class TEXT, Action TEXT, Target TEXT, Confidence TEXT, EvidenceJson TEXT, State TEXT, UndoJson TEXT, DecidedBy TEXT, DecidedAt TEXT` | `(State, Class)` |
| `SeriesMatchReview` | Id | Mismatch detection triage state | `Id INTEGER, Scope TEXT, Key TEXT, State TEXT, Note TEXT, DecidedBy TEXT, DecidedAt TEXT` | `UNIQUE (Scope, Key)` |
| `DuplicateGroup` | Id | Dedup groups | `Id INTEGER, Relationship INTEGER, Confidence TEXT, Evidence TEXT, SuggestedKeeperItemId INTEGER, ReviewState TEXT, DetectedAt TEXT` | — |
| `DuplicateMember` | Id | Dedup group members | `Id INTEGER, DuplicateGroupId INTEGER FK DuplicateGroup, ItemId INTEGER FK Item, Role TEXT, SoleFileInFolder INTEGER` | `(ItemId)`, `(DuplicateGroupId)` |
| `UserItemState` | UserId, ItemId | One row per MT user x item: reading position + want/favorite/hidden (lastPage -1 = Finished is the ONLY finish signal; any write clears HiddenFromHistory) | `UserId INTEGER, ItemId INTEGER FK Item, LastPage INTEGER, LastSpineItemIndex INTEGER, LastScrollPercent REAL, Status INTEGER, WantToRead INTEGER, Favorite INTEGER, HiddenFromHistory INTEGER, UpdatedAt TEXT` | `(UserId, UpdatedAt DESC)`, `(UserId, WantToRead)`, `(ItemId)` |
| `GroupMark` | UserId, GroupType, GroupKey | Per-user marks on a group (series key = SeriesId; volume/collection/publisher/decade keys as the batch endpoint addresses them) | `UserId INTEGER, GroupType INTEGER, GroupKey TEXT, IsRead INTEGER, WantToRead INTEGER, IsFavorite INTEGER, Rating INTEGER, Notes TEXT, UpdatedAt TEXT` | `(UserId, GroupType, WantToRead)` |
| `DerivedTable` | Name | Registry of DERIVED tables/columns: the job that rebuilds each, its input fingerprint, last rebuild - replaces the SystemState fingerprint rows and enforces the edit-inputs rule structurally | `Name TEXT, RebuildJob TEXT, InputFingerprint TEXT, LastRebuiltAt TEXT, RowCount INTEGER` | — |
| `SystemState` | Key | Generic KV (scan bookkeeping only; fingerprints moved to DerivedTable) | `Key TEXT, Value TEXT` | — |
| `ScanRun` | Id | Audit of scans/imports (replaces boot-time backfills) | `Id INTEGER, RootId INTEGER, Kind TEXT, StartedAt TEXT, FinishedAt TEXT, ItemsSeen INTEGER, Added INTEGER, Changed INTEGER, Removed INTEGER, Error TEXT` | — |
| `MigrationProgress` | Stage | books-migrate-v1 resume state | `Stage TEXT, Cursor TEXT, Processed INTEGER, Total INTEGER, FinishedAt TEXT` | — |
| `KnownIdentity` | UserId | Last-seen identity payload per MT user (cache warmer input) | `UserId INTEGER, Username TEXT, IsAdmin INTEGER, MaturityCeiling INTEGER, KidsStyle TEXT, LastSeenAt TEXT` | — |
| `ItemFts` | rowid | FTS5 content-less index over ResolvedTitle, ResolvedSeries, ResolvedCreatorsCsv, ResolvedPublisher and the synopsis the pointer names; rebuilt at the end of books-resolve | `rowid=Item.Id, body TEXT` | — |

#### books-legs.db — offline warehouse (`BooksLegsDb`; no FK crosses the file boundary)

| Table | Key | Purpose | Columns | Indexes |
|---|---|---|---|---|
| `LocgComicRaw` | LocgComicId | Every LOCG row incl. the 73k stubs and the columns nothing reads at runtime; the containment reduction reads this | `LocgComicId INTEGER, LocgSeriesId INTEGER, SeriesName TEXT, Title TEXT, IssueNumber TEXT, Format TEXT, ReleaseDate TEXT, CoverDate TEXT, PageCount INTEGER, Description TEXT, CommunityRating REAL, RatingCount INTEGER, IsKey INTEGER, KeyType TEXT, KeyReason TEXT, Isbn TEXT, Upc TEXT, DistributorSku TEXT, CoverPrice TEXT, EstimatedValue TEXT, CoverUrl TEXT, Url TEXT, StoryCount INTEGER, StoryIdsJson TEXT, ScrapedAt TEXT` | — |
| `LocgCreatorRaw` | LocgComicId, Ordinal | CreatorsJson normalized for ALL LOCG rows (the hot ItemCredit(Source=Locg) is the matched subset) | `LocgComicId INTEGER, Ordinal INTEGER, Role TEXT, Name TEXT, PeopleId TEXT` | — |
| `LocgContainment` | Id | 391k forward/reverse containment edges - input to books-locg-containment only | `Id INTEGER, ContainerLocgComicId INTEGER, ContainedLocgComicId INTEGER, ChapterTitle TEXT, Ordinal INTEGER, Source TEXT, StoryId INTEGER, ScrapedAt TEXT` | `UNIQUE (ContainerLocgComicId, ContainedLocgComicId)`, `(ContainedLocgComicId)` |
| `LocgSeries` | LocgSeriesId | LOCG series (python leg) | `LocgSeriesId INTEGER, Name TEXT, Publisher TEXT, YearBegin INTEGER, YearEnd INTEGER, YearText TEXT, IssueCount INTEGER, ImportedAt TEXT` | — |
| `LocgSeriesInference` | GcdSeriesId | GCD-series -> LOCG-series inference | `GcdSeriesId INTEGER, LocgSeriesId TEXT, SeriesName TEXT, Support INTEGER, ImportedAt TEXT` | — |
| `GcdIssue` | GcdIssueId | GCD issues (the story-genre fold reads them into ItemTag(Source=Gcd)) | `GcdIssueId INTEGER, GcdSeriesId INTEGER, SeriesName TEXT, SeriesYearBegan INTEGER, Number TEXT, Title TEXT, KeyDate TEXT, PublicationDate TEXT, ValidIsbn TEXT, Isbn TEXT, Barcode TEXT, PageCount INTEGER, Price TEXT, Publisher TEXT, Format TEXT, VariantOfId INTEGER, VariantName TEXT, ImportedAt TEXT, StoryGenres TEXT` | `(GcdSeriesId)`, `(ValidIsbn)`, `(Barcode)` |
| `GcdSeries` | GcdSeriesId | GCD series | `GcdSeriesId INTEGER, Name TEXT, SortName TEXT, YearBegan INTEGER, YearEnded INTEGER, Publisher TEXT, Format TEXT, IssueCount INTEGER, HasIsbn INTEGER, HasBarcode INTEGER, Binding TEXT, Notes TEXT, ImportedAt TEXT` | — |
| `OpenLibraryEdition` | Isbn | ISBN-keyed OL editions | `Isbn TEXT, Title TEXT, Subtitle TEXT, AuthorsJson TEXT, Publishers TEXT, PublishDate TEXT, Pages INTEGER, SubjectsJson TEXT, CoverUrl TEXT, OlEditionKey TEXT, OlWorkKey TEXT, SeriesString TEXT, PhysicalFormat TEXT, ImportedAt TEXT` | `(OlWorkKey)` |
| `OpenLibraryWork` | WorkKey | OL works | `WorkKey TEXT, Title TEXT, SubjectsJson TEXT, SeriesString TEXT, EditionCount INTEGER, ImportedAt TEXT` | — |
| `OlSeriesInference` | GcdSeriesId | GCD-series -> OL-work inference | `GcdSeriesId INTEGER, OlWorkKey TEXT, SeriesString TEXT, SubjectsJson TEXT, IsbnSupport INTEGER, ImportedAt TEXT` | — |
| `MarvelSeries` | MarvelSeriesId | Marvel identity leg | `MarvelSeriesId INTEGER, Slug TEXT, Name TEXT, YearStart INTEGER, YearEnd INTEGER, ScrapedAt TEXT` | — |
| `MarvelIssue` | MarvelIssueId | Marvel identity leg | `MarvelIssueId INTEGER, MarvelSeriesId INTEGER, Number TEXT, Slug TEXT, ScrapedAt TEXT` | — |
| `MarvelSeriesLink` | SeriesId | Marvel series match (128 rows) | `SeriesId INTEGER, MarvelSeriesId INTEGER, Status TEXT, Confidence REAL, MatchedKey TEXT, CreatedAt TEXT` | — |
| `BarcodeScan` | ItemId | Barcode sweep results | `ItemId INTEGER, CodesJson TEXT, PagesScanned INTEGER, Error TEXT, ScannedAt TEXT` | — |
| `ProviderResponseCache` | Provider, RequestKey | Cache-first API bodies (ComicVine today) | `Provider INTEGER, RequestKey TEXT, ResponseJson TEXT, FetchedAt TEXT` | — |
| `LinkCandidates` | Scope, Key, Provider | CandidatesJson for links that are NOT Pending/Multiple (audit trail; the hot links keep candidates only while a decision is open) | `Scope INTEGER, Key TEXT, Provider INTEGER, CandidatesJson TEXT` | — |
| `MuSeriesRaw` | MuSeriesId | MangaUpdates raw payload + JSON lists | `MuSeriesId INTEGER, GenresJson TEXT, CategoriesJson TEXT, RawJson TEXT` | — |
| `CvVolumeRaw` | CvVolumeId | ComicVine volume JSON lists (concepts/characters/locations/objects/teams) - all '[]' today | `CvVolumeId INTEGER, ConceptsJson TEXT, CharactersJson TEXT, LocationsJson TEXT, ObjectsJson TEXT, TeamsJson TEXT` | — |

<!-- /generated:catalog -->

## 15. Old → new mapping (every v1 table and column)
<!-- generated:mapping -->
| v1 table | rows | → v2 (file) | stage | column rules (renames / transforms / drops) |
|---|---:|---|---|---|
| `LibraryPaths` | 2 | `LibraryRoot` (hot) | roots | `Category` ->Kind; `IsCalibreLibrary` ->IsCalibre; `AuthorizedUsersJson` drop:per-folder/root ACL dropped (2 distinct lists over 54,418 folders; gate = BooksAccess + maturity) _(+2 carried as-is)_ |
| `Folders` | 54,418 | `Folder` (hot) | folders | `FolderPath` ->Path; `FolderName` ->Name; `Category` ->Kind; `AuthorizedUsersJson` drop:per-folder ACL dropped (uniform lists; LIKE scan on every list query removed); **+** `RootId` xf:root_of_path; **+** `Depth` xf:depth_of_path; **+** `TopFolderId` xf:top_ancestor; **+** `HasIcon` xf:icon_file_exists _(+5 carried as-is)_ |
| `FolderAggregates` | 54,418 | `Folder` (hot) | folders | `FolderId` ->Id; `DescendantComicCount` ->DescendantItemCount; `UpdatedAt` drop:aggregate refresh is a ScanRun fact now _(+1 carried as-is)_ |
| `Publishers` | 2,635 | `Publisher` (hot) | publishers | all as-is _(+3 carried as-is)_ |
| `Comics` | 141,010 | `Item` (hot), `ItemState` (hot), `ItemSignature` (hot), `ComicEmbedded` (hot), `BookDetail` (hot), `ItemCredit` (hot), `ItemTag` (hot), `Rating` (hot) | items | `ParentFolderId` ->Item.FolderId; `Category` ->Item.Kind; `FilePath` ->Item.Path; `FileExtension` ->Item.Extension; `FolderGroupId` ->Item.TopFolderId; `ExcludedFromLibrary` ->Item.IsExcluded; `KeepInDirectory` same; `PublisherId` same; `Title` same; `NormalizedTitle` same; `FileName` same; `FileSize` same; `FileModifiedAt` same; `IndexedAt` same; `PageCount` same; `IsBroken` ->ItemState.IsBroken; `BrokenReason` ->ItemState.BrokenReason; `BrokenCheckedAt` ->ItemState.BrokenCheckedAt; `ThumbnailError` ->ItemState.ThumbnailError; `ThumbnailCheckedAt` ->ItemState.ThumbnailCheckedAt; `CoverWidth` ->ItemState.CoverWidth; `CoverHeight` ->ItemState.CoverHeight; `CoverDimsComputedFor` ->ItemState.CoverDimsComputedFor; `ExclusionReason` ->ItemState.ExclusionReason; `ExcludedAt` ->ItemState.ExcludedAt; `ContentFingerprint` ->ItemSignature.ContentFingerprint; `CoverPHash` ->ItemSignature.CoverPHash; `PageSignature` ->ItemSignature.PageSignature; `SignaturesComputedFor` ->ItemSignature.SignaturesComputedFor; `SeriesName` xf:split_by_kind:ComicEmbedded.Series|BookDetail.SeriesName; `SeriesIndex` xf:split_by_kind:ComicEmbedded.Number|BookDetail.SeriesIndex; `AltSeriesName` ->ComicEmbedded.AltSeries; `AltSeriesIndex` ->ComicEmbedded.AltNumber; `Volume` ->ComicEmbedded.Volume; `IssueTitle` xf:split_by_kind:ComicEmbedded.Title|drop(duplicate of Title for books); `Description` xf:split_by_kind:ComicEmbedded.Summary|BookDetail.Description; `Publisher` xf:split_by_kind:ComicEmbedded.Publisher|BookDetail.Publisher; `PublicationDate` xf:split_by_kind:ComicEmbedded.PublicationDate|BookDetail.PublishedOn; `Language` xf:split_by_kind:ComicEmbedded.Language|BookDetail.Language; `Identifier` xf:split_by_kind:ComicEmbedded.Identifier|BookDetail.Isbn; `Writers` xf:split_by_kind:ComicEmbedded.Writers+ItemCredit(Writer,ComicInfo)|ItemCredit(Author,Calibre); `Pencillers` ->ComicEmbedded.Pencillers +ItemCredit; `Inker` ->ComicEmbedded.Inker +ItemCredit; `Colorist` ->ComicEmbedded.Colorist +ItemCredit; `Letterer` ->ComicEmbedded.Letterer +ItemCredit; `CoverArtist` ->ComicEmbedded.CoverArtist +ItemCredit; `Editor` ->ComicEmbedded.Editor +ItemCredit; `Genre` xf:comicinfo_genre:ComicEmbedded.Genre+ItemTag(genre,ComicInfo|Cv); `Tags` xf:split_by_kind:ComicEmbedded.Tags+ItemTag(tag,ComicInfo)|ItemTag(tag,Calibre); `Characters` ->ComicEmbedded.Characters; `Teams` ->ComicEmbedded.Teams; `Locations` ->ComicEmbedded.Locations; `StoryArc` ->ComicEmbedded.StoryArc; `Web` ->ComicEmbedded.Web; `Format` ->ComicEmbedded.Format; `BlackAndWhite` ->ComicEmbedded.BlackAndWhite; `Manga` ->ComicEmbedded.Manga; `Notes` ->ComicEmbedded.Notes; `Count` ->ComicEmbedded.Count; `AgeRating` ->ComicEmbedded.AgeRating; `EmbeddedRating` ->ComicEmbedded.Rating; `UserRating` drop:0 rows in v1; per-item user ratings arrive as Rating(Source=User) in v2; `MetadataVersion` drop:scanner-private constant 1; `Imprint` ->ComicEmbedded.Imprint; `GTIN` drop:always NULL or constant in v1 (column census); `SeriesGroup` drop:always NULL or constant in v1 (column census); `AlternateCount` drop:always NULL or constant in v1 (column census); `Translator` drop:always NULL or constant in v1 (column census); `StoryArcNumber` drop:always NULL or constant in v1 (column census); `MainCharacterOrTeam` drop:constant ('Superman') and write-only; **+** `Item.ContainerFormat` xf:sniff_or_extension; **+** `Item.CalibreBookId` xf:calibre_link_json; **+** `Item.SeriesId` derived:books-resolve-series; **+** `Item.CoverAspect` derived:books-resolve (ItemState dims, clamp 0.35-1.6, default 0.66); **+** `Item.Resolved*` derived:books-resolve; **+** `Item.RootId` xf:root_of_path _(+1 carried as-is)_ |
| `ComicParsedDetails` | 118,926 | `ComicDetail` (hot) | comic-details | `ComicId` ->ItemId; `Series` ->ParsedSeriesKey; `Format` xf:enum:Format (33 spellings -> enum + FormatRaw); `Confidence` xf:enum:Confidence; `SeriesSource` xf:enum:Source; `IssueSource` xf:enum:Source; `YearSource` xf:enum:Source; `PublisherSource` xf:enum:Source; `ClaudeSeriesMetadataId` drop:series-scoped; the Insight(Series) current row is reached via Item.SeriesId; `ComicvineVolumeId` drop:series-scoped -> Series.CvVolumeId (derived from SeriesKeyLink); `ExternalWorkId` drop:series-scoped -> Series.ExternalWorkId (derived); `SeriesId` ->Item.SeriesId (materialized; still DERIVED) _(+11 carried as-is)_ |
| `Series` | 19,481 | `Series` (hot) | series | `ResolvedName` ->Name; `ComicvineVolumeId` ->CvVolumeId _(+10 carried as-is)_ |
| `SeriesParsedKeys` | 21,566 | `SeriesAlias` (hot) | series-aliases | all as-is _(+2 carried as-is)_ |
| `SeriesMergeLogs` | 44,261 | `SeriesMerge` (hot) | series-merges | `CanonicalKey` drop:constant '' in v1 _(+3 carried as-is)_ |
| `SeriesAliases` | 19 | **drop** | — | 19 manual rows the resolution pipeline never reads (series-reconciliation skill); exported to data/books/v1/series-aliases.json |
| `ComicvineSeriesLinks` | 22,820 | `SeriesKeyLink` (hot), `LinkCandidates` (legs) | series-links | `SeriesName` ->ParsedKey; `ComicvineVolumeId` ->ProviderKey; `MatchScore` ->Score; `CandidatesJson` xf:candidates:hot only while Status in (Pending, Multiple), else LinkCandidates; StoredTopScore extracted; `AttemptedAt` same; `ErrorMessage` ->Error; `SearchQuery` drop:reconstructible from ParsedKey; `Status` xf:enum:LinkStatus; **+** `Provider` const:Cv _(+1 carried as-is)_ |
| `ExternalSeriesLinks` | 4,340 | `SeriesKeyLink` (hot), `LinkCandidates` (legs) | series-links | `SeriesName` ->ParsedKey; `ExternalWorkId` ->ProviderKey; `MatchScore` ->Score; `MatchedProvider` drop:constant 'openlibrary' (column census); `CandidatesJson` xf:candidates; `ErrorMessage` drop:always NULL; `SearchQuery` drop:reconstructible; `AttemptCount` same; `Status` xf:enum:LinkStatus; **+** `Provider` const:External _(+1 carried as-is)_ |
| `MangaUpdatesMatches` | 835 | `MuSeriesLink` (hot), `LinkCandidates` (legs) | series-links | `MatchMethod` ->Method; `CandidatesJson` xf:candidates _(+6 carried as-is)_ |
| `ComicvineMatches` | 114,753 | `ItemProviderLink` (hot), `LinkCandidates` (legs) | item-links | `ComicId` ->ItemId; `ComicvineIssueId` ->ProviderKey; `ComicvineVolumeId` ->SecondaryKey; `Status` xf:enum:LinkStatus; `LastAttemptedAt` ->AttemptedAt; `ErrorMessage` ->Error; `SearchQuery` drop:reconstructible; `CandidatesJson` xf:candidates; **+** `Provider` const:Cv; **+** `Quality` const:Unknown _(+2 carried as-is)_ |
| `LocgMatches` | 84,874 | `ItemProviderLink` (hot) | item-links | `ComicId` ->ItemId; `LocgComicId` ->ProviderKey; `LocgSeriesId` drop:always NULL; `Slug` drop:always NULL; `Status` xf:enum:LinkStatus; `MatchMethod` ->Method; `MatchQuality` xf:enum:LinkQuality ('span-corroborated' -> High + Method); `LastScrapedAt` ->AttemptedAt; `ErrorMessage` ->Error; **+** `Provider` const:Locg _(+3 carried as-is)_ |
| `GcdMatches` | 118,528 | `ItemProviderLink` (hot) | item-links | `ComicId` ->ItemId; `GcdIssueId` ->ProviderKey; `GcdSeriesId` ->SecondaryKey; `Status` xf:enum:LinkStatus; `MatchMethod` ->Method; `CandidateCount` drop:derivable; `ErrorMessage` ->Error; `CreatedAt` ->AttemptedAt; **+** `Provider` const:Gcd _(+3 carried as-is)_ |
| `BarneyMatches` | 2,332 | `ItemProviderLink` (hot) | item-links | `ComicId` ->ItemId; `ProgNo` ->ProviderKey; `MatchMethod` ->Method; `CreatedAt` ->AttemptedAt; **+** `Provider` const:Barney; **+** `Status` const:Matched |
| `MarvelMatches` | 14 | `ItemProviderLink` (hot) | item-links | `ComicId` ->ItemId; `MarvelIssueId` ->ProviderKey; `MatchMethod` ->Method; `CreatedAt` ->AttemptedAt; **+** `Provider` const:Marvel; **+** `Status` const:Matched _(+1 carried as-is)_ |
| `InducksMatches` | 14 | `ItemProviderLink` (hot) | item-links | `ComicId` ->ItemId; `IssueCode` ->ProviderKey; `PublicationCode` ->SecondaryKey; `Status` xf:enum:LinkStatus; `MatchMethod` ->Method; `CreatedAt` ->AttemptedAt; **+** `Provider` const:Inducks _(+1 carried as-is)_ |
| `MarvelSeriesMatches` | 128 | `MarvelSeriesLink` (legs) | legs | all as-is _(+6 carried as-is)_ |
| `ComicvineVolumes` | 14,357 | `CvVolume` (hot), `CvVolumeRaw` (legs) | cv | `ComicvineId` ->Id; `PublisherId` drop:CV publisher id unused (PublisherName is what the projection reads); `ConceptsJson` ->CvVolumeRaw.ConceptsJson; `CharactersJson` ->CvVolumeRaw.CharactersJson; `LocationsJson` ->CvVolumeRaw.LocationsJson; `ObjectsJson` ->CvVolumeRaw.ObjectsJson; `TeamsJson` ->CvVolumeRaw.TeamsJson _(+9 carried as-is)_ |
| `ComicvineIssues` | 70,591 | `CvIssue` (hot) | cv | `ComicvineId` ->Id _(+10 carried as-is)_ |
| `ComicvineApiCaches` | 20,286 | `ProviderResponseCache` (legs) | legs | **+** `Provider` const:Cv _(+3 carried as-is)_ |
| `ComicvineCollectedEditions` | 915 | `CollectedEditionSpan` (hot) | collected-editions | `ComicId` ->ItemId; `ComicvineVolumeId` ->ProviderRef; `ScrapedAt` ->CreatedAt; **+** `Source` const:Cv _(+5 carried as-is)_ |
| `GcdCollectedEditions` | 4,502 | `CollectedEditionSpan` (hot) | collected-editions | `ComicId` ->ItemId; `GcdIssueId` drop:always NULL; `SourceSeries` ->ProviderRef; `MatchBy` ->Note; **+** `Source` const:Gcd _(+8 carried as-is)_ |
| `LocgCollectedEditions` | 3,447 | `CollectedEditionSpan` (hot) | collected-editions | `ComicId` ->ItemId; `LocgComicId` ->ProviderRef; `ContainedCount` ->Note; **+** `Source` const:Locg _(+7 carried as-is)_ |
| `CuratedCollectedEditions` | 1,047 | `CollectedEditionSpan` (hot) | collected-editions | `ComicId` ->ItemId; `Source` drop:constant 'claude' (becomes Source=Curated); **+** `Source` const:Curated _(+7 carried as-is)_ |
| `ComicCollectionNodes` | 118,322 | `CollectionNode` (hot) | collection-nodes | `ComicId` ->ItemId; `CollectionLevel` ->Level; `ParentComicId` ->ParentItemId; `TrackRole` xf:enum:TrackRole; `SpanSource` xf:enum:SpanSource _(+5 carried as-is)_ |
| `ComicReadingOrder` | 126,035 | `ReadingOrderEntry` (hot) | reading-order | `ComicId` ->ItemId; `GroupKey` xf:groupkey_to_seriesid; `Source` xf:enum:ReadingOrderSource; `Confidence` xf:enum:Confidence; `ReadDatePrecision` xf:enum:DatePrecision _(+8 carried as-is)_ |
| `ClaudeSeriesMetadata` | 23,418 | `Insight` (hot) | insights | `Id` drop:v2 Insight.Id is new; v1 id kept in SourceKey suffix; `SeriesName` xf:insight_subject:Series via SeriesAlias/Series name (lower); SourceKey=SeriesName; the ~1,000 rows that resolve to no series are NOT carried - exported to data/books/v1/orphan-insights.json; `KnownSeries` ->Recognized; `Confidence` xf:enum:Confidence; `TagsCsv` drop:rollup; rebuilt as SeriesTag(Source=AI) from InsightTag; **+** `SubjectKind` const:Series; **+** `Rank` xf:model_rank; **+** `IsCurrent` derived:books-resolve-insights _(+9 carried as-is)_ |
| `ClaudeSeriesTags` | 245,499 | `InsightTag` (hot) | insights | `MetadataId` ->InsightId (via the v1 id kept during migration); `Tag` ->Value _(+1 carried as-is)_ |
| `ClaudeBookMetadata` | 6,168 | `Insight` (hot) | insights | `ComicId` ->SubjectId; `KnownBook` ->Recognized; `Confidence` xf:enum:Confidence; `YearPublished` ->YearBegin; `TagsCsv` drop:rollup; rebuilt as ItemTag(Source=AI); **+** `SubjectKind` const:Item; **+** `Rank` xf:model_rank; **+** `IsCurrent` const:1 (one row per book in v1) _(+6 carried as-is)_ |
| `ClaudeBookTags` | 22,997 | `InsightTag` (hot) | insights | `ComicId` ->InsightId (via the book's Insight row); `Tag` ->Value _(+1 carried as-is)_ |
| `KidSafeTags` | 2 | `KidSafeTag` (hot) | tags | all as-is _(+4 carried as-is)_ |
| `TagAliases` | 174 | `TagAlias` (hot) | tags | all as-is _(+4 carried as-is)_ |
| `Tags` | 0 | **drop** | — | 0 rows; the dead second tag system |
| `ComicTagAssociation` | 0 | **drop** | — | 0 rows; the dead second tag system |
| `LibraryComicRatings` | 114,798 | `Rating` (hot) | ratings | `ComicId` ->TargetId; `Rating` ->Value; `Sources` ->Note (appended); **+** `TargetKind` const:Item; **+** `Source` const:Library _(+3 carried as-is)_ |
| `LibrarySeriesRatings` | 17,337 | `Rating` (hot) | ratings | `SeriesId` ->TargetId; `Rating` ->Value; `Sources` ->Note (appended); **+** `TargetKind` const:Series; **+** `Source` const:Library _(+3 carried as-is)_ |
| `LibraryRatingOverrides` | 75 | `Rating` (hot) | ratings | `TargetType` ->TargetKind; `Rating` ->Value; `CreatedAt` ->GeneratedAt; **+** `Source` const:Override; **+** `IsOverride` const:1 _(+2 carried as-is)_ |
| `LocgComics` | 156,839 | `LocgComic` (hot), `LocgComicRaw` (legs), `LocgCreatorRaw` (legs), `ItemCredit` (hot) | locg | `RawJson` drop:always NULL (column census); `CreatorsJson` xf:creators_json -> LocgCreatorRaw (all rows) + ItemCredit(Source=Locg) for matched rows; `ReleaseDate` ->LocgComicRaw.ReleaseDate; `KeyReason` ->LocgComicRaw.KeyReason; `DistributorSku` ->LocgComicRaw.DistributorSku; `EstimatedValue` ->LocgComicRaw.EstimatedValue; `Url` ->LocgComicRaw.Url; `StoryIdsJson` ->LocgComicRaw.StoryIdsJson _(+19 carried as-is)_ — hot LocgComic keeps only rows referenced by ItemProviderLink(Locg); LocgComicRaw keeps every row |
| `LocgContainments` | 391,207 | `LocgContainment` (legs) | legs | all as-is _(+8 carried as-is)_ |
| `LocgSeries` | 8,376 | `LocgSeries` (legs) | legs | all as-is _(+8 carried as-is)_ |
| `LocgSeriesInference` | 11,830 | `LocgSeriesInference` (legs) | legs | all as-is _(+5 carried as-is)_ |
| `LocgApiCaches` | 0 | **drop** | — | 0 rows; LOCG HTTP lives in the Node scraper, never in C# |
| `GcdIssues` | 76,869 | `GcdIssue` (legs) | legs | `TagsCsv` drop:rollup; rebuilt as ItemTag(Source=Gcd) by the fold _(+19 carried as-is)_ |
| `GcdSeries` | 17,152 | `GcdSeries` (legs) | legs | all as-is _(+13 carried as-is)_ |
| `MangaUpdatesSeries` | 396 | `MuSeries` (hot), `MuSeriesRaw` (legs) | mu | `MuSeriesId` ->Id; `GenresJson` ->MuSeriesRaw.GenresJson (+SeriesTag(Source=Mu) via fold); `CategoriesJson` ->MuSeriesRaw.CategoriesJson; `RawJson` ->MuSeriesRaw.RawJson; `TagsCsv` drop:rollup; rebuilt as SeriesTag(Source=Mu) _(+9 carried as-is)_ |
| `BarneyProgs` | 2,313 | `BarneyProg` (hot) | barney | all as-is _(+5 carried as-is)_ |
| `MarvelSeries` | 5,583 | `MarvelSeries` (legs) | legs | all as-is _(+6 carried as-is)_ |
| `MarvelIssues` | 1,079 | `MarvelIssue` (legs) | legs | all as-is _(+5 carried as-is)_ |
| `ExternalWorks` | 691 | `ExternalWork` (hot) | external | `SubjectsJson` drop:folded into SeriesTag(Source=External) by the whitelist fold; raw subjects live in OpenLibraryWork.SubjectsJson; `TagsCsv` drop:rollup; rebuilt as SeriesTag(Source=External) _(+12 carried as-is)_ |
| `OpenLibraryEditions` | 19,265 | `OpenLibraryEdition` (legs) | legs | all as-is _(+14 carried as-is)_ |
| `OpenLibraryWorks` | 17,754 | `OpenLibraryWork` (legs) | legs | all as-is _(+6 carried as-is)_ |
| `OlSeriesInference` | 7,250 | `OlSeriesInference` (legs) | legs | all as-is _(+6 carried as-is)_ |
| `BarcodeScans` | 6,902 | `BarcodeScan` (legs) | legs | `ComicId` ->ItemId _(+4 carried as-is)_ |
| `CvdbResolutions` | 1,037 | `CvdbResolution` (hot) | tags | all as-is _(+6 carried as-is)_ |
| `SeriesInferenceDecisions` | 2,915 | `SeriesInferenceDecision` (hot) | reconciliation | all as-is _(+11 carried as-is)_ |
| `SeriesMatchReviews` | 7 | `SeriesMatchReview` (hot) | reconciliation | all as-is _(+7 carried as-is)_ |
| `DuplicateGroups` | 3,703 | `DuplicateGroup` (hot) | dedup-groups | `SuggestedKeeperComicId` ->SuggestedKeeperItemId _(+6 carried as-is)_ |
| `DuplicateMembers` | 7,649 | `DuplicateMember` (hot) | dedup-members | `ComicId` ->ItemId _(+4 carried as-is)_ |
| `Bookmarks` | 766 | `UserItemState` (hot) | user-activity | `Id` drop:composite key (UserId, ItemId); `Username` xf:username_to_userid (only user 2 -> 1); `ComicId` ->ItemId; `Status` xf:enum:ReadStatus _(+5 carried as-is)_ |
| `ComicUserLists` | 4 | `UserItemState` (hot) | user-activity | `Id` drop:composite key; `Username` xf:username_to_userid; `ComicId` ->ItemId; `ListType` xf:list_type -> WantToRead=1; `AddedAt` ->UpdatedAt (when no position row exists) |
| `GroupUserMetadata` | 68 | `GroupMark` (hot), `UserItemState` (hot) | user-activity | `Id` drop:composite key; `Username` xf:username_to_userid; `GroupType` xf:group_type:'comic' rows -> UserItemState(Favorite/WantToRead/Notes); others -> GroupMark enum; `IsFavorite` same _(+6 carried as-is)_ |
| `SeriesUserLists` | 0 | **drop** | — | 0 rows; superseded by GroupMark |
| `Users` | 10 | **drop** | — | identity moves to the site (MT Users/UserSettings; BooksIdentity header) |
| `Sessions` | 41 | **drop** | — | identity moves to the site (MT Users/UserSettings; BooksIdentity header) |
| `SiteSettings` | 1 | `SystemState` (hot) | system-state | `UpdatedAt` drop:KV has no timestamp _(+2 carried as-is)_ |
| `SystemState` | 7 | `DerivedTable` (hot) | system-state | `Key` xf:fingerprint_key_to_table (the 7 *_fingerprint rows become DerivedTable.InputFingerprint; other keys -> SystemState); `Value` ->InputFingerprint |
| `ComicvineCharacters` | 0 | **drop** | — | 0 rows; scraper code that wrote it is deleted in the port |
| `ComicvinePeople` | 0 | **drop** | — | 0 rows |
| `ComicvineTeams` | 0 | **drop** | — | 0 rows |
| `ComicvineStoryArcs` | 0 | **drop** | — | 0 rows |
| `ComicvineIssueCharacters` | 0 | **drop** | — | 0 rows |
| `ComicvineIssuePeople` | 0 | **drop** | — | 0 rows |
| `ComicvineIssueTeams` | 0 | **drop** | — | 0 rows |
| `ComicvineIssueStoryArcs` | 0 | **drop** | — | 0 rows |
| `ComicvineSeries` | 0 | **drop** | — | 0 rows; 'never populated - do not use' (unified-data skill) |
| `ComicvineVolumeSeries` | 0 | **drop** | — | 0 rows |
| `ComicFts` | 141,010 | `ItemFts` (hot) | fts | `body` xf:rebuild from Resolved* at the end of books-resolve |
| `ComicFts_config` | 1 | **drop** | — | FTS shadow table |
| `ComicFts_data` | 1,501 | **drop** | — | FTS shadow table |
| `ComicFts_docsize` | 141,010 | **drop** | — | FTS shadow table |
| `ComicFts_idx` | 1,515 | **drop** | — | FTS shadow table |
| `sqlite_sequence` | 17 | **drop** | — | SQLite internal |
<!-- /generated:mapping -->

## 16. Size attribution
<!-- generated:size -->
Column payload attributed by the mapping (v1 total 571.4 MB of column bytes; the file also carries indexes/FTS/page overhead — v1 file was 700 MB for 501 MB of column bytes):

- **hot**: 330.3 MB — largest: `LocgComic` 70.2, `ComicEmbedded` 57.1, `Item` 44.7, `SeriesKeyLink` 31.6, `ItemProviderLink` 25.2, `Rating` 19.7
- **legs**: 227.9 MB — largest: `LocgComicRaw` 136.9, `LocgContainment` 30.3, `ProviderResponseCache` 28.2, `GcdIssue` 11.6, `OpenLibraryEdition` 6.8, `MuSeriesRaw` 4.2
- **dropped**: 13.2 MB

Not counted above (new in v2): `ItemCredit` (est. ≈ 8 MB from `CreatorsJson` for matched rows + ComicInfo creators), `ItemTag`/`SeriesTag` (≈ 12 MB replacing the five CSV rollups), `Item.Resolved*` scalars (≈ 15 MB; no synopsis text). Expected hot file ≈ **hot column bytes × 1.4** after indexes and FTS.
<!-- /generated:size -->

## 17. Migration stage order + verification (R4)

Stages, each paged by PK with `WHERE pk > cursor LIMIT batch`, one transaction per batch with `PRAGMA defer_foreign_keys = ON`, idempotent upserts, `MigrationProgress` after each batch: `roots` → `folders` (two-pass for `ParentId`) → `publishers` → `series` → `series-aliases` → `series-merges` → `items` (fans out to `Item`/`ItemState`/`ItemSignature`/`ComicEmbedded`/`BookDetail`/`ItemCredit`/`ItemTag`/`Rating`) → `comic-details` → `reading-order` → `collection-nodes` → `collected-editions` → `cv` → `locg` (hot subset + raw + creators) → `mu` → `barney` → `external` → `legs` (the rest of the warehouse) → `series-links` (+ `LinkCandidates`, `StoredTopScore`) → `item-links` → `insights` (+ `InsightTag`; `IsCurrent` computed) → `ratings` → `tags` (`TagAlias`, `KidSafeTag`, `CvdbResolution`) → `reconciliation` → `dedup-groups` → `dedup-members` → `user-activity` (user 2 → 1 only) → `system-state` (fingerprints → `DerivedTable`) → `resolve` (the resolver over the whole file: `Resolved*`, folds, `IsCurrent`) → `fts` → `analyze`.

`--verify` asserts: id-set equality for `Item`/`Series`/`Folder`/`Publisher`; row counts per mapping (incl. the hot `LocgComic` subset = distinct Locg link keys); every v1 item with a `ClaudeSeriesMetadataId` reaches a current `Insight(Series)` through `Item.SeriesId`; user 2's counts; enum coverage; `PRAGMA integrity_check`; **hot-set replay** — the ported browse/facet/group/band/home queries run on the v2 file and their `EXPLAIN QUERY PLAN` + timings are compared to `data/books/census/queries.md`: no TEMP B-TREE over a ≥50k-row table, facets < 100 ms warm without the 48 h cache. That replay, not the size number, is R4's proof of the redesign.

## 18. Resolution of the 31 audit conflicts

- **No EF entity (15)**: `BarcodeScans`, `GcdSeries`, `InducksMatches`, `LibraryRatingOverrides`, `LocgSeries`, `LocgSeriesInference`, `MarvelIssues`, `MarvelMatches`, `MarvelSeries`, `MarvelSeriesMatches`, `OlSeriesInference`, `OpenLibraryEditions`, `OpenLibraryWorks`, `SystemState`, `ComicTagAssociation` → all but the last get entities (legs, or `Rating`/`ItemProviderLink`/`DerivedTable` in hot); `ComicTagAssociation` dropped (0 rows).
- **No runtime reader (2)**: `BarneyProgs` (reading-order job input) → hot, small; `LocgContainments` → legs.
- **Runtime code still references a dropped table (14)**: the nine empty ComicVine tables, `Tags`, `Users`, `Sessions`, `SeriesUserLists`, `SeriesAliases`, `SiteSettings` → the referencing code is deleted in the port (scraper writers for the empty CV tables, the two dead tag endpoints, the auth stack, the stranded-marks migrator, the manual alias reader, the settings row), listed per file in the R6 port checklist.

## 19. Gate questions — measured and resolved (2026-08-25)

1. **Keep `Imprint`, `AgeRating`, `Count`, `Notes`, `Locations` in `ComicEmbedded`.** They are not empty: 7,129 / 525 / 5,384 / 25,509 / 3,613 rows. `Imprint` (64 distinct, e.g. publisher lines) and `AgeRating` (10 distinct: Teen, Mature, …) are real signals a later facet or the kids gate can use; `Notes` carries scraper provenance incl. Amazon ASINs; `Locations` are CV entity names. Nothing reads them today, so they cost only their bytes (a few MB) as raw record.
2. **Drop ComicInfo `<Rating>` as a rating source.** 6 of 141,010 comics have one (values 3–4) and all six already have a library rating; `UserRating` has 0 rows. `Rating.Source` is `{User, AI, Locg, Mu, Library, Override}`; the raw value stays in `ComicEmbedded.Rating`.
3. **No synopsis in FTS.** Today's index (`LibraryScannerService.RebuildFtsAsync`) covers title, series (three spellings), issue title, publisher, every creator column, tags, characters, teams, locations, story arc, genre, parsed series/publisher/event — and no description. v2 indexes the same set on the resolved values; adding descriptions would change what a search matches and add ~78 MB of index for no UI feature that asks for it.
4. **Carry only the orphan insight rows that resolve to a series (57); export the rest (~1,000) to `data/books/v1/orphan-insights.json`.** Only 21 of the 1,079 match any parsed key; the samples are leading-issue-number artifacts of the round-3 name fixes (`008 Supergirl`, `10 Justice League of America`, `47 Justice League of America`) whose proper series already have an insight row. 641 of them have tags, all of which apply to a series that no longer exists under that key. Keeping them as NULL-subject rows would make `IsCurrent` selection and the kids gate reason about ghosts; re-inference on the real series (the `claude-inference` pipeline) is cheaper than reconciling them.

## 20. Review amendments — adopted / refuted

| # | Amendment | Outcome |
|---|---|---|
| 1 | Legs as a second DbContext, `ATTACH` only in two verbs, skip-and-report on missing ids | Adopted (§2, invariant 4) |
| 2 | Projection = `Item` + `Series` only; `CoverAspect`, `SeriesId`, `ResolvedRating` materialized; hot-set replay as R4's proof | Adopted (§5, §17); grouped paths additionally PK-join `ReadingOrderEntry`/`CollectionNode` — stated |
| 3 | `DerivedTable` carries job + fingerprint; `MuSeriesLink` in the merge touch list | Adopted (§4) |
| 4 | `StoredTopScore`, candidates-only-while-open, curated spans carried | Adopted (§11) |
| 5 | Facets on `ItemTag`/`SeriesTag` with covering indexes | Adopted (§7) |
| 6 | Ratings normalized 0–100 at write, `ResolvedRating` materialized | Adopted (§10) |
| 7 | GATE-1/2 append-only + `IsCurrent`, one `Insight` table | Adopted as decided (§9) |
| 8 | GATE-3 one `ItemCredit` | Adopted as decided (§8) |
| 9 | Do not materialize synopsis; pointer | Adopted (§5); the UI still shows the real description |
| 10 | Define FTS content + rebuild trigger | Adopted (§5) |
| 11 | Id preservation as invariant + verify | Adopted (§1, §17) |
| 12 | ACL drop with evidence | Adopted (§1) |
| 13 | `CalibreBookId` uniqueness; books insight pipeline writes `Insight(Item)` | Adopted (§3, §9) |
| 14 | Two-file size estimate, actuals in `--verify` | Adopted (§16, §17) |
| 15 | Hot `LocgComic` = modal columns only | Adopted (§11) — the census usage table backs it |

## 21. Coverage report (generated by `v2_mapping_check.py`)
<!-- generated:coverage -->

- v1 tables in census: 80; mapped: 63; dropped with reason: 22
- v1 columns: mapped 547, dropped 146, total 693
- v2 tables: 63 (hot 45, legs 18); migration targets hit: 59; new/derived-only: 4

## Enum coverage vs the frozen DB

- `ReadingOrderEntry.Source` <- `ComicReadingOrder.Source`: 8 distinct values, missing: none
- `DatePrecision` <- `ComicReadingOrder.ReadDatePrecision`: 4 distinct values, missing: none
- `CollectionNode.TrackRole` <- `ComicCollectionNodes.TrackRole`: 2 distinct values, missing: none
- `CollectionNode.SpanSource` <- `ComicCollectionNodes.SpanSource`: 6 distinct values, missing: none
- `Confidence` <- `ComicParsedDetails.Confidence`: 3 distinct values, missing: none
- `UserItemState.Status` <- `Bookmarks.Status`: 3 distinct values, missing: none
- `LinkQuality` <- `LocgMatches.MatchQuality`: 6 distinct values, missing: none

## Errors (0)


## Warnings (4)

- FolderAggregates.UpdatedAt: dropped but has 1 scoped runtime reader file(s): drop:aggregate refresh is a ScanRun fact now
- Comics.UserRating: dropped but has 3 scoped runtime reader file(s): drop:0 rows in v1; per-item user ratings arrive as Rating(Source=User) in v2
- ClaudeSeriesMetadata.Id: dropped but has 9 scoped runtime reader file(s): drop:v2 Insight.Id is new; v1 id kept in SourceKey suffix
- ExternalWorks.SubjectsJson: dropped but has 2 scoped runtime reader file(s): drop:folded into SeriesTag(Source=External) by the whitelist fold; raw subjects live in OpenLibraryWork.SubjectsJson

<!-- /generated:coverage -->

## 22. Built (R4, 2026-08-25)

The model above is real: `src/MovieTheater.Books.Db` (entities + migrations generated from `v2-mapping.json`),
`src/MovieTheater.Books` (migration engine, resolver, verifier), `src/MovieTheater.BooksHost` (the `books-*` verbs) and
`src/MovieTheater.Books.Tests` (64 tests). The frozen v1 file was migrated four times while the contract was corrected by
real data — the corrections are in the catalog above and listed in `v2-migration-verify-2026-08-25.md`. Facts that changed
from the design: `CvIssue.VolumeId` carries no FK (38 % of v1 issues have no fetched volume); MangaUpdates ids are 64-bit;
series insights resolve through v1's `ClaudeSeriesMetadataId` edge first, and minority series receive append-only clones so
no item loses its edge; the browse indexes are `(Kind, <sort>, Id)` — `IsExcluded` is filtered per row, not indexed;
`ItemCredit` holds 1.18 M rows (LOCG's ~18 credits per matched comic), which is most of the gap between the §16 estimate
(≈ 460 MB) and the 707 MB file. See `README.md` for the verbs.
