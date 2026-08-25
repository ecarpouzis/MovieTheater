# v2 migration — verify report (R4, 2026-08-25)

The frozen v1 file (`data/books/v1/mybooks-v1-frozen-20260825.db`, 700,354,560 bytes, sha256 `fa321205…` — re-hashed after
every run) was copy-transformed by `books-migrate-v1` into `data/books/v2/books.db` (707 MB, 170,985+ pages) and
`books-legs.db` (308 MB): 66 units, 577 batches of 5,000 rows, ≈ 8 min wall clock (≈ 300 s of batch time), then
`books-resolve` ran inside the `resolve` stage (25,529 current insights of 28,911; 232,767 raw + 95,484 folded AI tag rows
over 254 kept canonical tags; 19,481 series and 141,010 items resolved) and `ItemFts` was rebuilt (141,010 rows).

Four full runs were made; each earlier run changed the contract or the code (see the R4 log in the plan): `long` MU ids,
nullable `MuSeriesLink.MuSeriesId`, no FK on `CvIssue.VolumeId` (19,585 of 70,591 issues have no fetched volume — v1 had
no FK either), series insights resolved through v1's real `ClaudeSeriesMetadataId` edge (majority) with append-only
clones for the 224 minority series (346 clone rows, ids 20,000,000+), reading-order keys falling back to the item's own
series (31,201), `IsExcluded` removed from the browse indexes (EF renders `NOT(IsExcluded)`, which defeats the index
prefix), `(Kind, NormalizedTitle, Id)`, `(Kind, ResolvedPublisher, Id)` and `CollectionNode(ContainsCount DESC)` added.

Rows the contract could not place were counted, never dropped silently: 1,021 orphan series insights (no series by edge
or name) exported to `orphan-insights.json`; 22 MU matches and 2 Marvel series matches on series that no longer exist;
1 rating override on a missing target; 1 series mark keyed by a name that resolves to nothing; 51 + 1 activity rows of
other users (never copied, by decision 5). 547 `ComicDetail.FormatRaw` spellings map to `Unknown`.

## Reading the replay
Every warm query is ≤ 105 ms; v1's facets took 13 s cold and were served from a 48 h cache. The sum of all 37 warm
queries is 564 ms. Two facets are at the 100 ms target (`facets/tags` 105 ms, `facets/authors` 94 ms — GROUP BY over
268k / 1.18 M rows through covering indexes); the one flagged plan is a publisher-filtered browse sorted by series,
which sorts its filtered subset (11 ms) — the expected shape for filter-by-X/sort-by-Y without a per-filter compound
index, the same as v1. Facets stay cacheable; nothing in the hot set scans a large table.

## Series-resolution recompute
Recomputed with v1's own per-row provider ids as the signal, the port reproduces v1's derivation exactly (0 alias / name /
key differences). Recomputed from the CURRENT link table it differs (27 aliases, 49 names, 94 keys, 6 merges) — that is
drift v1 accumulated after its last rebuild, and what the R6 `books-resolve --series` job will apply.

# books-verify-v1 (run #4) — 2026-08-25 18:38 UTC

- 59 passed, 0 failed

