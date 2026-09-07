using MovieTheater.Books.Db;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The containment repair: a collected edition may be matched only to a provider CONTAINER record, and only
    /// on the edition's TITLE. Every fixture here is a row the library actually holds — "Saga Book 1" (505
    /// pages, filename `Saga 01-018`), whose whole fault was being matched to ComicVine's and GCD's ISSUE #1.
    /// </summary>
    public class ContainmentRepairTests
    {
        // The shape ComicVine really publishes: an H4 heading, a bold sub-heading per edition line, and a <ul>
        // of "<a>title</a> (#start-end)" entries.
        private const string SagaDescription = """
            <p>Saga is an ongoing series from Brian K. Vaughan and Fiona Staples.</p>
            <h4>Collected Editions</h4><p><strong>Trade Paperbacks</strong></p>
            <ul><li><a href="/saga-tpb/">Volume 1</a> (#1-6)</li>
            <li><a href="/saga-compendium/">Saga Compendium</a> (#1-54)</li>
            <li><a href="/saga-7/">Volume 7</a> (#37-42)</li>
            <li><a href="/saga-8/">Volume 8</a> (#43-48)</li></ul>
            <p><strong>Deluxe Edition Hardcovers</strong></p>
            <ul><li><a href="/saga-deluxe-1/">Saga Deluxe Edition Book One</a> (#1-18)</li>
            <li><a href="/saga-deluxe-2/">Saga Deluxe Edition Book Two</a> (#19-36)</li></ul>
            <p><strong>Publishing History</strong></p><p>Ignore me (#99-100)</p>
            """;

        private static CollectionBook Book(string fileName, int pages, int? volumeNo = null, string? issueNo = null,
            CollectionLevel level = CollectionLevel.Volume) =>
            new() { ItemId = 1, SeriesId = 14966, FileName = fileName, PageCount = pages, VolumeNo = volumeNo, IssueNo = issueNo, Level = level };

        // ── the ComicVine prose ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheCollectedEditionsBlockIsParsedAndTheSectionEnds()
        {
            var editions = CvEditionParser.ParseEditions(SagaDescription);
            Assert.Contains(editions, e => e.Title == "Volume 1" && e.Start == 1 && e.End == 6);
            Assert.Contains(editions, e => e.Title == "Saga Deluxe Edition Book One" && e.Start == 1 && e.End == 18);
            // "Publishing History" ends the section — what follows is not an edition.
            Assert.DoesNotContain(editions, e => e.Start == 99);
            Assert.Empty(CvEditionParser.ParseEditions("<p>No such section here.</p>"));
            Assert.True(CvEditionParser.HasCollectedSection(SagaDescription));
        }

        [Fact]
        public void TheSectionHeadingDoesNotLeakIntoTheFirstEditionTitle()
        {
            // Stripping the markup runs the heading straight into the first entry: "Collected Editions Deep
            // State: Dark Side of the Moon (#1-4)".
            var editions = CvEditionParser.ParseEditions(
                "<h4>Collected Editions</h4> Deep State: Dark Side of the Moon (#1-4)");
            Assert.Equal("Deep State: Dark Side of the Moon", editions[0].Title);
        }

        [Fact]
        public void SagaBookOneTakesTheDeluxeEditionRangeNotIssueOne()
        {
            var editions = CvEditionParser.ParseEditions(SagaDescription);
            var seriesNorm = EditionMatcher.Norm("Saga");

            var book1 = Book("Saga Book 1 (2014) (Saga 01-018) (digital-Empire).cbr", 505, volumeNo: 1, level: CollectionLevel.Book);
            var m1 = EditionMatcher.MatchRanged(book1, editions, seriesNorm);
            Assert.NotNull(m1);
            Assert.Equal(1, m1!.Value.Edition.Start);
            Assert.Equal(18, m1.Value.Edition.End);

            var vol7 = Book("Saga Vol. 07 (2017) (Digital) (Zone-Empire).cbr", 152, volumeNo: 7);
            var m7 = EditionMatcher.MatchRanged(vol7, editions, seriesNorm);
            Assert.Equal(37, m7!.Value.Edition.Start);
            Assert.Equal(42, m7.Value.Edition.End);
        }

        [Fact]
        public void ASingleIssueEntryIsNeverTheAnswerForACollection()
        {
            var editions = new List<EditionCandidate> { new("Saga", 1, 1, null) };
            Assert.Null(EditionMatcher.MatchRanged(Book("Saga Book 1.cbr", 505, 1, level: CollectionLevel.Book),
                editions, EditionMatcher.Norm("Saga")));
        }

        [Fact]
        public void AThinFileLabelledTpbIsNotCollectionShaped()
        {
            // "G.I. Joe A Real American Hero v2 043" is 48 pages and carries a v1 `FormatRaw` of "TPB" with
            // IsCollection = 1. Matching it to an edition would re-create the fault in a new place.
            Assert.False(EditionMatcher.IsCollectionShaped(CollectionLevel.Volume, isCollection: true, pageCount: 48));
            Assert.True(EditionMatcher.IsCollectionShaped(CollectionLevel.Volume, isCollection: true, pageCount: 129));
            Assert.False(EditionMatcher.IsCollectionShaped(CollectionLevel.Issue, isCollection: false, pageCount: 500));
        }

        [Fact]
        public void TheCollectionOrdinalComesFromTheVolumeThenTheFilenameThenTheIssueNumber()
        {
            Assert.Equal(3, EditionMatcher.CollectionNumber(Book("whatever.cbz", 200, volumeNo: 3)));
            Assert.Equal(2, EditionMatcher.CollectionNumber(Book("Saga Book 2 (2017).cbr", 200)));
            Assert.Equal(9, EditionMatcher.CollectionNumber(Book("Fables Compendium.cbr", 200, issueNo: "9")));
            Assert.Null(EditionMatcher.CollectionNumber(Book("Fables Compendium.cbr", 200)));
        }

        // ── the page-count audit ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ThePageAuditFlagsButNeverDrops()
        {
            Assert.Equal("thin", PageArithmetic.Flag(120, 1, 54));      // 2.2 pages per issue
            Assert.Equal("thick", PageArithmetic.Flag(1226, 44, 44));   // 1,226 pages for one issue
            Assert.Null(PageArithmetic.Flag(505, 1, 18));               // 28 pages per issue — a real deluxe
            Assert.Null(PageArithmetic.Flag(0, 1, 6));                  // no page count, no opinion
            Assert.Equal(28.0, PageArithmetic.PagesPerIssue(504, 1, 18));
        }

        // ── the GCD reprint graph ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheGcdSeriesKeyFoldsEditionWordsSoAFranchiseStaysTogether()
        {
            Assert.Equal("fables", GcdSpanJob.NormalizeSeries("Fables"));
            Assert.Equal("fables", GcdSpanJob.NormalizeSeries("Fables Deluxe Edition"));
            // The ORDINAL survives — "Fables Vol. 2" and "Fables" are not the same line — only the edition
            // WORDS are folded away.
            Assert.Equal("saga 1", GcdSpanJob.NormalizeSeries("Saga (Volume 1)"));
        }

        [Fact]
        public void TheEditionClassKeepsAPlainVolumeOffTheDeluxeLine()
        {
            Assert.Equal("tpb", GcdSpanJob.EditionClass("Fables Vol. 09 (2008).cbr"));
            Assert.Equal("deluxe", GcdSpanJob.EditionClass("Fables Deluxe Edition Book Three.cbr"));
            Assert.Equal("omnibus", GcdSpanJob.EditionClass("Wolverine Omnibus Vol. 3 HC.cbr"));
            Assert.Equal("compendium", GcdSpanJob.EditionClass("The Walking Dead Compendium One.cbr"));
            Assert.Equal("library", GcdSpanJob.EditionClass("Absolute Sandman Volume One.cbr"));
        }

        [Fact]
        public void TheReprintClusterIgnoresAStrayFarOffIssue()
        {
            // A collection reprinting #1-6 plus one guest appearance from #300 collects #1-6, not #1-300.
            var (start, end, count) = GcdSpanJob.Cluster([1, 2, 3, 4, 5, 6, 300]);
            Assert.Equal(1, start);
            Assert.Equal(6, end);
            Assert.Equal(6, count);
            Assert.Equal((0d, 0d, 0), GcdSpanJob.Cluster([]));
        }

        // ── the two rip importers ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheRipStoresTheResultsObjectItselfSoDescriptionIsTopLevel()
        {
            Assert.Equal("hello", RipImporters.DescriptionOf("""{"id":46568,"description":"hello"}"""));
            // …and a payload that DOES wrap it in `results` still reads.
            Assert.Equal("hello", RipImporters.DescriptionOf("""{"results":{"description":"hello"}}"""));
            Assert.Null(RipImporters.DescriptionOf("""{"id":1,"description":null}"""));
            Assert.Null(RipImporters.DescriptionOf("not json"));
        }

        [Fact]
        public void AReverseReprintFileNamesTheContainedComicAndListsItsContainers()
        {
            var edges = RipImporters.ParseReprintFile("1013261.json", """
                [{"locgId":"2854842","slug":"wolverine-omnibus-vol-3-hc","title":"Wolverine Omnibus Vol. 3 HC"},
                 {"locgId":"1073089","slug":"wolverine-blood-hungry-tp","title":"Wolverine: Blood Hungry TP"}]
                """);
            Assert.Equal(2, edges.Count);
            Assert.Equal(2854842, edges[0].ContainerId);
            Assert.Equal(1013261, edges[0].ContainedId);
            // A file whose name is not a comic id carries no edge, and neither does a self-reference.
            Assert.Empty(RipImporters.ParseReprintFile("resolve.json", "[]"));
            Assert.Empty(RipImporters.ParseReprintFile("77.json", """[{"locgId":"77"}]"""));
        }

        [Fact]
        public void TheCachedLocgSeriesPageYieldsTheEditionsAndTheirSlugs()
        {
            var editions = LocgEditionRelinkJob.ParseRich("""
                [{"id":"9412632","slug":"batman-year-one-dc-black-label-edition-tp","title":"Batman: Year One - DC Black Label Edition TP","isEdition":true},
                 {"id":"3182988","slug":"batman-year-one-hc-book-and-dvd-set","title":"","isEdition":true},
                 {"id":"7","slug":"not-an-edition","title":"Batman #7","isEdition":false}]
                """);
            Assert.Equal(2, editions.Count);
            Assert.Equal(9412632, editions[0].Id);
            // A bare entry falls back to its slug, which still carries the edition words.
            Assert.Equal("batman year one hc book and dvd set", editions[1].Title);
            Assert.Empty(LocgEditionRelinkJob.ParseRich("{}"));
        }

        [Fact]
        public void OneProviderRecordMayBeClaimedByOnlyOneBook()
        {
            // "20th_Century_Boys_v01..v11" carry no ordinal any matcher can read, so every volume scored
            // identically against "Perfect Edition Vol. 1". A tie is not evidence about any of them.
            var tied = new List<(int Item, long Edition, double Conf)>
            {
                (1, 500, 0.56), (2, 500, 0.56), (3, 500, 0.56), (4, 501, 0.9),
            };
            var kept = ProducerSupport.ResolveContention(tied, c => c.Edition, c => c.Conf);
            Assert.Single(kept);
            Assert.Equal(4, kept[0].Item);

            // A strict winner keeps its match and the weaker claimant is dropped.
            var contested = new List<(int Item, long Edition, double Conf)> { (1, 500, 1.0), (2, 500, 0.75) };
            var one = ProducerSupport.ResolveContention(contested, c => c.Edition, c => c.Conf);
            Assert.Single(one);
            Assert.Equal(1, one[0].Item);
        }

        [Fact]
        public void TheFilenameSubtitleIsTheStrongestTitleEvidence()
        {
            Assert.Equal("war and pieces", EditionMatcher.Subtitle("Fables Vol. 09 - War and Pieces (2008) (Digital).cbr"));
            Assert.Equal("", EditionMatcher.Subtitle("Saga Vol. 07 (2017) (Digital) (Zone-Empire).cbr"));

            // GCD titles a collected edition by its story arc, which shares nothing with the series name —
            // the cross-series guard that protects the ComicVine prose match must not apply here.
            var book = Book("Fables Vol. 09 - War and Pieces (2008) (Digital).cbr", 168, volumeNo: 9);
            var m = EditionMatcher.MatchTitleOnly(book, [(11L, "Sons of Empire"), (12L, "War and Pieces")],
                EditionMatcher.Norm("Fables"));
            Assert.NotNull(m);
            Assert.Equal(12L, m!.Value.Key);
            Assert.True(m.Value.Exact);
        }

        [Fact]
        public void ATitleOnlyMatchPicksTheEditionRecordNotTheIssueRecord()
        {
            var candidates = new List<(long Key, string Title)>
            {
                (1, "Saga #1"),
                (2, "Saga Deluxe Edition Book One HC"),
                (3, "Saga Compendium One TP"),
            };
            var book = Book("Saga Book 1 (2014) (Saga 01-018) (digital-Empire).cbr", 505, volumeNo: 1, level: CollectionLevel.Book);
            var m = EditionMatcher.MatchTitleOnly(book, candidates, EditionMatcher.Norm("Saga"));
            Assert.NotNull(m);
            Assert.Equal(2, m!.Value.Key);
        }
    }
}
