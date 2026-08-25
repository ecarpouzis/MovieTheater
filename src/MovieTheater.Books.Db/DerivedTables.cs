namespace MovieTheater.Books.Db
{
    /// <summary>
    /// The registry of DERIVED data — rebuilt by a named job from inputs, never hand-edited (the standalone site's
    /// "edit inputs, re-resolve" golden rule, made structural). Each entry names the job and the SQL whose
    /// result is the input fingerprint the job compares before rebuilding. Seeded into <see cref="DerivedTable"/>
    /// by the migration verb; the admin panel lists it.
    /// </summary>
    public static class DerivedTables
    {
        public sealed record Entry(string Name, string RebuildJob, string FingerprintSql);

        private const string ComicDetailStamp = "SELECT count(*) || ':' || coalesce(max(ItemId),0) || ':' || coalesce(sum(length(ParsedSeriesKey)),0) FROM ComicDetail";
        private const string InsightStamp = "SELECT count(*) || ':' || coalesce(max(Id),0) || ':' || coalesce(max(GeneratedAt),'') FROM Insight";

        public static readonly IReadOnlyList<Entry> All = new[]
        {
            new Entry("Series", "books-resolve --series",
                ComicDetailStamp + " UNION ALL SELECT count(*) || ':' || coalesce(sum(ProviderKey),0) || ':' || coalesce(sum(Status),0) FROM SeriesKeyLink"),
            new Entry("SeriesAlias", "books-resolve --series", ComicDetailStamp),
            new Entry("Item.SeriesId", "books-resolve --series", "SELECT count(*) || ':' || coalesce(sum(SeriesId),0) FROM SeriesAlias"),
            new Entry("Item.Resolved*", "books-resolve --items",
                "SELECT count(*) || ':' || coalesce(max(ResolvedAt),'') FROM Series UNION ALL " + InsightStamp
                + " UNION ALL SELECT count(*) || ':' || coalesce(max(AttemptedAt),'') FROM ItemProviderLink UNION ALL SELECT count(*) FROM Rating"),
            new Entry("Series.Resolved*", "books-resolve --items", InsightStamp + " UNION ALL SELECT count(*) FROM Rating"),
            new Entry("ItemTag/SeriesTag(folds)", "books-resolve --tags",
                InsightStamp + " UNION ALL SELECT count(*) FROM CvdbResolution UNION ALL SELECT count(*) FROM MuSeriesLink UNION ALL SELECT count(*) FROM ItemProviderLink WHERE Provider = 3"),
            new Entry("Insight.IsCurrent", "books-resolve --insights", InsightStamp),
            new Entry("ItemFts", "books-resolve --fts", "SELECT count(*) || ':' || coalesce(max(ResolvedAt),'') FROM Item"),
            new Entry("ReadingOrderEntry", "books-reading-order", ComicDetailStamp + " UNION ALL SELECT count(*) FROM CvIssue UNION ALL SELECT count(*) FROM CollectedEditionSpan"),
            new Entry("CollectionNode", "books-containment", ComicDetailStamp + " UNION ALL SELECT count(*) FROM CollectedEditionSpan"),
            new Entry("Folder.TopFolderId/Counts", "books-scan", "SELECT count(*) || ':' || coalesce(max(IndexedAt),'') FROM Folder"),
            new Entry("Rating(Source=Library)", "books-library-ratings", "SELECT count(*) FROM Rating WHERE Source <> 4"),
        };
    }
}
