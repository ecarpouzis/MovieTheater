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

        [Fact]
        public void AManualComicVineLinkDatesABookExactlyLikeAMatchedOne()
        {
            // Wave 6's defect. The identity pass CONFIRMS a link and stores it Manual (`identity-read`), which
            // is the higher-grade read — and every consumer joining on Matched alone then lost the book: the
            // Walking Dead compendiums re-dated from 2009-05-28 to their file years (source ComicVine -> a
            // local fallback). A Manual link is at least as usable as a Matched one (LinkStatuses.Usable).
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Exec($"UPDATE ItemProviderLink SET Status = $st, Method = 'identity-read' "
                       + $"WHERE ItemId = 1 AND Provider = {(int)Provider.Cv}", ("$st", (int)LinkStatus.Manual));
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            Assert.Equal(1, w.Scalar<long>(
                $"SELECT count(*) FROM ItemProviderLink WHERE ItemId = 1 AND Provider = {(int)Provider.Cv} "
              + $"AND Status = {(int)LinkStatus.Manual}"));
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
        public void ACollectionIsDatedByTheFirstIssueOfItsJudgedRangeNotByItsVolumeNumbersIssue()
        {
            // Saga's shape, on the fixture's Batman shelf (`Series.CvVolumeId = 796`). v1 matched a collected
            // edition to the ComicVine issue whose number equals its VOLUME number, so `Saga Vol. 07` carried
            // issue #7's 2012 cover date though it collects #37-42. Each edition below keeps such a link, and
            // none of them may be dated by it.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                // Three main-tier issues is the pull-in floor, and the fixture's item 5 is a TPB, so #406 and
                // #407 are both needed alongside #404. #406 is printed a year after #404.
                hot.Upsert("Item", new { Id = 600, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b406.cbz", FileName = "b406.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 600, ParsedSeriesKey = "Batman", IssueNo = "406", Year = 1988, Format = ComicFormat.SingleIssue });
                hot.Upsert("Item", new { Id = 601, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b407.cbz", FileName = "b407.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 601, ParsedSeriesKey = "Batman", IssueNo = "407", Year = 1988, Format = ComicFormat.SingleIssue });
                // The shelf's ComicVine volume knows #404 and the three decoys, and nothing else.
                hot.Upsert("CvIssue", new { Id = 7404, VolumeId = 796, IssueNumber = "404", CoverDate = "1987-02-01" });
                hot.Upsert("CvIssue", new { Id = 7002, VolumeId = 796, IssueNumber = "2", CoverDate = "1940-06-01" });
                hot.Upsert("CvIssue", new { Id = 7003, VolumeId = 796, IssueNumber = "3", CoverDate = "1940-07-01" });
                hot.Upsert("CvIssue", new { Id = 7009, VolumeId = 796, IssueNumber = "9", CoverDate = "1941-01-01" });
                void Edition(int id, int volumeNo, int year, double from, double to, int cvIssueId)
                {
                    hot.Upsert("Item", new { Id = id, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = $@"\\x\v{volumeNo}.cbz", FileName = $"Batman Vol. {volumeNo:00}.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 150 });
                    hot.Upsert("ComicDetail", new { ItemId = id, ParsedSeriesKey = "Batman", VolumeNo = volumeNo, Year = year, Format = ComicFormat.Tpb, IsCollection = true });
                    hot.Upsert("CollectedEditionSpan", new { ItemId = id, Source = EditionSource.Curated, SeriesId = 2, IssueStart = from, IssueEnd = to, Confidence = 0.95, Contiguous = true });
                    // the v1 volume-number match, still on the item
                    hot.Upsert("ItemProviderLink", new { ItemId = id, Provider = Provider.Cv, ProviderKey = cvIssueId.ToString(), Status = LinkStatus.Matched, Quality = LinkQuality.High, AttemptCount = 1 });
                }
                Edition(602, 2, 1990, 404, 405, 7002);   // #404 is cached            → the cover date of #404
                Edition(603, 3, 1991, 406, 407, 7003);   // #406 is owned, not cached → the file's own date
                Edition(604, 9, 1995, 500, 505, 7009);   // neither                   → the book's own year
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            string? Date(int id) => w.Scalar<string>($"SELECT ReadDate FROM ReadingOrderEntry WHERE ItemId = {id}");
            // (a) the FIRST issue of the judged range, from the shelf's ComicVine volume — not issue #2's 1940.
            Assert.Equal("1987-02-01", Date(602));
            // (a) again, from the OWNED file when the volume has no such issue cached — not issue #3's 1940,
            // and not the trade's own 1991 either.
            Assert.Equal(Date(600), Date(603));
            Assert.Equal("1988-07-01", Date(603));
            // (b) neither cached nor owned: the book's own year, anchored mid-year — not issue #9's 1941.
            Assert.Equal("1995-07-01", Date(604));
            Assert.Equal((long)DatePrecision.Year, w.Scalar<long>("SELECT ReadDatePrecision FROM ReadingOrderEntry WHERE ItemId = 604"));

            // Numbered issue files keep today's logic, and so does the ORDER: each edition is still placed by
            // its span (number + negative suffix), which is what puts it just before the issues it collects.
            Assert.Equal("1987-02", Date(4)?[..7]);
            Assert.Equal((long)ReadingOrderSource.Containment, w.Scalar<long>("SELECT Source FROM ReadingOrderEntry WHERE ItemId = 602"));
            Assert.Equal(404, w.Scalar<long>("SELECT CAST(ReadNumber AS INTEGER) FROM ReadingOrderEntry WHERE ItemId = 602"));
            Assert.True(w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 602")
                      < w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 4"));
        }

        [Fact]
        public void AnEditionMatchedToItsOwnComicVineRecordKeepsThatRecordsDate()
        {
            // `Terry Moore's Echo Vol. 01` links to ComicVine volume 47310 #1 — the collected-editions volume,
            // not the shelf's run (volume 20806) — so 2009-05-31 is the TRADE's printing date, a fact about the
            // book itself. Its filename says 2018 because that is when it was scanned. The rule rejects the
            // shelf's own issue matched by number; it must not throw away the edition's own record for a
            // filename year. 3,301 live rows are in this shape against 2,611 in Saga's.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("Item", new { Id = 600, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b406.cbz", FileName = "b406.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 600, ParsedSeriesKey = "Batman", IssueNo = "406", Year = 1987, Format = ComicFormat.SingleIssue });
                hot.Upsert("Item", new { Id = 601, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b407.cbz", FileName = "b407.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 601, ParsedSeriesKey = "Batman", IssueNo = "407", Year = 1987, Format = ComicFormat.SingleIssue });
                // the edition's own record, in a DIFFERENT ComicVine volume from the shelf's run (796)
                hot.Upsert("CvIssue", new { Id = 8001, VolumeId = 47310, IssueNumber = "1", CoverDate = "2009-05-31" });
                hot.Upsert("Item", new { Id = 605, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\e1.cbz", FileName = "Echo Vol. 01 (2018).cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 150 });
                hot.Upsert("ComicDetail", new { ItemId = 605, ParsedSeriesKey = "Batman", VolumeNo = 1, Year = 2018, Format = ComicFormat.Tpb, IsCollection = true });
                // a judged range whose first issue is neither cached nor owned — (a) cannot answer
                hot.Upsert("CollectedEditionSpan", new { ItemId = 605, Source = EditionSource.Curated, SeriesId = 2, IssueStart = 900.0, IssueEnd = 905.0, Confidence = 0.95, Contiguous = true });
                hot.Upsert("ItemProviderLink", new { ItemId = 605, Provider = Provider.Cv, ProviderKey = "8001", Status = LinkStatus.Matched, Quality = LinkQuality.High, AttemptCount = 1 });
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            // (the day is clamped to 1-28 by NormalizeDate, as everywhere else)
            Assert.Equal("2009-05-28", w.Scalar<string>("SELECT ReadDate FROM ReadingOrderEntry WHERE ItemId = 605"));
        }

        [Fact]
        public void AnUnjudgedSpanNeverRedatesTheEditionThatCarriesIt()
        {
            // A provider claim may be the match's own key echoed back (§6.2). It is enough to POSITION an
            // edition — the pull-in has always believed one — but not to restate when the book was printed.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Upsert("Item", new { Id = 600, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b406.cbz", FileName = "b406.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 600, ParsedSeriesKey = "Batman", IssueNo = "406", Year = 1987, Format = ComicFormat.SingleIssue });
                hot.Upsert("Item", new { Id = 601, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\b407.cbz", FileName = "b407.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2 });
                hot.Upsert("ComicDetail", new { ItemId = 601, ParsedSeriesKey = "Batman", IssueNo = "407", Year = 1987, Format = ComicFormat.SingleIssue });
                hot.Upsert("CvIssue", new { Id = 7404, VolumeId = 796, IssueNumber = "404", CoverDate = "1987-02-01" });
                hot.Upsert("CvIssue", new { Id = 7002, VolumeId = 796, IssueNumber = "2", CoverDate = "1940-06-01" });
                hot.Upsert("Item", new { Id = 602, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\v2.cbz", FileName = "Batman Vol. 02.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 150 });
                hot.Upsert("ComicDetail", new { ItemId = 602, ParsedSeriesKey = "Batman", VolumeNo = 2, Year = 1990, Format = ComicFormat.Tpb, IsCollection = true });
                hot.Upsert("CollectedEditionSpan", new { ItemId = 602, Source = EditionSource.Cv, SeriesId = 2, IssueStart = 404.0, IssueEnd = 405.0, Confidence = 0.9, Note = "match-by: title; cv collected-editions" });
                hot.Upsert("ItemProviderLink", new { ItemId = 602, Provider = Provider.Cv, ProviderKey = "7002", Status = LinkStatus.Matched, Quality = LinkQuality.High, AttemptCount = 1 });
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            Assert.Equal("1940-06-01", w.Scalar<string>("SELECT ReadDate FROM ReadingOrderEntry WHERE ItemId = 602"));
            Assert.Equal(404, w.Scalar<long>("SELECT CAST(ReadNumber AS INTEGER) FROM ReadingOrderEntry WHERE ItemId = 602"));
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

        [Fact]
        public void ATwentySevenPageFloppyIsNotFiveIssuesHoweverAProviderLinkedIt()
        {
            // The Lobster Johnson shelf, as it stands live. LOCG matched fifteen of the thirty-one single
            // issues to the TRADES' records and wrote each a container span — "Lobster Johnson 001", a
            // 27-page floppy, carries #2-4 "5 contained". Read as the file's place in the ladder, the whole
            // shelf shifts: Vol. 01 (#1-5) collected nothing and Vol. 02 (#6-10) collected 8, 9 and 10.
            // Twenty-seven pages cannot be five issues, and the page arithmetic says so before any provider.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 10; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, ReadNumber = i,
                    PageCount = 27,
                    // the junk links: issues 1, 2, 6 and 7 were pointed at a trade
                    SpanFromStart = i is 1 or 6 ? 2 : i is 2 or 7 ? 1 : null,
                    SpanFromEnd = i is 1 or 6 ? 4 : i is 2 or 7 ? 5 : null,
                });
            books.Add(new ContainmentJob.Book { ItemId = 101, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 155, SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated });
            books.Add(new ContainmentJob.Book { ItemId = 102, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 145, SpanFromStart = 6, SpanFromEnd = 10, RangeSource = EditionSource.Curated });

            ContainmentJob.BuildSeries(books);

            var vol1 = books.Single(b => b.ItemId == 101);
            var vol2 = books.Single(b => b.ItemId == 102);
            Assert.Equal("#1-5", vol1.SpanLabel);
            Assert.Equal("#6-10", vol2.SpanLabel);
            Assert.Equal(5, vol1.ContainsCount);
            Assert.Equal(5, vol2.ContainsCount);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, books.Where(b => b.ParentItemId == 101).Select(b => b.ItemId).OrderBy(x => x));
            Assert.Equal(new[] { 6, 7, 8, 9, 10 }, books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
        }

        [Fact]
        public void TheLadderIsInIssueNumbersNotTheReadingOrdersArcNumbers()
        {
            // Baltimore, as it stands live. ComicVine numbers by ARC, so the reading order gives
            // "Baltimore 006 - The Curse Bells 01 (of 05)" the number 1, and the same for the first file of
            // every one of the eight miniseries. The volumes claim #1-5, #6-10 ... #36-40 — the continuous
            // numbering — so nesting by the arc number drops the first file of each arc and hands it to
            // whatever wider container also covers #1. Reading order keeps its answer; the ladder does not use it.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 10; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, PageCount = 25,
                    IssueNumber = i,
                    ReadNumber = i == 1 || i == 6 ? 1 : i,     // the arc-firsts, as ComicVine numbers them
                });
            books.Add(new ContainmentJob.Book { ItemId = 101, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 143, SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated });
            books.Add(new ContainmentJob.Book { ItemId = 102, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 149, SpanFromStart = 6, SpanFromEnd = 10, RangeSource = EditionSource.Curated });

            ContainmentJob.BuildSeries(books);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, books.Where(b => b.ParentItemId == 101).Select(b => b.ItemId).OrderBy(x => x));
            Assert.Equal(new[] { 6, 7, 8, 9, 10 }, books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
        }

        [Fact]
        public void AVolumeInsideAQuotedHoleDoesNotNestUnderTheBookThatSkippedIt()
        {
            // Hellboy Omnibus Vol. 03, as it stands live. Its indicia names Darkness Calls, The Wild Hunt and
            // The Storm and the Fury — volumes 8, 9 and 12 on this shelf — so it is stored as a bounding 8-12.
            // Volumes 10 and 11 are the short-story collections that went into `Hellboy - The Complete Short
            // Stories` instead, and they must not appear inside a book that never printed them.
            var books = new List<ContainmentJob.Book>();
            for (var v = 1; v <= 12; v++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = v, SeriesId = 1, Level = CollectionLevel.Volume, VolumeNo = v,
                    ReadIndex = v, PageCount = 160,
                });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 103, SeriesId = 1, Level = CollectionLevel.Omnibus, PageCount = 529,
                SpanFromStart = 8, SpanFromEnd = 12, RangeSource = EditionSource.Curated,
                RangeNote = "its indicia names Darkness Calls, The Wild Hunt and The Storm and the Fury, "
                          + "which on this shelf are '8, 9, 12'",
            });

            ContainmentJob.BuildSeries(books);

            Assert.Equal(new[] { 8, 9, 12 }, books.Where(b => b.ParentItemId == 103).Select(b => b.ItemId).OrderBy(x => x));
        }

        // ── the run refs: "#1-4" and "#1-5" are not one range inside another when the runs differ ───────

        /// <summary>Run refs as a reader's `C` lines make them: (leg, key) with no range of its own, which
        /// means "the range on the span". The four-part form below adds the range in THAT run's numbering.</summary>
        private static IReadOnlyList<ReadingOrderJob.SpanRun> Runs(params (Provider P, string Key)[] xs) =>
            xs.Select(x => new ReadingOrderJob.SpanRun(x.P, x.Key, null, null)).ToList();

        private static IReadOnlyList<ReadingOrderJob.SpanRun> Runs(params (Provider P, string Key, double A, double B)[] xs) =>
            xs.Select(x => new ReadingOrderJob.SpanRun(x.P, x.Key, x.A, x.B)).ToList();

        /// <summary>A Hellboy-shaped shelf: a trade of one mini, and a book that collects another mini.</summary>
        private static List<ContainmentJob.Book> TwoMinis(
            IReadOnlyList<ReadingOrderJob.SpanRun>? trade, IReadOnlyList<ReadingOrderJob.SpanRun>? book) =>
        [
            new ContainmentJob.Book
            {
                ItemId = 101, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 130, IsCollection = true,
                SpanFromStart = 1, SpanFromEnd = 4, RangeSource = EditionSource.Curated, Runs = trade,
            },
            new ContainmentJob.Book
            {
                ItemId = 201, SeriesId = 1, Level = CollectionLevel.Book, PageCount = 400, IsCollection = true,
                SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated, Runs = book,
            },
        ];

        [Fact]
        public void TwoJudgedRangesCountedInDifferentRunsNeverNest()
        {
            // Hellboy Vol. 01 collects Seed of Destruction #1-4; Vol. 02 collects Wake the Devil #1-5. The
            // numbers say "1-4 is inside 1-5" and the numbers are talking about two different comics.
            var books = TwoMinis(
                Runs((Provider.Cv, "10")),
                Runs((Provider.Cv, "20")));
            ContainmentJob.BuildSeries(books);
            Assert.Null(books.Single(b => b.ItemId == 101).ParentItemId);
        }

        [Fact]
        public void TheSameRunOnBothSidesNestsAsItAlwaysDid()
        {
            var books = TwoMinis(
                Runs((Provider.Cv, "10")),
                Runs((Provider.Cv, "10")));
            ContainmentJob.BuildSeries(books);
            Assert.Equal(201, books.Single(b => b.ItemId == 101).ParentItemId);
        }

        [Fact]
        public void NoRunRefOnEitherSideIsTodaysBehaviour()
        {
            // The veto has to be ARMED by a reader. 20,498 collected editions carry no ref at all, and not
            // one of their nestings may change because this field exists.
            Assert.Equal(201, Parent(TwoMinis(null, null)));
            Assert.Equal(201, Parent(TwoMinis(Runs((Provider.Cv, "10")), null)));
            Assert.Equal(201, Parent(TwoMinis(null, Runs((Provider.Cv, "20")))));
            // …and legs that do not overlap say nothing about each other
            Assert.Equal(201, Parent(TwoMinis(Runs((Provider.Cv, "10")),
                                              Runs((Provider.Gcd, "99")))));

            static int? Parent(List<ContainmentJob.Book> books)
            {
                ContainmentJob.BuildSeries(books);
                return books.Single(b => b.ItemId == 101).ParentItemId;
            }
        }

        [Fact]
        public void TheRangeBasedParentTestRefusesADifferentRunToo()
        {
            // Saga's shape — a container with nothing of its own on disk, placed by its range alone — with
            // the ranges belonging to two different runs. Base books are issues #49-54, so neither container
            // holds a file and both fall to the range-based test.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 6; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, PageCount = 24,
                    IssueNumber = 48 + i, ReadNumber = 48 + i,
                });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 101, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 150, IsCollection = true,
                SpanFromStart = 37, SpanFromEnd = 42, RangeSource = EditionSource.Curated,
                Runs = Runs((Provider.Cv, "10")),
            });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 102, SeriesId = 1, Level = CollectionLevel.Book, PageCount = 500, IsCollection = true,
                SpanFromStart = 37, SpanFromEnd = 54, RangeSource = EditionSource.Curated,
                Runs = Runs((Provider.Cv, "20")),
            });

            ContainmentJob.BuildSeries(books);
            Assert.Equal(0, books.Single(b => b.ItemId == 101).SpanEnd);      // it really is the range-based path
            Assert.Null(books.Single(b => b.ItemId == 101).ParentItemId);

            // the same shelf with one run: the volume nests in the book, as it does live
            books.Single(b => b.ItemId == 102).Runs = Runs((Provider.Cv, "10"));
            ContainmentJob.BuildSeries(books);
            Assert.Equal(102, books.Single(b => b.ItemId == 101).ParentItemId);
        }

        // ── a range counted in a run only reaches files that are IN that run ────────────────────────────

        /// <summary>
        /// The Baltimore / Lobster Johnson shape: ten files a ripper numbered 001-010 over two miniseries,
        /// and Vol. 02, whose judged range is now the SECOND mini's own #1-5 rather than the folder's #6-10.
        /// <paramref name="linked"/> gives files 6-10 a ComicVine link into volume 20 at issue numbers 1-5.
        /// </summary>
        private static List<ContainmentJob.Book> RenumberedChain(bool linked, string? volOfVolume2 = "20")
        {
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 10; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, PageCount = 27,
                    IssueNumber = i, ReadNumber = i,
                    FileRuns = !linked ? null
                        : new Dictionary<Provider, string> { [Provider.Cv] = i <= 5 ? "10" : "20" },
                    ProviderIssueNumber = !linked ? null : i <= 5 ? i : i - 5,
                });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 102, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 150, IsCollection = true,
                SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated,
                Runs = volOfVolume2 == null ? null : Runs((Provider.Cv, volOfVolume2)),
            });
            return books;
        }

        [Fact]
        public void ARangeCountedInARunReachesNothingOnARefusedShelfWhoseFilesHaveNoLinks()
        {
            // No file says which run it is in, and the shelf has no identity of its own. "#1-5 of volume 20"
            // must NOT collect the ripper's 001-005, which are the other mini. Flat is right until the split.
            var books = RenumberedChain(linked: false);
            ContainmentJob.BuildSeries(books, shelfRuns: null);

            var vol2 = books.Single(b => b.ItemId == 102);
            Assert.Equal("#1-5", vol2.SpanLabel);          // the range is still stated
            Assert.Equal(0, vol2.ContainsCount);           // it just reaches nothing
            Assert.All(books.Where(b => b.ItemId <= 10), b => Assert.Null(b.ParentItemId));
        }

        [Fact]
        public void AFileLinkedIntoTheRunNestsAtTheProvidersIssueNumberNotTheRippersOwn()
        {
            // Same shelf, with the links the packet would have: 006-010 are volume 20's issues #1-5. They
            // nest — by the provider's numbers, which is what the range is written in — and 001-005, which
            // are volume 10, do not.
            var books = RenumberedChain(linked: true);
            ContainmentJob.BuildSeries(books, shelfRuns: null);

            Assert.Equal(new[] { 6, 7, 8, 9, 10 },
                books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
            Assert.Equal(5, books.Single(b => b.ItemId == 102).ContainsCount);
        }

        [Fact]
        public void AShelfWhoseOwnIdentityIsTheRunBehavesExactlyAsItDidBefore()
        {
            // The ordinary case, and the one that must not move: the shelf IS volume 20, so its files are in
            // that run by living here, links or no links, and the range counts them by their own numbers.
            var books = RenumberedChain(linked: false);
            ContainmentJob.BuildSeries(books,
                new Dictionary<Provider, string> { [Provider.Cv] = "20" });

            Assert.Equal(new[] { 1, 2, 3, 4, 5 },
                books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
        }

        [Fact]
        public void AFilesOwnLinkOutranksTheShelfIdentityWhenTheyDisagree()
        {
            // S100573, as wave 5's audit found it: a merge on a wrong stored link put Flash (1959) and Flash
            // v2 (2007-2009) on one shelf, whose identity is CV 1995 (the 1959 run). Both runs have a #232.
            // The 1975 file's gold #232-232 must not swallow the 2007 file, whose own GCD link names series
            // 26125 — Wally West's run. The shelf makes a file eligible only when the file does not say
            // otherwise.
            var nineteenSeventyFive = new ContainmentJob.Book
            {
                ItemId = 1, SeriesId = 1, Level = CollectionLevel.Volume, PageCount = 100, IsCollection = true,
                SpanFromStart = 232, SpanFromEnd = 232, RangeSource = EditionSource.Curated,
                Runs = Runs((Provider.Cv, "1995")),
            };
            var twoThousandSeven = new ContainmentJob.Book
            {
                ItemId = 2, SeriesId = 1, Level = CollectionLevel.Issue, PageCount = 22, ReadIndex = 1,
                IssueNumber = 232, ReadNumber = 232,
                FileRuns = new Dictionary<Provider, string> { [Provider.Gcd] = "26125" },
            };
            var alsoNineteenFiftyNine = new ContainmentJob.Book
            {
                ItemId = 3, SeriesId = 1, Level = CollectionLevel.Issue, PageCount = 36, ReadIndex = 2,
                IssueNumber = 232, ReadNumber = 232,
            };
            var shelf = new Dictionary<Provider, string> { [Provider.Cv] = "1995", [Provider.Gcd] = "1234" };

            var books = new List<ContainmentJob.Book> { nineteenSeventyFive, twoThousandSeven, alsoNineteenFiftyNine };
            ContainmentJob.BuildSeries(books, shelf);

            Assert.Null(books.Single(b => b.ItemId == 2).ParentItemId);      // its own link says another run
            Assert.Equal(1, books.Single(b => b.ItemId == 3).ParentItemId);  // no link of its own: the shelf decides
        }

        [Fact]
        public void ARangeWithNoRunRefsIsMeasuredExactlyAsItIsToday()
        {
            // The 20,498 editions nobody has read yet. Even where the files carry links into another volume,
            // a range that names no run is positional, as it has always been.
            var books = RenumberedChain(linked: true, volOfVolume2: null);
            ContainmentJob.BuildSeries(books, shelfRuns: null);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 },
                books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
        }

        [Fact]
        public void EqualRangesInDifferentRunsAreNotTwoEditionsOfOneComic()
        {
            // The range-based pass treats an EQUAL range as "the same material, twice" and refuses to nest
            // it. With different runs it is not the same material either — the refusal is the same, the
            // reason is not, and neither book may end up inside the other.
            var books = TwoMinis(
                Runs((Provider.Cv, "10")),
                Runs((Provider.Cv, "20")));
            books.Single(b => b.ItemId == 201).SpanFromEnd = 4;
            ContainmentJob.BuildSeries(books);
            Assert.Null(books.Single(b => b.ItemId == 101).ParentItemId);
            Assert.Null(books.Single(b => b.ItemId == 201).ParentItemId);
        }

        // ── per-run RANGES: one book, several runs, each counted in its own numbering (TOOLS_TODO 17) ────

        [Fact]
        public void ATradeCollectingTwoMinisReachesBothSetsOfFilesInEachMinisOwnNumbering()
        {
            // The shape a single `C` line could not state: one book collecting TWO runs, whose numbering
            // differs (Hellboy Vol. 06/12, Hell on Earth Vol. 02/04/05/07, every omnibus). Ten files a ripper
            // numbered 001-010: 001-005 are ComicVine volume 10 issues #1-5, 006-010 are volume 20 issues
            // #101-105 — the legacy-numbered continuation. The book names both runs, each with ITS range.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 10; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, PageCount = 27,
                    IssueNumber = i, ReadNumber = i,
                    FileRuns = new Dictionary<Provider, string> { [Provider.Cv] = i <= 5 ? "10" : "20" },
                    ProviderIssueNumber = i <= 5 ? i : 95 + i,
                });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 102, SeriesId = 1, Level = CollectionLevel.Omnibus, PageCount = 600, IsCollection = true,
                // the span's own range is the FIRST C line's, as apply_identity writes it
                SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated,
                Runs = Runs((Provider.Cv, "10", 1, 5), (Provider.Cv, "20", 101, 105)),
            });

            ContainmentJob.BuildSeries(books, shelfRuns: null);

            Assert.Equal(Enumerable.Range(1, 10),
                books.Where(b => b.ParentItemId == 102).Select(b => b.ItemId).OrderBy(x => x));
            Assert.Equal(10, books.Single(b => b.ItemId == 102).ContainsCount);
        }

        [Fact]
        public void AFileInNeitherNamedRunIsStillRefused()
        {
            // The same book, and a file whose own ComicVine link names a third volume. Naming two runs widens
            // what a range reaches; it does not stop the veto being a veto.
            var books = new List<ContainmentJob.Book>
            {
                new()
                {
                    ItemId = 1, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = 1, PageCount = 27,
                    IssueNumber = 1, ReadNumber = 1,
                    FileRuns = new Dictionary<Provider, string> { [Provider.Cv] = "99" }, ProviderIssueNumber = 1,
                },
                new()
                {
                    ItemId = 2, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = 2, PageCount = 27,
                    IssueNumber = 2, ReadNumber = 2,
                    FileRuns = new Dictionary<Provider, string> { [Provider.Cv] = "20" }, ProviderIssueNumber = 101,
                },
                new()
                {
                    ItemId = 102, SeriesId = 1, Level = CollectionLevel.Omnibus, PageCount = 600, IsCollection = true,
                    SpanFromStart = 1, SpanFromEnd = 5, RangeSource = EditionSource.Curated,
                    Runs = Runs((Provider.Cv, "10", 1, 5), (Provider.Cv, "20", 101, 105)),
                },
            };

            ContainmentJob.BuildSeries(books, shelfRuns: null);

            Assert.Null(books.Single(b => b.ItemId == 1).ParentItemId);
            Assert.Equal(102, books.Single(b => b.ItemId == 2).ParentItemId);
        }

        [Fact]
        public void TwoJudgedRangesAreComparedInTheRunTheyShare()
        {
            // Return of the Master: CV 51622 #1-5 IS GCD 71228 #103-107. A trade that states the CV numbering
            // and a book that states the GCD numbering of the SAME issues must not be read as #1-5 inside
            // #103-107 by arithmetic — the shared run's own ranges are the ones compared, and there they are
            // the same material, twice.
            var books = TwoMinis(
                Runs((Provider.Cv, "51622", 1, 5), (Provider.Gcd, "71228", 103, 107)),
                Runs((Provider.Gcd, "71228", 103, 107)));
            books.Single(b => b.ItemId == 201).SpanFromStart = 103;
            books.Single(b => b.ItemId == 201).SpanFromEnd = 107;
            ContainmentJob.BuildSeries(books);
            Assert.Null(books.Single(b => b.ItemId == 101).ParentItemId);
        }

        [Fact]
        public void AnOmnibusThatAlsoNamesASecondRunStillHoldsTheTradeOfTheFirst()
        {
            // …and the mirror: an equal range is "the same material twice" only when the two books name the
            // same material. The omnibus collects this whole mini AND another, so the trade sits inside it.
            var books = TwoMinis(
                Runs((Provider.Cv, "10", 1, 4)),
                Runs((Provider.Cv, "10", 1, 4), (Provider.Cv, "20", 1, 5)));
            ContainmentJob.BuildSeries(books);
            Assert.Equal(201, books.Single(b => b.ItemId == 101).ParentItemId);
        }

        [Fact]
        public void ARunNamedTwiceOnOneLegIsComparableWhenEitherKeyMatches()
        {
            // Comparability is per (leg, key), not per leg: a book naming two ComicVine volumes shares a
            // coordinate system with the trade of either of them, and with the trade of neither third one.
            var twoRuns = Runs((Provider.Cv, "10"), (Provider.Cv, "20"));
            Assert.Equal(201, Parent(TwoMinis(Runs((Provider.Cv, "20")), twoRuns)));
            Assert.Null(Parent(TwoMinis(Runs((Provider.Cv, "30")), twoRuns)));

            static int? Parent(List<ContainmentJob.Book> books)
            {
                ContainmentJob.BuildSeries(books);
                return books.Single(b => b.ItemId == 101).ParentItemId;
            }
        }

        [Fact]
        public void AFileBigEnoughToHoldItsSpanStillKeepsIt()
        {
            // The other end of the same population: a 1,203-page book the parser called a single issue really
            // is a collection, and its span is the truth about it. Only the arithmetic separates the two.
            var books = new List<ContainmentJob.Book>();
            for (var i = 1; i <= 6; i++)
                books.Add(new ContainmentJob.Book { ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = i, ReadNumber = i, PageCount = 24 });
            books.Add(new ContainmentJob.Book
            {
                ItemId = 50, SeriesId = 1, Level = CollectionLevel.Issue, ReadIndex = 50, ReadNumber = 50,
                PageCount = 1203, SpanFromStart = 1, SpanFromEnd = 40,
            });
            books.Add(new ContainmentJob.Book { ItemId = 101, SeriesId = 1, Level = CollectionLevel.Omnibus, PageCount = 1400, SpanFromStart = 1, SpanFromEnd = 40, RangeSource = EditionSource.Curated });

            ContainmentJob.BuildSeries(books);
            Assert.Equal(101, books.Single(b => b.ItemId == 50).ParentItemId);
        }

        /// <summary>Saga's shape (S14966), the acceptance gate, as it stood live on 2026-09-10.</summary>
        private static List<ContainmentJob.Book> SagaShape()
        {
            var books = new List<ContainmentJob.Book>();
            // the only files we own of Book 03's range: the six issues Vol. 09 collects
            for (var i = 49; i <= 54; i++)
                books.Add(new ContainmentJob.Book
                {
                    ItemId = i, SeriesId = 1, Level = CollectionLevel.Issue,
                    ReadIndex = i - 48, IssueNumber = i, PageCount = 24,
                });
            void Edition(int id, CollectionLevel level, int pages, double from, double to) =>
                books.Add(new ContainmentJob.Book
                {
                    ItemId = id, SeriesId = 1, Level = level, PageCount = pages, IsCollection = true,
                    SpanFromStart = from, SpanFromEnd = to, RangeSource = EditionSource.Curated,
                });
            Edition(2, CollectionLevel.Book, 504, 19, 36);       // Saga Book 02 — nothing of it on disk
            Edition(3, CollectionLevel.Book, 504, 37, 54);       // Saga Book 03
            Edition(7, CollectionLevel.Volume, 152, 37, 42);     // Vol. 07 — nothing of it on disk
            Edition(8, CollectionLevel.Volume, 152, 43, 48);     // Vol. 08 — nothing of it on disk
            Edition(9, CollectionLevel.Volume, 152, 49, 54);     // Vol. 09 — holds the six files
            return books;
        }

        [Fact]
        public void AJudgedEditionHoldingNoneOfOurIssuesStillSitsInsideItsBook()
        {
            // Saga, the acceptance gate. Book 03 collects #37-54 and we own only #49-54 as files, so Vol. 07
            // and Vol. 08 have no base position at all — and the positional pass, which is the only one there
            // was, left them flat beside Book 02 under "Editions without a known range". Their ranges ARE
            // known; there is simply nothing of them on disk. 419 editions on 75 shelves are in this shape.
            var books = SagaShape();
            ContainmentJob.BuildSeries(books);

            var vol7 = books.Single(b => b.ItemId == 7);
            var vol8 = books.Single(b => b.ItemId == 8);
            Assert.Equal(3, vol7.ParentItemId);
            Assert.Equal(3, vol8.ParentItemId);
            Assert.Equal("#37-42", vol7.SpanLabel);
            Assert.Equal("#43-48", vol8.SpanLabel);
            // The node gains a parent and NOTHING else: positions and the owned-file count are untouched.
            Assert.Equal((0, 0, 0), (vol7.SpanStart, vol7.SpanEnd, vol7.ContainsCount));
            Assert.Equal((0, 0, 0), (vol8.SpanStart, vol8.SpanEnd, vol8.ContainsCount));

            // Vol. 09 holds the files and nests exactly as it did before; Book 02 has no wider sibling.
            var vol9 = books.Single(b => b.ItemId == 9);
            Assert.Equal(3, vol9.ParentItemId);
            Assert.Equal(6, vol9.ContainsCount);
            Assert.Null(books.Single(b => b.ItemId == 2).ParentItemId);
            Assert.Null(books.Single(b => b.ItemId == 3).ParentItemId);
        }

        [Fact]
        public void TwoJudgedEditionsOfTheSameMaterialNeverNestOneInsideTheOther()
        {
            // Equal ranges are a Book and a Volume printing the same issues — a hardcover and its trade —
            // not a containment. Strict widening is also what stops "every empty container nests under any
            // other empty one", which is the defect the SpanEnd <= 0 leaf rule was bought with.
            var books = SagaShape();
            books.Single(b => b.ItemId == 3).SpanFromStart = 37;
            books.Single(b => b.ItemId == 3).SpanFromEnd = 42;   // Book 03 now claims exactly Vol. 07's range
            ContainmentJob.BuildSeries(books);

            Assert.Null(books.Single(b => b.ItemId == 7).ParentItemId);
            Assert.Null(books.Single(b => b.ItemId == 8).ParentItemId);
        }

        [Fact]
        public void AnUnjudgedSpanNestsNothingAndIsNestedByNothing()
        {
            // Both sides have to be a range a person judged. A provider's claim may be the match's own key
            // echoed back (§6.2), and acting on one would nest an edition inside a book on no evidence.
            var books = SagaShape();
            books.Single(b => b.ItemId == 7).RangeSource = EditionSource.Cv;    // the child is unjudged
            ContainmentJob.BuildSeries(books);
            Assert.Null(books.Single(b => b.ItemId == 7).ParentItemId);
            Assert.Equal(3, books.Single(b => b.ItemId == 8).ParentItemId);

            books = SagaShape();
            books.Single(b => b.ItemId == 3).RangeSource = EditionSource.Locg;  // the parent is unjudged
            ContainmentJob.BuildSeries(books);
            Assert.Null(books.Single(b => b.ItemId == 7).ParentItemId);
            Assert.Null(books.Single(b => b.ItemId == 8).ParentItemId);
        }

        [Fact]
        public void ABookNobodyFlaggedAsACollectionNestsNothingHoweverWideItsRange()
        {
            // `Iron Man Epic Collection` (S9575), the shelf this guard was bought on. It holds unrelated
            // trades, each judged in ITS OWN coordinates, and item 82200 — `Iron Man 2020 (2018)`, whose
            // #1-6 is really Machine Man #1-4 — took `Ultimate Iron Man` #1-5 inside it. It is flagged
            // IsCollection = 0, and PLAN §14.13: a container that is not flagged as a collection is a
            // container nobody judged, so its range is unreviewed and may not nest anything.
            var books = SagaShape();
            books.Single(b => b.ItemId == 3).IsCollection = false;
            ContainmentJob.BuildSeries(books);

            Assert.Null(books.Single(b => b.ItemId == 7).ParentItemId);
            Assert.Null(books.Single(b => b.ItemId == 8).ParentItemId);
            // The positional pass is untouched: Vol. 09 holds the files and still nests as it did.
            Assert.Equal(3, books.Single(b => b.ItemId == 9).ParentItemId);
        }

        [Fact]
        public void AnIssueNestsInTheInnermostContainerWhenTheirWindowsTie()
        {
            // Saga again, the other half of the same defect. `Book 03` collects #37-54 and `Vol. 09` collects
            // #49-54, but we own only #49-54 as files, so BOTH measure positions 1-6 (live: 8-13). The tie went
            // to whichever came first in the list — the Book — so the six issues sat beside the Volume that
            // holds them instead of inside it, and the reading list showed an empty Vol. 09.
            var books = SagaShape();
            ContainmentJob.BuildSeries(books);

            Assert.Equal(9, books.Single(b => b.ItemId == 49).ParentItemId);
            Assert.Equal(new[] { 49, 50, 51, 52, 53, 54 },
                         books.Where(b => b.ParentItemId == 9).Select(b => b.ItemId).OrderBy(x => x));
            // …and the Volume itself still nests in the Book, so the ladder is issue → volume → book.
            Assert.Equal(3, books.Single(b => b.ItemId == 9).ParentItemId);
            Assert.DoesNotContain(books, b => b.Level == CollectionLevel.Issue && b.ParentItemId == 3);
        }

        [Fact]
        public void ANarrowerWindowStillBeatsAnInnerLevel()
        {
            // The tie-break is only a TIE-break: the smallest window that covers the child is still the first
            // word, whatever level it sits at. Vol. 09 covers #49-54 and a hypothetical Book covering only
            // #53-54 is tighter around #54 even though a Book is the outer level.
            var books = SagaShape();
            books.Add(new ContainmentJob.Book
            {
                ItemId = 30, SeriesId = 1, Level = CollectionLevel.Book, PageCount = 90, IsCollection = true,
                SpanFromStart = 53, SpanFromEnd = 54, RangeSource = EditionSource.Curated,
            });
            ContainmentJob.BuildSeries(books);

            Assert.Equal(30, books.Single(b => b.ItemId == 53).ParentItemId);
            Assert.Equal(30, books.Single(b => b.ItemId == 54).ParentItemId);
            Assert.Equal(9, books.Single(b => b.ItemId == 49).ParentItemId);
        }

        [Fact]
        public void AnInnerBookNobodyFlaggedAsACollectionDoesNotWinTheTie()
        {
            // `Green Hornet - Sky Lights Collection` (S7899) and `The Amazing Spider-Man (2023) (DCP Webrips)`
            // (S34339): a Volume-level book flagged IsCollection = 0, tying with the flagged omnibus above it.
            // PLAN §14.13 — a container nobody flagged is a container nobody judged — so the inner-level
            // preference stops at the flag, and `audit_containment` stays at zero.
            var books = SagaShape();
            books.Single(b => b.ItemId == 9).IsCollection = false;   // Vol. 09, the inner tie-mate
            ContainmentJob.BuildSeries(books);

            Assert.Equal(3, books.Single(b => b.ItemId == 49).ParentItemId);
            Assert.DoesNotContain(books, b => b.Level == CollectionLevel.Issue && b.ParentItemId == 9);
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
        public void SpanPrecedenceIsCuratedFirstThenTheTitleMatchedProducers()
        {
            // The order is `SpanSelection.Rank`, not the source enum: hand-read indicia first, then a ComicVine
            // span produced by an edition-TITLE match, then a complete LOCG table of contents. Four noteless
            // rows are all the operator ever had; the curated one is the only one that says how it was decided.
            using var f = Migrated();
            using (var hot = Writer(f))
            {
                hot.Begin();
                foreach (var source in new[] { EditionSource.Curated, EditionSource.Cv, EditionSource.Gcd, EditionSource.Locg })
                    hot.Upsert("CollectedEditionSpan", new { ItemId = 7, Source = source, SeriesId = 4, IssueStart = (double)source + 1, IssueEnd = 60.0 });
                hot.Commit();
            }
            using (var w = f.Hot()) Assert.Equal(EditionSource.Curated, ReadingOrderJob.LoadSpans(w)[7].Source);

            // Drop the curated row and the title-matched ComicVine span leads; without its note it would rank
            // with GCD and the complete LOCG span would win instead.
            using (var hot = Writer(f))
            {
                hot.Begin();
                hot.Exec($"DELETE FROM CollectedEditionSpan WHERE ItemId = 7 AND Source = {(int)EditionSource.Curated}");
                hot.Update("CollectedEditionSpan", "ItemId", 7, new { Note = "60 contained" });
                hot.Exec($"UPDATE CollectedEditionSpan SET Note = 'match-by: title; cv collected-editions' WHERE ItemId = 7 AND Source = {(int)EditionSource.Cv}");
                hot.Commit();
            }
            using (var w = f.Hot()) Assert.Equal(EditionSource.Cv, ReadingOrderJob.LoadSpans(w)[7].Source);
        }

        private static string Snapshot(V1Fixture f, string sql)
        {
            using var w = f.Hot();
            return string.Join(";", w.Pairs(sql).Select(p => p.Item1 + "=" + p.Item2));
        }
    }
}
