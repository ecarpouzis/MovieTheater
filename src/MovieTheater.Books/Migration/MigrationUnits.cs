using MovieTheater.Books.Db;
using MovieTheater.Books.Migration.Units;

namespace MovieTheater.Books.Migration
{
    /// <summary>The ordered unit list — the contract's 30 stages, one unit per v1 table with targets (validated by the engine).</summary>
    public static class MigrationUnits
    {
        public static IReadOnlyList<StageUnit> All() => new StageUnit[]
        {
            new RootsUnit(),
            new FoldersUnit(), new FolderParentsUnit(), new FolderAggregatesUnit(),
            new PublishersUnit(),
            new SeriesUnit(),
            new SeriesAliasesUnit(),
            new SeriesMergesUnit(),
            new ItemsUnit(),
            new ComicDetailsUnit(),
            new ReadingOrderUnit(),
            new CollectionNodesUnit(),
            new CollectedEditionsUnit("LocgCollectedEditions", EditionSource.Locg),
            new CollectedEditionsUnit("GcdCollectedEditions", EditionSource.Gcd),
            new CollectedEditionsUnit("ComicvineCollectedEditions", EditionSource.Cv),
            new CollectedEditionsUnit("CuratedCollectedEditions", EditionSource.Curated),
            new CvVolumesUnit(), new CvIssuesUnit(),
            new LocgUnit(),
            new MuUnit(),
            new BarneyUnit(),
            new ExternalWorksUnit(),
            // legs: column-for-column copies after the contract's renames
            new LegsCopyUnit("MarvelSeriesMatches", "MarvelSeriesLink", (r, ctx) => ctx.SeriesExists(r.L("SeriesId")) ? new { SeriesId = r.Int("SeriesId"), MarvelSeriesId = r.I("MarvelSeriesId"), Status = r.S("Status"), Confidence = r.D("Confidence"), MatchedKey = r.S("MatchedKey"), CreatedAt = r.At("CreatedAt") } : null),
            new LegsCopyUnit("ComicvineApiCaches", "ProviderResponseCache", (r, _) => new { Provider = Provider.Cv, RequestKey = r.S("RequestKey") ?? "", ResponseJson = r.S("ResponseJson"), FetchedAt = r.At("FetchedAt") }),
            new LegsCopyUnit("LocgContainments", "LocgContainment", (r, _) => new { Id = r.Int("Id"), ContainerLocgComicId = r.I("ContainerLocgComicId"), ContainedLocgComicId = r.I("ContainedLocgComicId"), ChapterTitle = r.S("ChapterTitle"), Ordinal = r.I("Ordinal"), Source = r.S("Source"), StoryId = r.I("StoryId"), ScrapedAt = r.At("ScrapedAt") }),
            new LegsCopyUnit("LocgSeries", "LocgSeries", (r, _) => new { LocgSeriesId = r.Int("LocgSeriesId"), Name = r.S("Name"), Publisher = r.S("Publisher"), YearBegin = r.I("YearBegin"), YearEnd = r.I("YearEnd"), YearText = r.S("YearText"), IssueCount = r.I("IssueCount"), ImportedAt = r.At("ImportedAt") }),
            new LegsCopyUnit("LocgSeriesInference", "LocgSeriesInference", (r, _) => new { GcdSeriesId = r.Int("GcdSeriesId"), LocgSeriesId = r.S("LocgSeriesId"), SeriesName = r.S("SeriesName"), Support = r.Int("Support"), ImportedAt = r.At("ImportedAt") }),
            new LegsCopyUnit("GcdIssues", "GcdIssue", (r, _) => new
            {
                GcdIssueId = r.Int("GcdIssueId"), GcdSeriesId = r.I("GcdSeriesId"), SeriesName = r.S("SeriesName"), SeriesYearBegan = r.I("SeriesYearBegan"), Number = r.S("Number"), Title = r.S("Title"), KeyDate = r.S("KeyDate"),
                PublicationDate = r.S("PublicationDate"), ValidIsbn = r.S("ValidIsbn"), Isbn = r.S("Isbn"), Barcode = r.S("Barcode"), PageCount = r.I("PageCount"), Price = r.S("Price"), Publisher = r.S("Publisher"), Format = r.S("Format"),
                VariantOfId = r.I("VariantOfId"), VariantName = r.S("VariantName"), ImportedAt = r.At("ImportedAt"), StoryGenres = r.S("StoryGenres"),
            }),
            new LegsCopyUnit("GcdSeries", "GcdSeries", (r, _) => new
            {
                GcdSeriesId = r.Int("GcdSeriesId"), Name = r.S("Name"), SortName = r.S("SortName"), YearBegan = r.I("YearBegan"), YearEnded = r.I("YearEnded"), Publisher = r.S("Publisher"), Format = r.S("Format"), IssueCount = r.I("IssueCount"),
                HasIsbn = r.B("HasIsbn"), HasBarcode = r.B("HasBarcode"), Binding = r.S("Binding"), Notes = r.S("Notes"), ImportedAt = r.At("ImportedAt"),
            }),
            new LegsCopyUnit("MarvelSeries", "MarvelSeries", (r, _) => new { MarvelSeriesId = r.Int("MarvelSeriesId"), Slug = r.S("Slug"), Name = r.S("Name"), YearStart = r.I("YearStart"), YearEnd = r.I("YearEnd"), ScrapedAt = r.At("ScrapedAt") }),
            new LegsCopyUnit("MarvelIssues", "MarvelIssue", (r, _) => new { MarvelIssueId = r.Int("MarvelIssueId"), MarvelSeriesId = r.I("MarvelSeriesId"), Number = r.S("Number"), Slug = r.S("Slug"), ScrapedAt = r.At("ScrapedAt") }),
            new LegsCopyUnit("OpenLibraryEditions", "OpenLibraryEdition", (r, _) => new
            {
                Isbn = r.S("Isbn") ?? "", Title = r.S("Title"), Subtitle = r.S("Subtitle"), AuthorsJson = r.S("AuthorsJson"), Publishers = r.S("Publishers"), PublishDate = r.S("PublishDate"), Pages = r.I("Pages"), SubjectsJson = r.S("SubjectsJson"),
                CoverUrl = r.S("CoverUrl"), OlEditionKey = r.S("OlEditionKey"), OlWorkKey = r.S("OlWorkKey"), SeriesString = r.S("SeriesString"), PhysicalFormat = r.S("PhysicalFormat"), ImportedAt = r.At("ImportedAt"),
            }),
            new LegsCopyUnit("OpenLibraryWorks", "OpenLibraryWork", (r, _) => new { WorkKey = r.S("WorkKey") ?? "", Title = r.S("Title"), SubjectsJson = r.S("SubjectsJson"), SeriesString = r.S("SeriesString"), EditionCount = r.Int("EditionCount"), ImportedAt = r.At("ImportedAt") }),
            new LegsCopyUnit("OlSeriesInference", "OlSeriesInference", (r, _) => new { GcdSeriesId = r.Int("GcdSeriesId"), OlWorkKey = r.S("OlWorkKey"), SeriesString = r.S("SeriesString"), SubjectsJson = r.S("SubjectsJson"), IsbnSupport = r.Int("IsbnSupport"), ImportedAt = r.At("ImportedAt") }),
            new LegsCopyUnit("BarcodeScans", "BarcodeScan", (r, ctx) => ctx.ItemExists(r.L("ComicId")) ? new { ItemId = r.Int("ComicId"), CodesJson = r.S("CodesJson"), PagesScanned = r.Int("PagesScanned"), Error = r.S("Error"), ScannedAt = r.At("ScannedAt") } : null),
            new SeriesKeyLinksUnit("ComicvineSeriesLinks", Provider.Cv, "ComicvineVolumeId"),
            new SeriesKeyLinksUnit("ExternalSeriesLinks", Provider.External, "ExternalWorkId"),
            new MuLinksUnit(),
            new ItemLinksUnit("ComicvineMatches", Provider.Cv),
            new ItemLinksUnit("LocgMatches", Provider.Locg),
            new ItemLinksUnit("GcdMatches", Provider.Gcd),
            new ItemLinksUnit("BarneyMatches", Provider.Barney),
            new ItemLinksUnit("MarvelMatches", Provider.Marvel),
            new ItemLinksUnit("InducksMatches", Provider.Inducks),
            new SeriesInsightsUnit(), new SeriesInsightTagsUnit(), new BookInsightsUnit(), new BookInsightTagsUnit(),
            new RatingsUnit("LibraryComicRatings"), new RatingsUnit("LibrarySeriesRatings"), new RatingsUnit("LibraryRatingOverrides"),
            new KidSafeTagsUnit(), new TagAliasesUnit(), new CvdbResolutionsUnit(),
            new InferenceDecisionsUnit(), new MatchReviewsUnit(),
            new DuplicateGroupsUnit(),
            new DuplicateMembersUnit(),
            new BookmarksUnit(), new UserListsUnit(), new GroupMarksUnit(),
            new SiteSettingsUnit(), new SystemStateUnit(),
            new ResolveUnit(),
            new FtsUnit(),
            new AnalyzeUnit(),
        };
    }
}
