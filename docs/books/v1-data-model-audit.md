# Books v1 (MyBooks) data-model audit — census evidence for the v2 design

Produced by R0 of the Books merge program from the frozen snapshot (see `v1-baseline-counts.md`). Three censuses: **column** (`column_census.py`: null/constant/distinct/bytes per column), **code usage** (`usage_census.py`: which entity properties the runtime, startup, tools and offline code reference — a runtime reference is *scoped* when the file also names the entity or its DbSet; names shared by several entities carry an ambiguity count), and **query** (`capture-sql.ps1` + `query_census.py`: the hot SQL the live binary runs at warm-up and under the browse/reader/shelf endpoints, with `EXPLAIN QUERY PLAN`). Raw artifacts live under `data/books/census/` (gitignored). The **plan verdict** column is the program plan's provisional v2 decision; **conflict** is where the evidence disagrees and R3 must rule.

## 0. Headline findings (R0, 2026-08-25) — what the census overturned or confirmed

1. **`LocgComics.RawJson` is ALWAYS NULL** — the plan assumed it was the 741 MB's bulk. The weight is `LocgComics.CreatorsJson` (78.6 MB, up to 82 KB/row, projected and parsed client-side), `Comics.Description` (45.7 MB), `LocgComics.Description` (32 MB), `ComicvineSeriesLinks.CandidatesJson` (29 MB), `ComicvineApiCaches.ResponseJson` (27 MB). v2 must normalize `CreatorsJson` (a `LocgCreator` table or trimmed roles) — dropping RawJson saves nothing.
2. **`LocgContainments` (391k rows, 30 MB) has NO runtime reader** — only `Mybooks.Tools` (`locg-containment`) and the boot block touch it; the runtime reads the derived `LocgCollectedEditions`. It is offline input, not hot-DB data.
3. **Facets are full `GROUP BY` scans over `Comics`** (21 TEMP B-TREE statements, 15–98 ms each warm, per user) — the 48 h facet cache exists to hide this. A real tag table + resolved columns (plan §D c7/c8) removes the scans.
4. **Only 15 of the 60 named indexes are touched by the hot set**; the browse path leans on `idx_comics_category` plus PK/auto indexes. Most of the other 45 serve writes, admin or offline paths — re-derive v2 indexes from the query census, do not inherit.
5. **MangaUpdates IS a runtime leg** (`ComicSummary` projects `MuDescription/MuGenresJson/MuTagsCsv`) and **Barney feeds reading-order recompute** — confirmed; Marvel/Inducks (14 links each) have no EF entity and no runtime reader.
6. **Dead columns**: 16 always-NULL (`Comics.UserRating/StoryArc/StoryArcNumber/AlternateCount/PageSignature`, `LocgComics.ReleaseDate/KeyType/KeyReason/EstimatedValue/Url/RawJson`, `LocgMatches.Slug`, `GroupUserMetadata.Notes`, …), 39 constant (all five `ComicvineVolumes.*Json` are `'[]'`, every `ModelId`, most `CreatedAt/ScrapedAt`), and two DB columns with no entity property (`GroupUserMetadata.IsFavorite`, `ClaudeSeriesMetadata.ReviewFlag`).
7. **`ClaudeBookMetadata` is mostly empty**: `Rating` 98 % NULL, `Synopsis` 99 % NULL, `TagsCsv` constant — 6,168 rows of which only the audience/maturity fields carry data. The books insight leg is thinner than the comics one by two orders of magnitude.
8. **The per-folder ACL is uniform**: 2 distinct lists over 54,418 folders (lengths 7 and 8, differing by the test account) — confirms the plan's drop.
9. **`ComicSummary` fields the client never reads**: `Web`, `BlackAndWhite`. Everything else in the ~100-field projection has a client consumer.
10. **`SeriesUserLists`, `Tags`, `Sessions`, `Users`, `SeriesAliases`, `SiteSettings` and the nine empty ComicVine tables are still referenced by runtime code** (auth, scrapers, dead tag endpoints) — dropping them means deleting that code in the port, not just the tables.
11. Warm-up on this box: `Cache warm (startup): 10 users, 82 targets warmed, 34.1 s` on a cold copy — the per-user cache-key design (§8.1 `KnownIdentity`) must keep that shape.

## 1. Where the bytes are

Column payload 501.2 MB of a 700.4 MB file (the rest is indexes, FTS and page overhead).

