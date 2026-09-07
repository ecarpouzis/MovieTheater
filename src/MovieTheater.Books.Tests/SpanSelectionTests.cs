using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Parse;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// WHICH "collects #a-#b" claim wins when the providers disagree (<see cref="SpanSelection"/>), and what the
    /// reading order then does with the winner. Every candidate below is a row the library actually holds —
    /// "Wolverine Omnibus Book 03" (1,226 pages) with GCD claiming it collects the single issue #44, "Saga Book
    /// 1" with GCD claiming #1-6 against ComicVine's correct #1-18, and "Rachel Rising Vol. 01", a GCD span with
    /// a numeric match key that is nonetheless a good TITLE match and must NOT be demoted.
    /// </summary>
    public class SpanSelectionTests
    {
        private static SpanSelection.Candidate Gcd(double a, double b, double conf, string? note, string? key) =>
            new(EditionSource.Gcd, a, b, conf, note, key);

        private const string ByNum = "gcd reprint; 1 issues; match-by: num";
        private const string ByTitle = "gcd reprint; 6 issues; match-by: title";

        [Fact]
        public void ADegenerateSpanOnAThousandPageOmnibusIsDiscarded()
        {
            // Wolverine Omnibus Book 03: Gcd #44-44 conf 0.8 used to beat Curated #31-59 conf 0.97 on source
            // order alone. A 1,226-page book does not collect one issue.
            var candidates = new[]
            {
                Gcd(44, 44, 0.8, ByNum, "3"),
                new SpanSelection.Candidate(EditionSource.Curated, 31, 59, 0.97, "issue: p001 back cover", null),
            };
            var win = SpanSelection.Select(candidates, isCollection: true, pageCount: 1226);
            Assert.NotNull(win);
            Assert.Equal(EditionSource.Curated, win!.Value.Source);
            Assert.Equal(31, win.Value.Start);
            Assert.Equal(59, win.Value.End);
        }

        [Fact]
        public void AnIssueKeyedGcdSpanRanksBelowComicVineAndCurated()
        {
            // Saga Book 1 (505 pages) collects #1-18. GCD matched the wrongly-parsed issue number 1 and claimed
            // #1-6; ComicVine's #1-18 at 1.0 is the right answer and now wins.
            var candidates = new[]
            {
                Gcd(1, 6, 0.8, "gcd reprint; 6 issues; match-by: num", "1"),
                new SpanSelection.Candidate(EditionSource.Cv, 1, 18, 1.0, null, null),
            };
            var win = SpanSelection.Select(candidates, isCollection: true, pageCount: 505);
            Assert.Equal(EditionSource.Cv, win!.Value.Source);
            Assert.Equal(18, win.Value.End);
        }

        [Fact]
        public void AGcdSpanMatchedByTitleKeepsItsRankEvenWithANumericKey()
        {
            // "Rachel Rising Vol. 01" → GCD key "1", but the note says match-by: title. That is not the
            // issue-keyed failure mode and the span is good (#1-6): it stays in the title-matched class (3),
            // not the issue-keyed safety-net class (5).
            var c = Gcd(1, 6, 0.9, ByTitle, "1");
            Assert.False(SpanSelection.IsIssueKeyedGcd(c));
            Assert.Equal(3, SpanSelection.Rank(c));
            Assert.True(SpanSelection.IsIssueKeyedGcd(Gcd(44, 44, 0.8, ByNum, "3")));
            Assert.Equal(5, SpanSelection.Rank(Gcd(44, 44, 0.8, ByNum, "3")));
            // With no note at all, a bare numeric match key is the only signal there is.
            Assert.True(SpanSelection.IsIssueKeyedGcd(Gcd(3, 3, 0.8, null, "3")));
            Assert.False(SpanSelection.IsIssueKeyedGcd(Gcd(3, 9, 0.8, null, "Saga Deluxe Edition Book One")));
        }

        [Fact]
        public void TheSixClassesRankInOrder()
        {
            // 0 Curated, 1 Cv title-matched, 2 complete LOCG, 3 Gcd-by-title and legacy Cv, 4 partial LOCG,
            // 5 issue-keyed Gcd.
            Assert.Equal(0, SpanSelection.Rank(new(EditionSource.Curated, 31, 59, 0.97, "issue: indicia p2", null)));
            Assert.Equal(1, SpanSelection.Rank(new(EditionSource.Cv, 1, 18, 0.94, "match-by: title; cv collected-editions", null)));
            Assert.Equal(2, SpanSelection.Rank(new(EditionSource.Locg, 1, 12, 0.4, "12 contained", null)));
            Assert.Equal(3, SpanSelection.Rank(Gcd(1, 6, 0.9, ByTitle, "1")));
            Assert.Equal(3, SpanSelection.Rank(new(EditionSource.Cv, 1, 18, 1.0, null, null)));  // legacy: no note, ranks with GCD
            Assert.Equal(4, SpanSelection.Rank(new(EditionSource.Locg, 1, 12, 0.4, "3 contained", null)));
            Assert.Equal(5, SpanSelection.Rank(Gcd(44, 44, 0.8, ByNum, "3")));
        }

        [Fact]
        public void CuratedBeatsEveryProducer()
        {
            var candidates = new[]
            {
                Gcd(21, 25, 0.9, ByTitle, "5"),
                new SpanSelection.Candidate(EditionSource.Cv, 37, 42, 0.99, "match-by: title", null),
                new SpanSelection.Candidate(EditionSource.Curated, 55, 60, 0.4, "issue: indicia p2", null),
            };
            Assert.Equal(EditionSource.Curated, SpanSelection.Select(candidates, true, 152)!.Value.Source);
        }

        [Fact]
        public void ATitleMatchedComicVineSpanBeatsALocgTableOfContents()
        {
            // ComicVine's "Collected Editions" list is the publisher's own statement of what the edition
            // collects; LOCG's is a scrape that truncates to whatever was seen.
            var locg = new SpanSelection.Candidate(EditionSource.Locg, 1, 12, 0.4, "12 contained", null);
            var cv = new SpanSelection.Candidate(EditionSource.Cv, 1, 18, 0.94, "match-by: title; cv collected-editions", null);
            Assert.Equal(EditionSource.Cv, SpanSelection.Select(new[] { locg, cv }, true, 400)!.Value.Source);
            // …but a LEGACY Cv row, with nothing in it saying how it was decided, does not: LOCG's complete
            // table of contents outranks it.
            var legacy = new SpanSelection.Candidate(EditionSource.Cv, 1, 18, 1.0, null, null);
            Assert.Equal(EditionSource.Locg, SpanSelection.Select(new[] { locg, legacy }, true, 400)!.Value.Source);
        }

        [Fact]
        public void APartialLocgSpanIsALowerBoundNotARange()
        {
            // LOCG truncates its table of contents to the subset the scrape saw, so "#1-12 from 3 edges" says
            // only that the edition collects SOMETHING between 1 and 12.
            var full = new SpanSelection.Candidate(EditionSource.Locg, 1, 12, 0.4, "12 contained", null);
            var partial = new SpanSelection.Candidate(EditionSource.Locg, 1, 12, 0.4, "3 contained", null);
            Assert.True(SpanSelection.LocgIsComplete(full));
            Assert.False(SpanSelection.LocgIsComplete(partial));
            Assert.Equal(3, SpanSelection.ContainedCount("3 contained"));
            Assert.Equal(1, SpanSelection.ContainedCount("contained: 1"));
            Assert.Null(SpanSelection.ContainedCount(null));
            // A partial LOCG (4) falls behind a GCD title match (3).
            var gcd = Gcd(1, 6, 0.9, ByTitle, "1");
            Assert.Equal(EditionSource.Gcd, SpanSelection.Select(new[] { partial, gcd }, true, 400)!.Value.Source);

            // A LOCG row reduced from ONE edge is a shell page: on a book-sized item the claim is discarded.
            var shell = new SpanSelection.Candidate(EditionSource.Locg, 7, 7, 0.4, "contained: 1", null);
            Assert.Equal(4, SpanSelection.Rank(shell));
            Assert.Equal(EditionSource.Cv, SpanSelection.Select(
                new[] { shell, new SpanSelection.Candidate(EditionSource.Cv, 1, 18, 1.0, null, null) }, true, 400)!.Value.Source);
        }

        [Fact]
        public void ADegenerateSpanSurvivesOnAThinNonCollection()
        {
            // A floppy legitimately "collects" itself; only a collection-shaped item discards the claim.
            var c = Gcd(7, 7, 0.9, ByTitle, "Some Title");
            Assert.False(SpanSelection.IsDiscardable(c, isCollection: false, pageCount: 32));
            Assert.True(SpanSelection.IsDiscardable(c, isCollection: true, pageCount: 32));
            Assert.True(SpanSelection.IsDiscardable(c, isCollection: false, pageCount: 300));
        }

        [Fact]
        public void EveryClaimDiscardedMeansNoSpanAtAll()
        {
            var only = new[] { Gcd(44, 44, 0.8, ByNum, "3") };
            Assert.Null(SpanSelection.Select(only, isCollection: true, pageCount: 1226));
        }

        // ── the reading order's use of the winner ────────────────────────────────────────────────────────

        private static V1Fixture Migrated()
        {
            var f = new V1Fixture();
            var summary = f.Engine(f.Options()).Run();
            if (summary.Stopped) throw new InvalidOperationException("fixture migration stopped: " + summary.StopReason);
            return f;
        }

        [Fact]
        public void ARowWithASpanTakesTheContainmentPositionWhateverItsParsedTier()
        {
            // "Batman Book 2" as the re-parse leaves it: the volume number has moved out of `IssueNo`, so the
            // row has no number of its own — but its FORMAT is still the ComicInfo's SingleIssue, which puts it
            // on the MAIN tier, not the collection tier. The span says it collects #404-406 and that, not the
            // tier, decides where it reads.
            using var f = Migrated();
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                hot.Begin();
                foreach (var (id, no) in new[] { (600, "404"), (601, "405"), (602, "406"), (603, "407") })
                {
                    hot.Upsert("Item", new { Id = id, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = $@"\\x\b{no}.cbz", FileName = $"b{no}.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 32 });
                    hot.Upsert("ComicDetail", new { ItemId = id, ParsedSeriesKey = "Batman", IssueNo = no, Year = 1987, Format = ComicFormat.SingleIssue, IsCollection = false });
                }
                hot.Upsert("Item", new { Id = 610, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = @"\\x\Batman Book 2.cbz", FileName = "Batman Book 2.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 500 });
                hot.Upsert("ComicDetail", new { ItemId = 610, ParsedSeriesKey = "Batman", IssueNo = (string?)null, VolumeNo = 2, Year = 1988, Format = ComicFormat.SingleIssue, IsCollection = true });
                hot.Upsert("CollectedEditionSpan", new { ItemId = 610, Source = EditionSource.Cv, SeriesId = 2, IssueStart = 404.0, IssueEnd = 406.0, Confidence = 1.0 });
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            Assert.Equal((long)ReadingOrderSource.Containment, w.Scalar<long>("SELECT Source FROM ReadingOrderEntry WHERE ItemId = 610"));
            Assert.Equal(ReadingOrderParser.TierMain, (int)w.Scalar<long>("SELECT ReadTier FROM ReadingOrderEntry WHERE ItemId = 610"));
            Assert.Equal(404, w.Scalar<long>("SELECT CAST(ReadNumber AS INTEGER) FROM ReadingOrderEntry WHERE ItemId = 610"));
            Assert.True(w.Scalar<double>("SELECT ReadNumberSuffix FROM ReadingOrderEntry WHERE ItemId = 610") < 0);
            // It reads BEFORE #404 and after #403 — not at position "#2" in the middle of the run.
            Assert.True(w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 610")
                      < w.Scalar<long>("SELECT ReadIndex FROM ReadingOrderEntry WHERE ItemId = 600"),
                "the collected edition must read before the first issue it collects");
        }

        [Fact]
        public void AnIssueWithItsOwnNumberIsNeverRelocatedByASpan()
        {
            // The collateral this guard exists for: a shell-page span claiming a whole run dragged 1,647
            // genuine 32-page issues (2000 AD progs among them) to the front of their series. A row that holds
            // a number of its own outside the collection tier keeps its place.
            using var f = Migrated();
            using (var hot = new TargetWriter(f.HotPath, MappingContract.Load(), dryRun: false))
            {
                hot.Begin();
                foreach (var (id, no) in new[] { (620, "404"), (621, "405"), (622, "406") })
                {
                    hot.Upsert("Item", new { Id = id, RootId = 1, FolderId = 5, Kind = ItemKind.Comic, Path = $@"\\x\c{no}.cbz", FileName = $"c{no}.cbz", Extension = ".cbz", FileSize = 1, SeriesId = 2, PageCount = 32 });
                    hot.Upsert("ComicDetail", new { ItemId = id, ParsedSeriesKey = "Batman", IssueNo = no, Year = 1987, Format = ComicFormat.SingleIssue, IsCollection = false });
                }
                // A degenerate claim about #405, and a whole-run claim about #406 — neither may move its row.
                hot.Upsert("CollectedEditionSpan", new { ItemId = 621, Source = EditionSource.Gcd, SeriesId = 2, IssueStart = 405.0, IssueEnd = 405.0, Confidence = 0.9 });
                hot.Upsert("CollectedEditionSpan", new { ItemId = 622, Source = EditionSource.Locg, SeriesId = 2, IssueStart = 1.0, IssueEnd = 700.0, Confidence = 0.4, Note = "contained: 700" });
                hot.Commit();
                ReadingOrderJob.RunAll(hot, 50, _ => { });
            }

            using var w = f.Hot();
            foreach (var id in new[] { 621, 622 })
            {
                Assert.NotEqual((long)ReadingOrderSource.Containment, w.Scalar<long>($"SELECT Source FROM ReadingOrderEntry WHERE ItemId = {id}"));
                Assert.Equal(0d, w.Scalar<double>($"SELECT ReadNumberSuffix FROM ReadingOrderEntry WHERE ItemId = {id}"));
            }
            Assert.Equal(406, w.Scalar<long>("SELECT CAST(ReadNumber AS INTEGER) FROM ReadingOrderEntry WHERE ItemId = 622"));
        }
    }
}
