using MovieTheater.Books.Parse;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The three derived jobs that rebuild a per-series model — reading order, containment, and the LOCG span
    /// reduction — against the migrated synthetic file.
    ///
    /// <para>These assert the RECOMPUTE's own semantics rather than equality with the fixture's stored rows: the
    /// fixture hand-authored `ComicReadingOrder` values that no real recompute would produce (a group key of ''
    /// carrying a ReadIndex, a row marked Unordered that still has one). Asserting equality with those would pin
    /// the fixture's fiction rather than the port's behaviour — the same reason the series rebuild's counts are
    /// checked against the data instead of against v1's stored IssueCount.</para>
    /// </summary>
    public class DerivedJobTests
    {
        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        private static TargetWriter Writer(V1Fixture f) => new(f.HotPath, MappingContract.Load(), dryRun: false);

        // ── reading order ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ReadingOrderNumbersEachRunDenselyFromOne()
        {
            using var f = Migrated();
            using (var hot = Writer(f)) ReadingOrderJob.RunAll(hot, 50, _ => { });

            using var w = f.Hot();
            // Series 1 holds three files, one of them an excluded shadow duplicate: the run is TWO issues.
            var indexes = w.Pairs("SELECT ItemId, CAST(ReadIndex AS TEXT) FROM ReadingOrderEntry WHERE SeriesId = 1 ORDER BY ReadIndex")
                .Select(p => p.Item2).ToList();
            Assert.Equal(new[] { "1", "2" }, indexes);
            Assert.Equal(2, w.Scalar<long>("SELECT ReadCount FROM ReadingOrderEntry WHERE ItemId = 1"));

            // The ComicVine-matched issue takes its number and cover date from the issue scrape.
            Assert.Equal((long)ReadingOrderSource.ComicVine, w.Scalar<long>("SELECT Source FROM ReadingOrderEntry WHERE ItemId = 1"));
            Assert.Equal("1977-02-26", w.Scalar<string>("SELECT ReadDate FROM ReadingOrderEntry WHERE ItemId = 1"));
            Assert.Equal((long)Confidence.High, w.Scalar<long>("SELECT Confidence FROM ReadingOrderEntry WHERE ItemId = 1"));
        }

        private const string OrderSql =
            "SELECT ItemId, coalesce(CAST(ReadIndex AS TEXT),'') || '|' || Source || '|' || coalesce(ReadDate,'') FROM ReadingOrderEntry ORDER BY ItemId";

        [Fact]
        public void ReadingOrderIsIdempotent()
        {
            using var f = Migrated();
            using (var hot = Writer(f)) ReadingOrderJob.RunAll(hot, 50, _ => { });
            var once = Snapshot(f, OrderSql);
            using (var hot = Writer(f)) ReadingOrderJob.RunAll(hot, 50, _ => { });
            Assert.Equal(once, Snapshot(f, OrderSql));
        }

        [Fact]
        public void AnItemWithNoNumberAndNoDateIsUnorderableAndGetsNoIndex()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                // Item 7 is the omnibus: strip every ordering signal it has.
                hot.Begin();
                hot.Update("ComicDetail", "ItemId", 7, new { IssueNo = (string?)null, Year = (int?)null });
                hot.Exec("DELETE FROM CollectedEditionSpan WHERE ItemId = 7");
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }
            using var w = f.Hot();
            Assert.Equal(1, w.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry WHERE ItemId = 7 AND ReadIndex IS NULL"));
            Assert.Equal((long)ReadingOrderSource.Unordered, w.Scalar<long>("SELECT Source FROM ReadingOrderEntry WHERE ItemId = 7"));
            Assert.Equal(ReadingOrderParser.TierUnorderable, (int)w.Scalar<long>("SELECT ReadTier FROM ReadingOrderEntry WHERE ItemId = 7"));
        }

        [Fact]
        public void ACollectedEditionWithAKnownSpanJoinsTheMainLineAtItsSpanStart()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                // Series 2 needs a real issue run (three main-tier issues is the floor) plus a TPB collecting
                // #404-406; the pull-in must then place the TPB just BEFORE #404.
                hot.Begin();
                hot.Upsert("Item", new { Id = 500, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b406.cbz", FileName = "b406.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 500, ParsedSeriesKey = "Batman", IssueNo = "406", Year = 1987, Format = ComicFormat.SingleIssue });
                // Three MAIN-tier issues is the pull-in floor, and the fixture's item 5 is a TPB (collection
                // tier), so #406 and #407 are both needed to clear it alongside #404.
                hot.Upsert("Item", new { Id = 502, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b407.cbz", FileName = "b407.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 502, ParsedSeriesKey = "Batman", IssueNo = "407", Year = 1987, Format = ComicFormat.SingleIssue });
                hot.Upsert("Item", new { Id = 501, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\byo.cbz", FileName = "byo.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 501, ParsedSeriesKey = "Batman", IssueNo = (string?)null, Year = 1988, Format = ComicFormat.Tpb });
                hot.Upsert("CollectedEditionSpan", new { ItemId = 501, Source = EditionSource.Locg, SeriesId = 2, IssueStart = 404.0, IssueEnd = 406.0, Contiguous = true });
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            Assert.Equal((long)ReadingOrderSource.Containment, w.Scalar<long>("SELECT Source FROM ReadingOrderEntry WHERE ItemId = 501"));
            Assert.Equal(ReadingOrderParser.TierMain, (int)w.Scalar<long>("SELECT ReadTier FROM ReadingOrderEntry WHERE ItemId = 501"));
            // Its number is the span START and its suffix is negative, so it sorts immediately before #404.
            Assert.Equal(404, w.Scalar<long>("SELECT CAST(ReadNumber AS INTEGER) FROM ReadingOrderEntry WHERE ItemId = 501"));
            Assert.True(w.Scalar<double>("SELECT ReadNumberSuffix FROM ReadingOrderEntry WHERE ItemId = 501") < 0);
            var tpbIndex = w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 501");
            var issue404 = w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 4");
            Assert.True(tpbIndex < issue404, "the collected edition must read before the first issue it collects");
        }

        [Fact]
        public void ReadingOrderStampsItsRegistryRow()
        {
            using var f = Migrated();
            using (var hot = Writer(f)) ReadingOrderJob.RunAll(hot, 50, _ => { });
            using var w = f.Hot();
            Assert.Equal("books-reading-order", w.Scalar<string>("SELECT RebuildJob FROM DerivedTable WHERE Name = 'ReadingOrderEntry'"));
            Assert.True(w.Scalar<long>("SELECT RowCount FROM DerivedTable WHERE Name = 'ReadingOrderEntry'") > 0);
        }

        [Fact]
        public void TheAuditCsvHasAHeaderAndOneRowPerOrderedSeries()
        {
            using var f = Migrated();
            using var hot = Writer(f);
            ReadingOrderJob.RunAll(hot, 50, _ => { });
            var lines = ReadingOrderJob.AuditCsv(hot).ToList();
            Assert.StartsWith("seriesId,seriesName,issues,ordered", lines[0]);
            Assert.True(lines.Count > 1);
        }

        // ── containment ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ContainmentMakesTheFinestRealRunThePrimaryTrack()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                ReadingOrderJob.RunAll(hot, 50, _ => { });
                ContainmentJob.RunAll(hot, 50, _ => { });
            }
            using var w = f.Hot();
            Assert.Equal((long)TrackRole.Primary, w.Scalar<long>("SELECT TrackRole FROM CollectionNode WHERE ItemId = 1"));
            Assert.Equal((long)CollectionLevel.Issue, w.Scalar<long>("SELECT Level FROM CollectionNode WHERE ItemId = 1"));
            Assert.True(w.Scalar<long>("SELECT SpanStart FROM CollectionNode WHERE ItemId = 1") >= 1);
        }

        [Fact]
        public void AContainerWithNoOwnedContentsKeepsItsLabelAndClaimsNoChildren()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                // The fixture's series 4 holds ONLY the omnibus, which makes the omnibus its own base level.
                // Give it three issues beneath so the omnibus is a CONTAINER — the case being asserted.
                hot.Begin();
                for (var i = 0; i < 3; i++)
                {
                    var id = 600 + i;
                    hot.Upsert("Item", new { Id = id, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = $@"\\x\ff{id}.cbz", FileName = $"ff{id}.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 4 });
                    hot.Upsert("ComicDetail", new { ItemId = id, ParsedSeriesKey = "Fantastic Four Omnibus", IssueNo = (i + 90).ToString(), Year = 1975, Format = ComicFormat.SingleIssue });
                }
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
                ContainmentJob.RunAll(hot, 50, _ => { });
            }
            using var w = f.Hot();
            // Item 7 is the Fantastic Four omnibus: it collects #1-60, and the three issues we own are #90-92.
            Assert.Equal((long)TrackRole.Container, w.Scalar<long>("SELECT TrackRole FROM CollectionNode WHERE ItemId = 7"));
            Assert.Equal("#1-60", w.Scalar<string>("SELECT SpanLabel FROM CollectionNode WHERE ItemId = 7"));
            Assert.Equal(0, w.Scalar<long>("SELECT ContainsCount FROM CollectionNode WHERE ItemId = 7"));
            Assert.Equal(0, w.Scalar<long>("SELECT count(*) FROM CollectionNode WHERE ItemId = 7 AND ParentItemId IS NOT NULL"));
        }

        private const string NodeSql =
            "SELECT ItemId, Level || '|' || TrackRole || '|' || SpanStart || '|' || SpanEnd || '|' || ContainsCount || '|' || coalesce(SpanLabel,'') FROM CollectionNode ORDER BY ItemId";

        [Fact]
        public void ContainmentIsIdempotentAndStampsItsRegistryRow()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                ReadingOrderJob.RunAll(hot, 50, _ => { });
                ContainmentJob.RunAll(hot, 50, _ => { });
            }
            var once = Snapshot(f, NodeSql);
            using (var hot = Writer(f)) ContainmentJob.RunAll(hot, 50, _ => { });
            Assert.Equal(once, Snapshot(f, NodeSql));

            using var w = f.Hot();
            Assert.Equal("books-containment", w.Scalar<string>("SELECT RebuildJob FROM DerivedTable WHERE Name = 'CollectionNode'"));
        }

        [Fact]
        public void TheOverCollectionGuardDropsAnEditionWhoseSpanCannotBeARealRun()
        {
            // A "#1-2" edition whose matches land at base positions 1 and 40 has a 40-wide span for a 2-wide
            // range — a conflated-run collision. The guard keeps the label and refuses the children.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 40; i++)
                books.Add(new ContainmentJob.Book { ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, ReadNumber = i <= 2 ? i : i + 1000 });
            books[39].ReadNumber = 2;   // the last base row also overlaps the range
            books.Add(new ContainmentJob.Book { ItemId = 99, SeriesId = 1, Level = CollectionLevel.Omnibus, SpanFromStart = 1, SpanFromEnd = 2, RangeSource = EditionSource.Locg });

            ContainmentJob.BuildSeries(books);
            var container = books.Single(b => b.ItemId == 99);
            Assert.Equal("#1-2", container.SpanLabel);
            Assert.Equal(0, container.ContainsCount);
        }

        // ── collected editions (the LOCG reduction) ──────────────────────────────────────────────────────

        [Fact]
        public void TheLocgReductionTurnsContainmentEdgesIntoASpan()
        {
            using var f = Migrated();
            using (var legs = new TargetWriter(f.LegsPath, MappingContract.Load(), dryRun: false))
            {
                legs.Begin();
                legs.Upsert("LocgComicRaw", new { LocgComicId = 900001, IssueNumber = "1" });
                legs.Upsert("LocgComicRaw", new { LocgComicId = 900002, IssueNumber = "4" });
                legs.Upsert("LocgContainment", new { Id = 90001, ContainerLocgComicId = 4686349, ContainedLocgComicId = 900001 });
                legs.Upsert("LocgContainment", new { Id = 90002, ContainerLocgComicId = 4686349, ContainedLocgComicId = 900002 });
                legs.Commit();
            }
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("ItemProviderLink", new { ItemId = 7, Provider = Provider.Locg, ProviderKey = "4686349", Status = LinkStatus.Matched, Quality = LinkQuality.High, AttemptCount = 1 });
                hot.Commit();
                CollectedEditionJob.RunAll(hot, f.LegsPath, 500, _ => { });
            }

            using var w = f.Hot();
            var where = $"ItemId = 7 AND Source = {(int)EditionSource.Locg}";
            Assert.Equal(1, w.Scalar<long>($"SELECT IssueStart FROM CollectedEditionSpan WHERE {where}"));
            Assert.Equal(4, w.Scalar<long>($"SELECT IssueEnd FROM CollectedEditionSpan WHERE {where}"));
            // #1 and #4 with nothing between them is NOT contiguous, and the row says so.
            Assert.Equal(0, w.Scalar<long>($"SELECT Contiguous FROM CollectedEditionSpan WHERE {where}"));
            // The curated span for the same item is untouched — only the LOCG source is rebuilt.
            Assert.Equal(1, w.Scalar<long>($"SELECT count(*) FROM CollectedEditionSpan WHERE ItemId = 7 AND Source = {(int)EditionSource.Curated}"));
            Assert.Equal("books-collected-editions", w.Scalar<string>("SELECT RebuildJob FROM DerivedTable WHERE Name = 'CollectedEditionSpan(Source=Locg)'"));
        }

        [Fact]
        public void SpanPrecedenceIsLocgOverGcdOverCvOverCurated()
        {
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                foreach (var source in new[] { EditionSource.Curated, EditionSource.Cv, EditionSource.Gcd, EditionSource.Locg })
                    hot.Upsert("CollectedEditionSpan", new { ItemId = 7, Source = source, SeriesId = 4, IssueStart = (double)source + 1, IssueEnd = 60.0 });
                hot.Commit();
            }
            using var w = f.Hot();
            Assert.Equal(EditionSource.Locg, ReadingOrderJob.LoadSpans(w)[7].Source);
        }

        private static string Snapshot(V1Fixture f, string sql)
        {
            using var w = f.Hot();
            return string.Join(";", w.Pairs(sql).Select(p => p.Item1 + "=" + p.Item2));
        }
    }
}