| Table | Rows | Column bytes (MB) | Heaviest columns |
|---|---:|---:|---|
| `LocgComics` | 156,839 | 136.9 | `CreatorsJson` 78.6 MB, `Description` 32.2 MB, `StoryIdsJson` 6.8 MB |
| `Comics` | 141,010 | 109.4 | `Description` 45.7 MB, `FilePath` 19.2 MB, `FileName` 7.4 MB |
| `ComicvineSeriesLinks` | 22,820 | 31.1 | `CandidatesJson` 29.1 MB, `AttemptedAt` 0.6 MB, `SearchQuery` 0.6 MB |
| `LocgContainments` | 391,207 | 30.3 | `ScrapedAt` 11.0 MB, `ChapterTitle` 6.8 MB, `ContainerLocgComicId` 2.7 MB |
| `ComicvineApiCaches` | 20,286 | 28.2 | `ResponseJson` 26.9 MB, `RequestKey` 0.8 MB, `FetchedAt` 0.5 MB |
| `ComicParsedDetails` | 118,926 | 18.7 | `ParsedAt` 3.7 MB, `Series` 2.2 MB, `FolderSeries` 1.7 MB |
| `LibraryComicRatings` | 114,798 | 17.1 | `Note` 11.5 MB, `GeneratedAt` 2.2 MB, `ModelId` 1.6 MB |
| `Folders` | 54,418 | 15.5 | `AuthorizedUsersJson` 5.6 MB, `FolderPath` 3.9 MB, `IndexedAt` 1.5 MB |
| `GcdIssues` | 76,869 | 12.6 | `ImportedAt` 2.1 MB, `SeriesName` 1.2 MB, `Format` 1.2 MB |
| `ComicvineMatches` | 114,753 | 11.6 | `CandidatesJson` 4.2 MB, `LastAttemptedAt` 3.0 MB, `SearchQuery` 2.4 MB |
| `ComicReadingOrder` | 126,035 | 9.9 | `ComputedAt` 3.4 MB, `ReadDate` 1.2 MB, `Source` 1.2 MB |
| `LocgMatches` | 84,874 | 8.8 | `LastScrapedAt` 2.7 MB, `ErrorMessage` 1.5 MB, `MatchedKey` 1.3 MB |
| `ComicvineIssues` | 70,591 | 7.8 | `FetchedAt` 1.9 MB, `Description` 1.3 MB, `Name` 1.2 MB |
| `ComicvineVolumes` | 14,357 | 7.2 | `Description` 4.1 MB, `ImageUrl` 1.1 MB, `SiteDetailUrl` 0.9 MB |
| `GcdMatches` | 118,528 | 7.1 | `CreatedAt` 3.3 MB, `Status` 0.8 MB, `ComicId` 0.6 MB |
| `OpenLibraryEditions` | 19,265 | 6.8 | `SubjectsJson` 2.9 MB, `CoverUrl` 0.6 MB, `ImportedAt` 0.6 MB |
| `ClaudeSeriesMetadata` | 23,418 | 5.8 | `Synopsis` 2.5 MB, `TagsCsv` 1.0 MB, `SeriesName` 0.6 MB |
| `ClaudeSeriesTags` | 245,499 | 4.6 | `Tag` 1.9 MB, `Category` 1.6 MB, `MetadataId` 1.1 MB |
| `MangaUpdatesSeries` | 396 | 4.5 | `RawJson` 4.0 MB, `Description` 0.2 MB, `CategoriesJson` 0.1 MB |
| `OpenLibraryWorks` | 17,754 | 3.8 | `SubjectsJson` 2.5 MB, `ImportedAt` 0.6 MB, `Title` 0.4 MB |

## 2. Column census

- **Always NULL (16)** — nothing has ever been written; drop unless a writer is planned: `Comics.UserRating`, `Comics.AlternateCount`, `Comics.StoryArc`, `Comics.StoryArcNumber`, `Comics.PageSignature`, `ExternalSeriesLinks.ErrorMessage`, `GcdCollectedEditions.GcdIssueId`, `GroupUserMetadata.Notes`, `LocgComics.ReleaseDate`, `LocgComics.KeyType`, `LocgComics.KeyReason`, `LocgComics.EstimatedValue`, `LocgComics.Url`, `LocgComics.RawJson`, `LocgMatches.Slug`, `SeriesInferenceDecisions.EvidenceJson`
- **Constant (39)** — one value across every row (≥100 rows); fold into config/enum or drop: `BarneyMatches.MatchMethod`=prog-number, `BarneyMatches.CreatedAt`=2026-06-12T02:58:30.229902+00:00, `BarneyProgs.Price`=150p, `BarneyProgs.ScrapedAt`=2026-06-12T02:58:30.229902+00:00, `ClaudeBookMetadata.TagsCsv`=audience:children, genre:horror, genre:s, `ComicReadingOrder.ComputedAt`=2026-06-13 05:20:48.6459704, `Comics.MetadataVersion`=1, `Comics.MainCharacterOrTeam`=Superman, `ComicvineCollectedEditions.ScrapedAt`=2026-06-08T03:00:58.5396365Z, `ComicvineSeriesLinks.ErrorMessage`=Comicvine API returned 420 420, `ComicvineVolumes.ConceptsJson`=[], `ComicvineVolumes.CharactersJson`=[], `ComicvineVolumes.LocationsJson`=[], `ComicvineVolumes.ObjectsJson`=[], `ComicvineVolumes.TeamsJson`=[], `CuratedCollectedEditions.Source`=claude, `CuratedCollectedEditions.CreatedAt`=2026-06-08T13:15:57, `ExternalSeriesLinks.MatchedProvider`=openlibrary, `ExternalSeriesLinks.AttemptCount`=1, `ExternalWorks.Provider`=openlibrary, `FolderAggregates.UpdatedAt`=2026-06-21 03:49:44, `GcdCollectedEditions.Contiguous`=1, `GcdCollectedEditions.CreatedAt`=2026-06-09T13:46:04.539000+00:00, `GcdMatches.Applied`=0, `LibraryComicRatings.ModelId`=claude-fable-5, `LibrarySeriesRatings.GeneratedAt`=2026-06-13 05:06:03, `LibrarySeriesRatings.ModelId`=claude-fable-5, `LocgCollectedEditions.CreatedAt`=2026-06-12T02:34:39.5199730Z, `LocgComics.IsKey`=0, `LocgSeriesInference.ImportedAt`=2026-06-11T12:27:35.321261+00:00, `MarvelIssues.ScrapedAt`=2026-06-12T03:13:21.511486+00:00, `MarvelSeries.ScrapedAt`=2026-06-12T03:02:28.311571+00:00, `MarvelSeriesMatches.Status`=matched, `MarvelSeriesMatches.CreatedAt`=2026-06-12T03:02:28.311571+00:00, `OlSeriesInference.ImportedAt`=2026-06-11T22:52:08.872859+00:00, `OpenLibraryEditions.ImportedAt`=2026-06-11T22:52:07.815305+00:00, `OpenLibraryWorks.ImportedAt`=2026-06-11T22:52:07.815305+00:00, `SeriesMergeLogs.CanonicalKey`=, `TagAliases.Source`=Rule
- **≥90 % NULL (49)** — sparse; candidates for a 1:1 side table or removal: `BarcodeScans.Error` (99%), `Bookmarks.LastSpineItemIndex` (97%), `Bookmarks.LastScrollPercent` (97%), `ClaudeBookMetadata.Rating` (98%), `ClaudeBookMetadata.Synopsis` (99%), `ClaudeSeriesMetadata.ReviewFlag` (97%), `ComicCollectionNodes.ParentComicId` (96%), `ComicCollectionNodes.SpanLabel` (97%), `ComicParsedDetails.EventName` (92%), `ComicParsedDetails.ExternalWorkId` (92%), `ComicReadingOrder.Notes` (99%), `Comics.AltSeriesName` (99%), `Comics.AltSeriesIndex` (100%), `Comics.Volume` (91%), `Comics.Pencillers` (93%), `Comics.Count` (96%), `Comics.SeriesGroup` (100%), `Comics.Imprint` (95%), `Comics.Format` (99%), `Comics.AgeRating` (100%), `Comics.GTIN` (100%), `Comics.Inker` (95%), `Comics.Colorist` (96%), `Comics.Letterer` (96%), `Comics.CoverArtist` (95%), `Comics.Editor` (96%), `Comics.Translator` (100%), `Comics.Characters` (96%), `Comics.Teams` (97%), `Comics.Locations` (97%), `Comics.Manga` (100%), `Comics.BrokenReason` (99%), `Comics.ThumbnailError` (100%), `Comics.ExclusionReason` (100%), `Comics.ExcludedAt` (100%), `Comics.ContentFingerprint` (99%), `Comics.SignaturesComputedFor` (99%), `ComicvineIssues.Deck` (98%), `ComicvineIssues.Description` (98%), `ComicvineMatches.ErrorMessage` (99%), `ComicvineMatches.CandidatesJson` (95%), `GcdIssues.VariantOfId` (100%), `GcdMatches.ErrorMessage` (99%), `LocgComics.LocgSeriesId` (100%), `LocgComics.Isbn` (91%), `LocgMatches.LocgSeriesId` (100%), `OlSeriesInference.SeriesString` (93%), `Series.ExternalWorkId` (97%), `Series.DisplayNameOverride` (100%)