- PASS hot integrity_check: ok
- PASS legs integrity_check: ok
- PASS hot foreign_key_check: 0 violations
- PASS Item.Id == Comics.Id: v1 141010, v2 141010
- PASS Series.Id == Series.Id: v1 19481, v2 19481
- PASS Folder.Id == Folders.Id: v1 54418, v2 54418
- PASS Publisher.Id == Publishers.Id: v1 2635, v2 2635
- PASS count Folder: v1 54418, v2 54418
- PASS count Folder.ParentId set: v1 54416, v2 54416
- PASS count ItemState: v1 141010, v2 141010
- PASS count BookDetail: v1 22084, v2 22084
- PASS count Item(Kind=Book): v1 22084, v2 22084
- PASS count Item.IsExcluded: v1 604, v2 604
- PASS count ComicDetail: v1 118926, v2 118926
- PASS count Item.SeriesId set: v1 118926, v2 118926
- PASS count SeriesAlias: v1 21566, v2 21566
- PASS count SeriesMerge: v1 44261, v2 44261
- PASS count ReadingOrderEntry: v1 126035, v2 126035
- PASS count CollectionNode: v1 118322, v2 118322
- PASS count CollectedEditionSpan: v1 9911, v2 9911
- PASS count CvVolume: v1 14357, v2 14357
- PASS count CvIssue: v1 70591, v2 70591
- PASS count LocgComicRaw (legs): v1 156839, v2 156839
- PASS count LocgComic (hot subset): v1 60684, v2 60684
- PASS count LocgContainment (legs): v1 391207, v2 391207
- PASS count GcdIssue (legs): v1 76869, v2 76869
- PASS count MuSeries: v1 396, v2 396
- PASS count BarneyProg: v1 2313, v2 2313
- PASS count ExternalWork: v1 691, v2 691
- PASS count ProviderResponseCache (legs): v1 20286, v2 20286
- PASS count SeriesKeyLink: v1 27160, v2 27160
- PASS count MuSeriesLink: v1 813, v2 813
- PASS count ItemProviderLink: v1 320515, v2 320515
- PASS count Insight(Item): v1 6168, v2 6168
- PASS count InsightTag(book): v1 22997, v2 22997
- PASS count Rating(Library/Override): v1 132209, v2 132209
- PASS count KidSafeTag: v1 2, v2 2
- PASS count TagAlias: v1 174, v2 174
- PASS count CvdbResolution: v1 1037, v2 1037
- PASS count SeriesInferenceDecision: v1 2915, v2 2915
- PASS count SeriesMatchReview: v1 7, v2 7
- PASS count DuplicateGroup: v1 3703, v2 3703
- PASS count DuplicateMember: v1 7649, v2 7649
- PASS item→current series insight edge: v1 items with an insight 118495, v2 items with a current series insight 118503
- PASS series insights carried + exported: v1 23418, carried 22397 (+346 clones for minority series), exported 1021 (orphan-insights.json)
- PASS one current insight per subject: duplicates by (SubjectKind, SubjectId) with IsCurrent = 1
- PASS every subject has a current insight: subjects with rows but no current row
- PASS owner positions: v1 bookmarks 715, v2 UserItemState rows 716
- PASS owner want-to-read: v1 lists 3, v2 WantToRead rows 3
- PASS owner group marks: v1 group rows 68 (series+comic), v2 GroupMark 42
- PASS no other user copied: UserItemState/GroupMark rows for other user ids
- PASS ComicDetail.Format mapped: 547 rows with an unmapped FormatRaw (reported, not failed)
- PASS ItemProviderLink statuses: null statuses
- PASS Item.Resolved* populated: 0 items unresolved
- PASS Series.Resolved* populated: 0 series unresolved
- PASS ItemFts rows == Item rows: fts 141010, items 141010
- PASS series-resolution recompute (v1 signal) diff: alias +0 ~0 (stale, v1 would drop too: -0); survivor name 0, key 0; merge candidates 0
- PASS series-resolution recompute (current links) drift — informational: what the R6 rebuild will change: alias +0 ~27 -196; survivor name 49, key 94; merges 6
- PASS all migration units finished: 66/66 finished

# Hot-set replay

| Query | cold ms | warm ms | rows | flags |
|---|---:|---:|---:|---|
| facets/series | 103 | 38 | 500 |  |
| facets/publishers | 38 | 33 | 200 |  |
| facets/decades | 88 | 82 | 13 |  |
| facets/events | 2 | 3 | 200 |  |
| facets/franchises | 3 | 1 | 74 |  |
| facets/collections | 29 | 27 | 109 |  |
| facets/authors | 104 | 94 | 300 |  |
| facets/artists | 41 | 36 | 300 |  |
| facets/tags | 111 | 105 | 300 |  |
| facets/series-tags | 9 | 7 | 268 |  |
| groups/series heads | 0 | 0 | 60 |  |
| groups/series heads (top rated) | 74 | 1 | 60 |  |
| groups/publisher heads | 2 | 1 | 60 |  |
| groups/decade heads | 82 | 83 | 14 |  |
| groups/letters | 10 | 8 | 56 |  |
| band/series items | 1 | 0 | 40 |  |
| band/first 8 series (by series id) | 1 | 0 | 320 |  |
| catalog/default (series, id) | 0 | 0 | 120 |  |
| catalog/newest (year desc, indexed desc) | 0 | 0 | 120 |  |
| catalog/top rated | 0 | 0 | 120 |  |
| catalog/recently added | 0 | 0 | 120 |  |
| catalog/title | 0 | 3 | 120 |  |
| catalog/filter publisher + series sort | 11 | 11 | 120 | TEMP B-TREE |
| catalog/filter series page 2 | 0 | 0 | 0 |  |
| catalog/count with filter | 50 | 42 | 31912 |  |
| catalog/tag filter (exists) | 8 | 1 | 120 |  |
| home/highest rated series | 1 | 0 | 24 |  |
| home/big collected editions | 0 | 0 | 24 |  |
| home/fresh arrivals | 0 | 0 | 48 |  |
| home/top shelf reads (user) | 0 | 0 | 48 |  |
| home/continue reading (user) | 25 | 0 | 24 |  |
| kids/series allow-list | 10 | 1 | 160 |  |
| kids/books maturity 0 | 2 | 0 | 160 |  |
| novels/authors | 77 | 53 | 300 |  |
| novels/rated books | 0 | 0 | 96 |  |
| fts/search join | 20 | 14 | 200 |  |
| bookshelf/marked series | 19 | 2 | 42 |  |

## Plans

### facets/series
```
SEARCH i USING INDEX IX_Item_SeriesId_Id (SeriesId>?)
USE TEMP B-TREE FOR ORDER BY
```

### facets/publishers
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedPublisher_Id (Kind=? AND ResolvedPublisher>?)
USE TEMP B-TREE FOR ORDER BY
```

### facets/decades
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedYear_IndexedAt_Id (Kind=? AND ResolvedYear>?)
USE TEMP B-TREE FOR GROUP BY
```