## 3. Code-usage census

- Entities parsed: 65; properties: 666.
- **Tables with no scoped runtime reader** (only startup/tools/offline touch them): `BarneyProgs`, `ComicvineSeries`, `ComicvineVolumeSeries`, `LocgApiCaches`, `LocgContainments`
- **Tables with no EF entity at all** (python/offline-only): `BarcodeScans`, `ComicTagAssociation`, `GcdSeries`, `InducksMatches`, `LibraryRatingOverrides`, `LocgSeries`, `LocgSeriesInference`, `MarvelIssues`, `MarvelMatches`, `MarvelSeries`, `MarvelSeriesMatches`, `OlSeriesInference`, `OpenLibraryEditions`, `OpenLibraryWorks`, `SystemState`
- **DB columns with no entity property** (dead columns): `ClaudeSeriesMetadata.ReviewFlag`, `GroupUserMetadata.IsFavorite`
- **`ComicSummary` fields the client never reads**: `Web`, `BlackAndWhite`

Zero-runtime-reader columns per table (column exists in the DB, no scoped runtime file and not in the projection):

- `BarneyMatches` (3/4): `CreatedAt`, `MatchMethod`, `ProgNo`
- `BarneyProgs` (5/5): `CoverDate`, `Price`, `ProgNo`, `ScrapedAt`, `StripsJson`
- `ClaudeBookMetadata` (4/11): `GeneratedAt`, `KnownBook`, `ModelId`, `YearPublished`
- `ClaudeSeriesMetadata` (2/14): `GeneratedAt`, `ModelId`
- `ComicvineCharacters` (5/13): `Aliases`, `FirstAppearedInIssueId`, `Gender`, `Origin`, `RealName`
- `ComicvineIssueCharacters` (1/5): `IsDiedIn`
- `ComicvineIssueTeams` (1/5): `IsDisbanded`
- `ComicvinePeople` (5/11): `Aliases`, `Birth`, `Country`, `Death`, `Hometown`
- `ComicvineSeries` (7/12): `Aliases`, `ComicvineId`, `CountOfEpisodes`, `FetchedAt`, `ImageUrl`, `SiteDetailUrl`, `StartYear`
- `ComicvineStoryArcs` (1/10): `Aliases`
- `ComicvineTeams` (2/10): `Aliases`, `CountOfTeamMembers`
- `ComicvineVolumeSeries` (1/3): `VolumeId`
- `CuratedCollectedEditions` (2/9): `CreatedAt`, `EditionTitle`
- `GcdCollectedEditions` (6/12): `Contiguous`, `CreatedAt`, `EditionTitle`, `GcdIssueId`, `MatchBy`, `SourceSeries`
- `GcdIssues` (12/20): `Barcode`, `GcdIssueId`, `GcdSeriesId`, `ImportedAt`, `Isbn`, `KeyDate`, `Number`, `Price`, `SeriesYearBegan`, `ValidIsbn`, `VariantName`, `VariantOfId`
- `GcdMatches` (6/11): `Applied`, `CandidateCount`, `ErrorMessage`, `GcdSeriesId`, `MatchMethod`, `MatchedKey`
- `LibraryComicRatings` (4/6): `ComicId`, `GeneratedAt`, `ModelId`, `Sources`
- `LibrarySeriesRatings` (3/6): `GeneratedAt`, `ModelId`, `Sources`
- `LocgApiCaches` (3/3): `FetchedAt`, `RequestKey`, `ResponseJson`
- `LocgCollectedEditions` (4/10): `ContainedCount`, `Contiguous`, `CreatedAt`, `EditionTitle`
- `LocgComics` (15/27): `CoverDate`, `CoverUrl`, `DistributorSku`, `EstimatedValue`, `Isbn`, `IssueNumber`, `KeyReason`, `LocgSeriesId`, `RawJson`, `ReleaseDate`, `ScrapedAt`, `StoryCount`, `StoryIdsJson`, `Upc`, `Url`
- `LocgContainments` (6/8): `ChapterTitle`, `ContainedLocgComicId`, `ContainerLocgComicId`, `Ordinal`, `ScrapedAt`, `StoryId`
- `LocgMatches` (7/12): `Applied`, `ErrorMessage`, `LastScrapedAt`, `LocgSeriesId`, `MatchMethod`, `MatchedKey`, `Slug`
- `MangaUpdatesMatches` (4/8): `CandidatesJson`, `CreatedAt`, `MatchMethod`, `MatchedKey`
- `MangaUpdatesSeries` (6/14): `BayesianRating`, `Completed`, `RawJson`, `ScrapedAt`, `Type`, `Url`
- `SeriesAliases` (1/6): `CreatedAt`

## 4. Query census

- Captured 677 statements (310 distinct). Flagged 27 (full scans of tables ≥ 50k rows, automatic indexes, temp b-trees). Named indexes used: 15 of 60.
- **Unused by the hot set**: `IX_Bookmarks_ComicId`, `IX_Bookmarks_Username_ComicId`, `IX_ComicTagAssociation_TagAssociationsId`, `IX_Comics_FilePath`, `IX_Comics_NormalizedTitle`, `IX_Comics_ParentFolderId`, `IX_Comics_SeriesName`, `IX_Folders_FolderPath`, `IX_Folders_ParentId`, `IX_Sessions_Token`, `IX_Tags_NormalizedName`, `IX_Users_Username`, `idx_claudebookmeta_maturity`, `idx_claudebooktags_cat_tag`, `idx_claudebooktags_comic`, `idx_claudetags_cat_tag`, `idx_claudetags_metadata`, `idx_collnode_series`, `idx_comics_folder_group`, `idx_comics_parent_folder`, `idx_curatedcolled_series`, `idx_cvcolled_series`, `idx_dupmember_comic`, `idx_dupmember_group`, `idx_extlinks_status`, `idx_extlinks_work`, `idx_gcdcolled_series`, `idx_gcdissue_barcode`, `idx_gcdissue_isbn`, `idx_gcdissue_series`, `idx_gcdmatch_issue`, `idx_gum_user_fav`, `idx_gum_user_type`, `idx_gum_user_wtr`, `idx_infdecision_state`, `idx_locgcolled_series`, `idx_locgcomic_series`, `idx_locgcontain_contained`, `idx_locgmatch_locgid`, `idx_oled_work`, `idx_parsed_confidence`, `idx_parsed_year`, `idx_readorder_group`, `idx_serieslinks_status`, `idx_serieslinks_volume`, `idx_seriesparsedkeys_series`, `idx_seriesuserl_user_type`, `idx_userl_comic`, `idx_userl_user_type`, `uix_extworks_provider_key`, `uix_locgcontain`, `uix_seriesmatchreview`
- TEMP B-TREE — SELECT ×3 max 62 ms: `SELECT "c1"."TagsCsv" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" LEFT JOIN "ClaudeSeriesMetadata" AS "c1" ON "c0"."ClaudeSeriesMetadataId" = "c1`
- TEMP B-TREE — SELECT ×3 max 63 ms: `SELECT "g0"."TagsCsv" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "GcdMatches" AS "g" ON "c"."Id" = "g"."ComicId" LEFT JOIN "GcdIssues" AS "g0" ON "g"."GcdIssueId" = "g0"."GcdIssueId" WHERE "c"."Category`
- TEMP B-TREE — SELECT ×3 max 44 ms: `SELECT "c"."SeriesId" AS "Key", COUNT(*) AS "Count" FROM "ComicParsedDetails" AS "c" WHERE "c"."ComicId" IN ( SELECT "c0"."Id" FROM "Comics" AS "c0" WHERE "c0"."Category" = 0 AND NOT ("c0"."ExcludedFromLibrary") ) AND "c`
- TEMP B-TREE — SELECT ×3 max 37 ms: `SELECT "c0"."SeriesId" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c0"."ComicId`
- TEMP B-TREE — SELECT ×2 max 47 ms: `SELECT "c1"."Author" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" LEFT JOIN "ClaudeSeriesMetadata" AS "c1" ON "c0"."ClaudeSeriesMetadataId" = "c1"`
- TEMP B-TREE — SELECT ×2 max 47 ms: `SELECT "s"."Franchise" AS "Key", COUNT(*) AS "Count" FROM "ComicParsedDetails" AS "c" LEFT JOIN "Series" AS "s" ON "c"."SeriesId" = "s"."Id" WHERE "c"."ComicId" IN ( SELECT "c0"."Id" FROM "Comics" AS "c0" WHERE "c0"."Cat`
- TEMP B-TREE — SELECT ×2 max 47 ms: `SELECT "c1"."Artist" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" LEFT JOIN "ClaudeSeriesMetadata" AS "c1" ON "c0"."ClaudeSeriesMetadataId" = "c1"`
- TEMP B-TREE — SELECT ×2 max 41 ms: `SELECT "c1"."Key" AS "Decade", COUNT(*) AS "Count" FROM ( SELECT "c"."Year" / 10 AS "Key" FROM "ComicParsedDetails" AS "c" WHERE "c"."ComicId" IN ( SELECT "c0"."Id" FROM "Comics" AS "c0" WHERE "c0"."Category" = 0 AND NOT`
- TEMP B-TREE — SELECT ×3 max 29 ms: `SELECT "c"."FolderGroupId" AS "Id", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."FolderGroupId" IS NOT NULL GROUP BY "c"."FolderGroupId"`
- TEMP B-TREE — SELECT ×3 max 27 ms: `SELECT "c"."FolderGroupId" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."FolderGroupId" IS NOT NULL GROUP BY "c"."FolderGroupId"`
- TEMP B-TREE — SELECT ×2 max 39 ms: `SELECT "m0"."TagsCsv" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" LEFT JOIN "Series" AS "s" ON "c0"."SeriesId" = "s"."Id" LEFT JOIN "MangaUpdates`
- TEMP B-TREE — SELECT ×3 max 23 ms: `SELECT "e"."TagsCsv" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" LEFT JOIN "ExternalWorks" AS "e" ON "c0"."ExternalWorkId" = "e"."Id" WHERE "c"."`
- TEMP B-TREE — SELECT ×2 max 34 ms: `SELECT "s"."Key", COUNT(*) AS "Count" FROM ( SELECT ("c0"."Year" / 10) * 10 AS "Key" FROM "Comics" AS "c" LEFT JOIN "ComicParsedDetails" AS "c0" ON "c"."Id" = "c0"."ComicId" WHERE "c"."Category" = 0 AND NOT ("c"."Exclude`
- SCAN Comics (141,010 rows), TEMP B-TREE — INSERT ×1 max 64 ms: `INSERT OR IGNORE INTO Publishers(Name, FullName) SELECT DISTINCT Publisher, Publisher FROM Comics WHERE Publisher IS NOT NULL AND Publisher != '';`
- SCAN ComicParsedDetails (118,926 rows) — UPDATE ×1 max 59 ms: `UPDATE ComicParsedDetails SET ExternalWorkId = ( SELECT el.ExternalWorkId FROM ExternalSeriesLinks el WHERE el.SeriesName = ComicParsedDetails.Series AND el.ExternalWorkId IS NOT NULL LIMIT 1 ) WHERE Series IS NOT NULL A`
- TEMP B-TREE — SELECT ×3 max 20 ms: `SELECT "c"."PublisherId" AS "Id", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."PublisherId" IS NOT NULL GROUP BY "c"."PublisherId"`
- TEMP B-TREE — SELECT ×3 max 19 ms: `SELECT "c"."Genre" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."Genre" IS NOT NULL AND "c"."Genre" <> '' GROUP BY "c"."Genre"`
- TEMP B-TREE — SELECT ×3 max 19 ms: `SELECT "c"."PublisherId" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."PublisherId" IS NOT NULL GROUP BY "c"."PublisherId"`
- TEMP B-TREE — SELECT ×3 max 15 ms: `SELECT "c"."Tags" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."Tags" IS NOT NULL AND "c"."Tags" <> '' GROUP BY "c"."Tags"`
- TEMP B-TREE — SELECT ×3 max 14 ms: `SELECT "c"."Category", "c"."Tag", COUNT(*) AS "Count" FROM "ClaudeBookTags" AS "c" WHERE "c"."ComicId" IN ( SELECT "c0"."Id" FROM "Comics" AS "c0" WHERE "c0"."Category" = 1 AND "c0"."ParentFolderId" IS NOT NULL ) GROUP B`
- TEMP B-TREE — SELECT ×2 max 18 ms: `SELECT "c"."Writers" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."Writers" IS NOT NULL AND "c"."Writers" <> '' GROUP BY "c"."Writers"`
- SCAN ComicParsedDetails (118,926 rows) — UPDATE ×1 max 35 ms: `UPDATE ComicParsedDetails SET ComicvineVolumeId = ( SELECT sl.ComicvineVolumeId FROM ComicvineSeriesLinks sl WHERE sl.SeriesName = ComicParsedDetails.Series AND sl.ComicvineVolumeId IS NOT NULL LIMIT 1 ) WHERE Series IS `
- TEMP B-TREE — SELECT ×2 max 17 ms: `SELECT "c"."Pencillers" AS "Key", COUNT(*) AS "Count" FROM "Comics" AS "c" WHERE "c"."Category" = 0 AND NOT ("c"."ExcludedFromLibrary") AND "c"."Pencillers" IS NOT NULL AND "c"."Pencillers" <> '' GROUP BY "c"."Pencillers`
- TEMP B-TREE — INSERT ×1 max 30 ms: `INSERT INTO Series (ParsedKey, ResolvedName) SELECT DISTINCT pd.Series, pd.Series FROM ComicParsedDetails pd WHERE pd.Series IS NOT NULL AND pd.Series <> '' AND NOT EXISTS (SELECT 1 FROM Series s WHERE s.ParsedKey = pd.S`
- TEMP B-TREE — SELECT ×3 max 9 ms: `SELECT "c0"."Key" AS "Prefix", COUNT(*) AS "Count" FROM ( SELECT substr("c"."PublicationDate", 0 + 1, 3) AS "Key" FROM "Comics" AS "c" WHERE "c"."Category" = 1 AND "c"."ParentFolderId" IS NOT NULL AND "c"."PublicationDat`
- SCAN ComicParsedDetails (118,926 rows) — UPDATE ×1 max 14 ms: `UPDATE ComicParsedDetails SET ClaudeSeriesMetadataId = ( SELECT m.Id FROM ClaudeSeriesMetadata m WHERE lower(m.SeriesName) = lower(ComicParsedDetails.Series) LIMIT 1 ) WHERE Series IS NOT NULL AND ClaudeSeriesMetadataId `
- SCAN ComicFts (141,010 rows) — SELECT ×1 max 6 ms: `SELECT "s"."Value" FROM ( SELECT COUNT(*) AS Value FROM ComicFts ) AS "s" LIMIT 1`
- Full detail: `data/books/census/queries.md`.