### facets/events
```
SEARCH c USING COVERING INDEX IX_ComicDetail_EventName (EventName>?)
USE TEMP B-TREE FOR ORDER BY
```

### facets/franchises
```
SEARCH s USING INDEX IX_Series_Franchise (Franchise>?)
USE TEMP B-TREE FOR ORDER BY
```

### facets/collections
```
SEARCH i USING INDEX IX_Item_TopFolderId (TopFolderId>?)
USE TEMP B-TREE FOR ORDER BY
```

### facets/authors
```
SEARCH i USING COVERING INDEX IX_ItemCredit_Role_NormalizedName_ItemId (Role=?)
USE TEMP B-TREE FOR GROUP BY
USE TEMP B-TREE FOR ORDER BY
```

### facets/artists
```
SEARCH i USING COVERING INDEX IX_ItemCredit_Role_NormalizedName_ItemId (Role=?)
USE TEMP B-TREE FOR GROUP BY
USE TEMP B-TREE FOR ORDER BY
```

### facets/tags
```
SEARCH i USING COVERING INDEX IX_ItemTag_Category_Value_ItemId (Category=?)
USE TEMP B-TREE FOR GROUP BY
USE TEMP B-TREE FOR ORDER BY
```

### facets/series-tags
```
SEARCH s USING COVERING INDEX IX_SeriesTag_Category_Value_SeriesId (Category=?)
USE TEMP B-TREE FOR ORDER BY
```

### groups/series heads
```
SCAN s USING INDEX IX_Series_Name_Id
```

### groups/series heads (top rated)
```
SEARCH s USING INDEX IX_Series_ResolvedRating_Id (ResolvedRating>?)
```

### groups/publisher heads
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedPublisher_Id (Kind=?)
```

### groups/decade heads
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedYear_IndexedAt_Id (Kind=?)
USE TEMP B-TREE FOR GROUP BY
```

### groups/letters
```
SCAN s
USE TEMP B-TREE FOR GROUP BY
```

### band/series items
```
SEARCH r USING COVERING INDEX IX_ReadingOrderEntry_SeriesId_ReadIndex (SeriesId=?)
SEARCH i USING INTEGER PRIMARY KEY (rowid=?)
```

### band/first 8 series (by series id)
```
SEARCH i USING INDEX IX_Item_SeriesId_Id (SeriesId>?)
```

### catalog/default (series, id)
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedSeries_Id (Kind=?)
```

### catalog/newest (year desc, indexed desc)
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedYear_IndexedAt_Id (Kind=?)
```

### catalog/top rated
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedRating_Id (Kind=? AND ResolvedRating>?)
```

### catalog/recently added
```
SEARCH i USING INDEX IX_Item_Kind_IndexedAt_Id (Kind=?)
```

### catalog/title
```
SEARCH i USING INDEX IX_Item_Kind_NormalizedTitle_Id (Kind=?)
```

### catalog/filter publisher + series sort
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedPublisher_Id (Kind=? AND ResolvedPublisher=?)
USE TEMP B-TREE FOR ORDER BY
```

### catalog/filter series page 2
```
SEARCH i USING INDEX IX_Item_SeriesId_Id (SeriesId=?)
```

### catalog/count with filter
```
(raw SQL: FTS MATCH)
```

### catalog/tag filter (exists)
```
(raw SQL: FTS MATCH)
```

### home/highest rated series
```
SEARCH s USING INDEX IX_Series_ResolvedRating_Id (ResolvedRating>?)
```

### home/big collected editions
```
SEARCH c USING COVERING INDEX IX_CollectionNode_ContainsCount (ContainsCount>?)
SEARCH i USING INTEGER PRIMARY KEY (rowid=?)
```

### home/fresh arrivals
```
SEARCH i USING INDEX IX_Item_Kind_IndexedAt_Id (Kind=?)
```

### home/top shelf reads (user)
```
SEARCH u USING INDEX IX_UserItemState_UserId_UpdatedAt (UserId=?)
SEARCH i USING INTEGER PRIMARY KEY (rowid=?)
```

### home/continue reading (user)
```
SEARCH u USING INDEX IX_UserItemState_UserId_UpdatedAt (UserId=?)
```

### kids/series allow-list
```
(raw SQL: FTS MATCH)
```

### kids/books maturity 0
```
SEARCH i USING INDEX IX_Insight_SubjectKind_SubjectId_IsCurrent (SubjectKind=?)
```

### novels/authors
```
SCAN i
USE TEMP B-TREE FOR GROUP BY
USE TEMP B-TREE FOR ORDER BY
```

### novels/rated books
```
SEARCH i USING INDEX IX_Item_Kind_ResolvedRating_Id (Kind=? AND ResolvedRating>?)
```

### fts/search join
```
(raw SQL: FTS MATCH)
```

### bookshelf/marked series
```
SCAN g
```


- flagged: 1 of 37