## 5. Per-table evidence vs plan verdict

| Table | Rows | MB | Runtime files (scoped) | Tools | Offline | Plan verdict | Conflict |
|---|---:|---:|---:|---:|---:|---|---|
| `BarcodeScans` | 6,902 | 0.3 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `BarneyMatches` | 2,332 | 0.1 | 1 | 87 | 59 | merge -> ItemProviderLink(Provider) |  |
| `BarneyProgs` | 2,313 | 1.0 | 0 | 18 | 7 | keep -> BarneyProg (reading-order recompute) | no runtime reader — offline/tools only |
| `Bookmarks` | 766 | 0.0 | 6 | 97 | 114 | merge -> UserItemState (one row per user x item) |  |
| `ClaudeBookMetadata` | 6,168 | 0.4 | 4 | 92 | 73 | merge -> BookInsight |  |
| `ClaudeBookTags` | 22,997 | 0.4 | 3 | 90 | 71 | merge -> ItemTag (real M:N) |  |
| `ClaudeSeriesMetadata` | 23,418 | 5.8 | 10 | 85 | 117 | merge -> SeriesInsight keyed by SeriesId (collapse rule) |  |
| `ClaudeSeriesTags` | 245,499 | 4.6 | 4 | 38 | 58 | merge -> SeriesTag (real M:N) |  |
| `ComicCollectionNodes` | 118,322 | 3.6 | 5 | 91 | 61 | keep -> CollectionNode |  |
| `ComicFts` | 141,010 | 0.0 | 0 | 0 | 0 | keep -> ItemFts (rebuilt from Resolved*) |  |
| `ComicParsedDetails` | 118,926 | 18.7 | 22 | 118 | 91 | split -> ComicDetail (provider FKs move to Series) |  |
| `ComicReadingOrder` | 126,035 | 9.9 | 5 | 94 | 64 | keep -> ReadingOrderEntry(SeriesId) |  |
| `ComicTagAssociation` | 0 | 0.0 | 0 | 0 | 0 | drop: dead tag system | no EF entity |
| `ComicUserLists` | 4 | 0.0 | 5 | 89 | 106 | merge -> UserItemState |  |
| `Comics` | 141,010 | 109.4 | 39 | 119 | 131 | split -> Item + ItemEmbeddedMetadata + BookDetail |  |
| `ComicvineApiCaches` | 20,286 | 28.2 | 1 | 2 | 2 | merge -> ProviderResponseCache(Provider) |  |
| `ComicvineCharacters` | 0 | 0.0 | 2 | 44 | 63 | drop: empty CV entity | runtime code still references it (2 files) |
| `ComicvineCollectedEditions` | 915 | 0.1 | 3 | 96 | 66 | merge -> CollectedEditionSpan(Source) |  |
| `ComicvineIssueCharacters` | 0 | 0.0 | 1 | 0 | 5 | drop: empty CV entity | runtime code still references it (1 files) |
| `ComicvineIssuePeople` | 0 | 0.0 | 1 | 2 | 7 | drop: empty CV entity | runtime code still references it (1 files) |
| `ComicvineIssueStoryArcs` | 0 | 0.0 | 1 | 0 | 5 | drop: empty CV entity | runtime code still references it (1 files) |
| `ComicvineIssueTeams` | 0 | 0.0 | 1 | 0 | 5 | drop: empty CV entity | runtime code still references it (1 files) |
| `ComicvineIssues` | 70,591 | 7.8 | 4 | 59 | 62 | keep -> CvIssue |  |
| `ComicvineMatches` | 114,753 | 11.6 | 8 | 93 | 79 | merge -> ItemProviderLink(Provider) |  |
| `ComicvinePeople` | 0 | 0.0 | 1 | 44 | 63 | drop: empty CV entity | runtime code still references it (1 files) |
| `ComicvineSeries` | 0 | 0.0 | 0 | 45 | 63 | drop: empty CV entity |  |
| `ComicvineSeriesLinks` | 22,820 | 31.1 | 8 | 51 | 48 | merge -> SeriesProviderLink(Provider) keyed by SeriesId |  |
| `ComicvineStoryArcs` | 0 | 0.0 | 2 | 44 | 62 | drop: empty CV entity | runtime code still references it (2 files) |
| `ComicvineTeams` | 0 | 0.0 | 2 | 44 | 62 | drop: empty CV entity | runtime code still references it (2 files) |
| `ComicvineVolumeSeries` | 0 | 0.0 | 0 | 49 | 33 | drop: empty CV entity |  |
| `ComicvineVolumes` | 14,357 | 7.2 | 10 | 46 | 63 | keep -> CvVolume |  |
| `CuratedCollectedEditions` | 1,047 | 0.2 | 2 | 98 | 70 | merge -> CollectedEditionSpan(Source) |  |
| `CvdbResolutions` | 1,037 | 0.1 | 2 | 47 | 41 | keep -> CvdbResolution |  |
| `DuplicateGroups` | 3,703 | 0.4 | 3 | 76 | 104 | keep |  |
| `DuplicateMembers` | 7,649 | 0.2 | 1 | 89 | 106 | keep |  |
| `ExternalSeriesLinks` | 4,340 | 1.2 | 2 | 47 | 30 | merge -> SeriesProviderLink(Provider) keyed by SeriesId |  |
| `ExternalWorks` | 691 | 0.2 | 7 | 113 | 116 | keep -> ExternalWork |  |
| `FolderAggregates` | 54,418 | 1.4 | 2 | 1 | 1 | merge -> Folder |  |
| `Folders` | 54,418 | 15.5 | 12 | 67 | 106 | merge -> Folder (+FolderAggregates) |  |
| `GcdCollectedEditions` | 4,502 | 0.5 | 2 | 100 | 66 | merge -> CollectedEditionSpan(Source) |  |
| `GcdIssues` | 76,869 | 12.6 | 3 | 111 | 72 | keep -> GcdIssue |  |
| `GcdMatches` | 118,528 | 7.1 | 3 | 101 | 77 | merge -> ItemProviderLink(Provider) |  |
| `GcdSeries` | 17,152 | 2.4 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `GroupUserMetadata` | 68 | 0.0 | 7 | 64 | 111 | merge -> GroupMark(GroupType) |  |
| `InducksMatches` | 14 | 0.0 | 0 | 0 | 0 | merge -> ItemProviderLink(Provider) | no EF entity |
| `KidSafeTags` | 2 | 0.0 | 2 | 38 | 58 | keep -> KidSafeTag |  |
| `LibraryComicRatings` | 114,798 | 17.1 | 2 | 86 | 71 | merge -> LibraryRating(TargetKind, IsOverride) |  |
| `LibraryPaths` | 2 | 0.0 | 6 | 69 | 122 | keep -> LibraryRoot |  |
| `LibraryRatingOverrides` | 75 | 0.0 | 0 | 0 | 0 | merge -> LibraryRating(TargetKind, IsOverride) | no EF entity |
| `LibrarySeriesRatings` | 17,337 | 2.6 | 3 | 36 | 41 | merge -> LibraryRating(TargetKind, IsOverride) |  |
| `LocgApiCaches` | 0 | 0.0 | 0 | 2 | 2 | drop: never written |  |
| `LocgCollectedEditions` | 3,447 | 0.3 | 2 | 99 | 64 | merge -> CollectedEditionSpan(Source) |  |
| `LocgComics` | 156,839 | 136.9 | 1 | 106 | 59 | trim -> LocgComic (drop blobs nothing reads) |  |
| `LocgContainments` | 391,207 | 30.3 | 0 | 72 | 101 | keep -> LocgContainment | no runtime reader — offline/tools only |
| `LocgMatches` | 84,874 | 8.8 | 2 | 102 | 77 | merge -> ItemProviderLink(Provider) |  |
| `LocgSeries` | 8,376 | 0.7 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `LocgSeriesInference` | 11,830 | 0.8 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `MangaUpdatesMatches` | 835 | 0.2 | 3 | 65 | 61 | merge -> SeriesProviderLink(Provider) keyed by SeriesId |  |
| `MangaUpdatesSeries` | 396 | 4.5 | 2 | 86 | 78 | keep -> MuSeries (runtime leg) |  |
| `MarvelIssues` | 1,079 | 0.1 | 0 | 0 | 0 | move -> links + disk cache | no EF entity |
| `MarvelMatches` | 14 | 0.0 | 0 | 0 | 0 | merge -> ItemProviderLink(Provider) | no EF entity |
| `MarvelSeries` | 5,583 | 0.6 | 0 | 0 | 0 | move -> links + disk cache | no EF entity |
| `MarvelSeriesMatches` | 128 | 0.0 | 0 | 0 | 0 | merge -> SeriesProviderLink(Provider) keyed by SeriesId | no EF entity |
| `OlSeriesInference` | 7,250 | 1.2 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `OpenLibraryEditions` | 19,265 | 6.8 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `OpenLibraryWorks` | 17,754 | 3.8 | 0 | 0 | 0 | keep (new entity) | no EF entity |
| `Publishers` | 2,635 | 0.1 | 18 | 86 | 115 | keep |  |
| `Series` | 19,481 | 1.6 | 22 | 70 | 105 | keep |  |
| `SeriesAliases` | 19 | 0.0 | 2 | 34 | 26 | drop: manual map unused by resolution | runtime code still references it (2 files) |
| `SeriesInferenceDecisions` | 2,915 | 0.5 | 2 | 75 | 109 | keep |  |
| `SeriesMatchReviews` | 7 | 0.0 | 2 | 63 | 111 | keep |  |
| `SeriesMergeLogs` | 44,261 | 1.6 | 1 | 3 | 4 | keep -> SeriesMerge (migrated in full as the old-id redirect) |  |
| `SeriesParsedKeys` | 21,566 | 0.6 | 1 | 32 | 27 | keep -> SeriesAlias |  |
| `SeriesUserLists` | 0 | 0.0 | 4 | 73 | 104 | drop: 0 rows, superseded by GroupUserMetadata | runtime code still references it (4 files) |
| `Sessions` | 41 | 0.0 | 4 | 65 | 102 | drop: identity moves to the site | runtime code still references it (4 files) |
| `SiteSettings` | 1 | 0.0 | 1 | 1 | 42 | drop -> SystemState | runtime code still references it (1 files) |
| `SystemState` | 7 | 0.0 | 0 | 0 | 0 | keep | no EF entity |
| `TagAliases` | 174 | 0.0 | 2 | 49 | 58 | keep -> TagAlias |  |
| `Tags` | 0 | 0.0 | 11 | 86 | 115 | drop: dead tag system | runtime code still references it (11 files) |
| `Users` | 10 | 0.0 | 7 | 65 | 102 | drop: identity moves to the site | runtime code still references it (7 files) |

### Conflicts to rule on in R3 (31)

- `BarcodeScans`: no EF entity
- `BarneyProgs`: no runtime reader — offline/tools only
- `ComicTagAssociation`: no EF entity
- `ComicvineCharacters`: runtime code still references it (2 files)
- `ComicvineIssueCharacters`: runtime code still references it (1 files)
- `ComicvineIssuePeople`: runtime code still references it (1 files)
- `ComicvineIssueStoryArcs`: runtime code still references it (1 files)
- `ComicvineIssueTeams`: runtime code still references it (1 files)
- `ComicvinePeople`: runtime code still references it (1 files)
- `ComicvineStoryArcs`: runtime code still references it (2 files)
- `ComicvineTeams`: runtime code still references it (2 files)
- `GcdSeries`: no EF entity
- `InducksMatches`: no EF entity
- `LibraryRatingOverrides`: no EF entity
- `LocgContainments`: no runtime reader — offline/tools only
- `LocgSeries`: no EF entity
- `LocgSeriesInference`: no EF entity
- `MarvelIssues`: no EF entity
- `MarvelMatches`: no EF entity
- `MarvelSeries`: no EF entity
- `MarvelSeriesMatches`: no EF entity
- `OlSeriesInference`: no EF entity
- `OpenLibraryEditions`: no EF entity
- `OpenLibraryWorks`: no EF entity
- `SeriesAliases`: runtime code still references it (2 files)
- `SeriesUserLists`: runtime code still references it (4 files)
- `Sessions`: runtime code still references it (4 files)
- `SiteSettings`: runtime code still references it (1 files)
- `SystemState`: no EF entity
- `Tags`: runtime code still references it (11 files)
- `Users`: runtime code still references it (7 files)

## 6. `ComicSummary` fields by client usage

| Field | Client files |
|---|---:|
| `Id` | 46 |
| `ParentFolderId` | 5 |
| `Title` | 43 |
| `FileName` | 5 |
| `FileExtension` | 5 |
| `FileSize` | 2 |
| `IndexedAt` | 4 |
| `PageCount` | 12 |
| `EmbeddedRating` | 3 |
| `UserRating` | 3 |
| `SeriesName` | 11 |
| `SeriesIndex` | 4 |
| `Volume` | 7 |
| `IssueTitle` | 2 |
| `Writers` | 6 |
| `Pencillers` | 3 |
| `Inker` | 1 |
| `Colorist` | 1 |
| `CoverArtist` | 2 |
| `Publisher` | 27 |
| `Description` | 18 |
| `Genre` | 9 |
| `Tags` | 12 |
| `Characters` | 5 |
| `Teams` | 2 |
| `Language` | 1 |
| `Format` | 5 |
| `PublicationDate` | 5 |
| `StoryArc` | 2 |
| `Web` | 0 |
| `BlackAndWhite` | 0 |
| `Manga` | 2 |
| `ParentFolderName` | 10 |
| `CoverWidth` | 3 |
| `CoverHeight` | 4 |
| `CvDeck` | 2 |
| `CvDescription` | 2 |
| `CvSeriesName` | 3 |
| `CvPublisherName` | 2 |
| `ExtSynopsis` | 2 |
| `ExtAuthor` | 2 |
| `ExtPublisher` | 1 |
| `ExtYearBegin` | 2 |
| `ExtCoverUrl` | 1 |
| `ExtTagsCsv` | 3 |
| `ClaudeSynopsis` | 2 |
| `ClaudeRating` | 4 |
| `ClaudeAuthor` | 3 |
| `ClaudeArtist` | 3 |
| `ClaudeTagsCsv` | 3 |
| `LocgMatchQuality` | 1 |
| `LocgComicId` | 1 |
| `LocgRating` | 2 |
| `LocgRatingCount` | 1 |
| `LocgDescription` | 2 |
| `LocgCoverPrice` | 1 |
| `LocgCreatorsJson` | 2 |
| `LocgIsKey` | 1 |
| `LocgKeyType` | 1 |
| `LibraryRating` | 4 |
| `LibraryRatingNote` | 3 |
| `MuDescription` | 2 |
| `MuGenresJson` | 2 |
| `MuTagsCsv` | 3 |
| `GcdTagsCsv` | 3 |
| `SeriesId` | 10 |
| `ResolvedSeriesName` | 6 |
| `Franchise` | 2 |
| `IsSingleIssueSeries` | 7 |
| `SeriesYearStart` | 4 |
| `SeriesYearEnd` | 4 |
| `SeriesIsOngoing` | 4 |
| `SeriesIssueCount` | 3 |
| `ParsedSeries` | 8 |
| `ParsedIssueNo` | 5 |
| `ParsedYear` | 8 |
| `ParsedVolumeNo` | 3 |
| `ParsedPublisher` | 3 |
| `ParsedFormat` | 5 |
| `ParsedConfidence` | 1 |
| `ParsedEventName` | 4 |
| `IsCollection` | 7 |
| `PublisherId` | 6 |
| `FolderGroupId` | 7 |
| `FolderGroupName` | 3 |
| `FolderPath` | 4 |
| `ReadIndex` | 6 |
| `ReadCount` | 2 |
| `ReadNumber` | 2 |
| `ReadTier` | 4 |
| `ReadDate` | 6 |
| `ReadDatePrecision` | 2 |
| `ReadOrderSource` | 1 |
| `CollectionLevel` | 3 |
| `TrackRole` | 2 |
| `SpanStart` | 2 |
| `SpanEnd` | 2 |
| `ContainsCount` | 2 |
| `ContainerParentId` | 2 |
| `SpanLabel` | 3 |
| `IsShadowedDuplicate` | 2 |
