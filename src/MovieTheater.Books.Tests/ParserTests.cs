using MovieTheater.Books.Db;
using MovieTheater.Books.Parse;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The parse pipeline, exercised as the pure function it is. Every case here is one the library actually
    /// contains — the "2000 AD" number trap, the mini-series "(of N)" convention, the scanner-tag bracket, the
    /// ComicInfo &lt;Volume&gt; that is really a year.
    /// </summary>
    public class ParserTests
    {
        private static readonly string[] Roots = { @"\\share\comics", @"\\share\books" };

        private static ComicTitleParser.Parsed Parse(string relative, ComicTitleParser.Embedded? meta = null)
        {
            var path = @"\\share\comics\" + relative.Replace('/', '\\');
            return ComicTitleParser.Parse(System.IO.Path.GetFileName(path), path, meta, Roots);
        }

        [Theory]
        // The title contains a bare 2000; the LEADING ZEROS on 0001 are what say "this is the index".
        [InlineData("2000 AD 0001 (1977)", "1")]
        [InlineData("2000 AD 0370 (1984)", "370")]
        // The mini-series convention puts the issue OUTSIDE the parens; the total is not the issue.
        [InlineData("Doppelganger 01 (of 04) (2017)", "1")]
        [InlineData("Battle Action 00 (of 05) (2023)", "0")]
        // An explicit #N wins, rightmost.
        [InlineData("Batman #015", "15")]
        [InlineData("Detective Comics #027", "27")]
        // A scanner tag bracket is a position anchor, not noise to fall past.
        [InlineData("2000 AD 0087 [ScAndy]", "87")]
        // A Vol./Issue label is an issue number when nothing else is.
        [InlineData("Some Series Vol. 5", "5")]
        [InlineData("Some Series Issue 3", "3")]
        public void TheIssueLadderPicksTheRightNumber(string stem, string expected) =>
            Assert.Equal(expected, ComicTitleParser.ExtractIssueNo(stem));

        /// <summary>
        /// An ISO date's month and day look exactly like bare issue numbers to the right-to-left fallback, so the
        /// date is stripped before the scan. Here the strip also takes the number with it and the answer is
        /// NOTHING — which is the correct outcome: a wrong number would be worse than no number.
        /// </summary>
        [Fact]
        public void AnIsoDateNeverBecomesTheIssueNumber() =>
            Assert.Null(ComicTitleParser.ExtractIssueNo("Crisis_002_Fleetway_1988-10-01_Slinky_J_"));

        [Theory]
        [InlineData("Batman #015 - The Trial", "Batman")]
        [InlineData("Zot! #01 - Zot!", "Zot!")]
        [InlineData("Watchmen - The Deluxe Edition", "Watchmen")]
        // The qualifier word is REQUIRED for keywords that live inside real series names.
        [InlineData("The Acme Novelty Library", "The Acme Novelty Library")]
        [InlineData("Ultimate Spider-Man 001", "Ultimate Spider-Man")]
        // Omnibus strips bare, which folds "<Series> Omnibus Book NN" back onto the series.
        [InlineData("B.P.R.D. Omnibus Book 01", "B.P.R.D.")]
        [InlineData("Domestic Girlfriend v21", "Domestic Girlfriend")]
        [InlineData("Thor by J. Michael Straczynski", "Thor")]
        public void CleanTitleStripsNoiseWithoutEatingTheName(string stem, string expected) =>
            Assert.Equal(expected, ComicTitleParser.CleanTitle(stem));

        [Fact]
        public void EmbeddedMetadataOutranksTheFilename()
        {
            var parsed = Parse(@"Marvel\Daredevil (1964)\042 - Red Birds.cbz",
                new ComicTitleParser.Embedded(Series: "Daredevil", Number: "42"));
            Assert.Equal("Daredevil", parsed.ParsedSeriesKey);
            Assert.Equal(ParseSource.Metadata, parsed.SeriesSource);
            Assert.Equal("42", parsed.IssueNo);
            Assert.Equal(Confidence.High, parsed.Confidence);
        }

        [Fact]
        public void ASortPrefixedFilenameYieldsAStoryTitleSoTheFolderWins()
        {
            // "042 - Red Birds" cleans to a STORY name, not a series name — the year-bearing folder is better.
            var parsed = Parse(@"Marvel\_Daredevil\01 Daredevil v1 (1964)\042 - Red Birds.cbz");
            Assert.Equal("Daredevil", parsed.ParsedSeriesKey);
            Assert.Equal(ParseSource.Folder, parsed.SeriesSource);
            Assert.Equal(1964, parsed.Year);
            Assert.Equal(ParseSource.Folder, parsed.YearSource);
            // The sort prefix is STRIPPED, not read as an issue number: "042" is a reading-order position in the
            // folder, and inventing an issue #42 from it would be a wrong answer rather than a missing one.
            Assert.Equal(ParseSource.None, parsed.IssueSource);
            Assert.Null(parsed.IssueNo);
            Assert.NotNull(parsed.ParseNotes);
        }

        [Fact]
        public void TheSeriesFolderIsTheFirstOneCarryingAYearNotTheGrouper()
        {
            var (raw, clean, year, _) = ComicTitleParser.BestSeriesComponent(new[] { "Marvel", "_Daredevil", "01 Daredevil v1 (1964)" });
            Assert.Equal("01 Daredevil v1 (1964)", raw);
            Assert.Equal("Daredevil", clean);
            Assert.Equal(1964, year);
        }

        [Fact]
        public void WithNoYearInAnyComponentTheFirstChildOfThePublisherIsTheSeries()
        {
            // "(1987-1990)" is a RANGE, not a (YYYY) — no component matches, so component[1] is used.
            var (_, clean, year, _) = ComicTitleParser.BestSeriesComponent(new[] { "DC", "#DC Events", "001 Emerald Dawn(1987-1990)" });
            Assert.Equal("DC Events", clean);
            Assert.Null(year);
        }

        [Fact]
        public void AVolumeInTheYearBandIsAYearSignalNotARunNumber()
        {
            var parsed = Parse(@"Image\Saga (2012)\Saga 01.cbz", new ComicTitleParser.Embedded(Series: "Saga", Number: "1", Volume: 2012));
            Assert.Null(parsed.VolumeNo);
            Assert.Equal(2012, parsed.Year);
            Assert.Contains("looks like a year", parsed.ParseNotes);
        }

        [Fact]
        public void AStrayComicVineVolumeTagNeverReachesTheSeriesKey()
        {
            var parsed = Parse(@"DC\Batman (1940)\Batman 404.cbz", new ComicTitleParser.Embedded(Series: "Batman cvv161843"));
            Assert.Equal("Batman", parsed.ParsedSeriesKey);
        }

        [Theory]
        [InlineData("none")]
        [InlineData("N/A")]
        [InlineData("-")]
        [InlineData("  ")]
        [InlineData(null)]
        public void GarbageIssueNumbersAreRejected(string? value) => Assert.True(ComicTitleParser.IsGarbageIssueNumber(value));

        [Fact]
        public void AGarbageMetadataNumberFallsThroughToTheFilename()
        {
            var parsed = Parse(@"DC\Batman (1940)\Batman 404.cbz", new ComicTitleParser.Embedded(Series: "Batman", Number: "none"));
            Assert.Equal("404", parsed.IssueNo);
            Assert.Equal(ParseSource.Filename, parsed.IssueSource);
        }

        [Theory]
        [InlineData("Saga Omnibus", ComicFormat.Omnibus, true)]
        [InlineData("Watchmen HC", ComicFormat.Hardcover, true)]
        [InlineData("Something TPB", ComicFormat.Tpb, true)]
        [InlineData("Batman Annual 3", ComicFormat.Annual, false)]
        [InlineData("Batman 404", ComicFormat.SingleIssue, false)]
        // A "Vol N" with no explicit #N is a collected volume, not an issue.
        [InlineData("Sandman Vol 4", ComicFormat.Tpb, true)]
        public void FormatDetectionReadsTheFilenameWhenMetadataIsSilent(string stem, ComicFormat format, bool isCollection)
        {
            var (f, raw, collection) = ComicTitleParser.DetectFormat(stem, null);
            Assert.Equal(format, f);
            Assert.Null(raw);
            Assert.Equal(isCollection, collection);
        }

        [Fact]
        public void AnUnknownFormatSpellingKeepsItsRawText()
        {
            var (f, raw, _) = ComicTitleParser.DetectFormat("whatever", "Prestige Format");
            Assert.Equal(ComicFormat.Unknown, f);
            Assert.Equal("Prestige Format", raw);
        }

        [Fact]
        public void TheBookIsCategorizedByItsRootNotItsName() =>
            Assert.Equal(ContainerFormat.Epub, Services.LibraryScanner.ContainerFor(".epub"));

        // ── reading-order parsing ────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1977-02-26", "1977-02-26", DatePrecision.Day)]
        [InlineData("1987-03", "1987-03-15", DatePrecision.Month)]      // month anchors mid-month
        [InlineData("2020", "2020-07-01", DatePrecision.Year)]          // year anchors mid-year
        [InlineData("who knows", null, DatePrecision.None)]
        public void DatesAreNormalizedAndAnchoredByPrecision(string raw, string? iso, DatePrecision precision)
        {
            var d = ReadingOrderParser.NormalizeDate(raw);
            Assert.Equal(iso, d.Iso);
            Assert.Equal(precision, d.Precision);
        }

        [Fact]
        public void AProgCoverDateBecomesAnIsoDay() =>
            Assert.Equal("2021-12-08", ReadingOrderParser.NormalizeProgDate("8th December, 2021"));

        [Theory]
        [InlineData("-1", -1d)]
        [InlineData("0", 0d)]
        [InlineData("½", 0.5d)]
        // "1/2" carries a leading base number, so it is issue 1½ — half-issues sort AFTER the issue they follow.
        [InlineData("1/2", 1.5d)]
        [InlineData("12", 12d)]
        [InlineData("none", null)]
        public void IssueNumbersParseIntoTheirOrderingValue(string raw, double? expected)
        {
            var order = ReadingOrderParser.ParseIssue(raw, ComicFormat.SingleIssue, null);
            Assert.Equal(expected, order.Number);
        }

        [Fact]
        public void AnAnnualKeywordOverridesAMainTierFormat()
        {
            var order = ReadingOrderParser.ParseIssue("2", ComicFormat.SingleIssue, "Batman Annual 02.cbz");
            Assert.Equal(ReadingOrderParser.TierAnnual, order.Tier);
        }

        [Fact]
        public void CollectedFormatsSortAfterTheMainLine()
        {
            Assert.Equal(ReadingOrderParser.TierCollection, ReadingOrderParser.TierFromFormat(ComicFormat.Omnibus));
            Assert.Equal(ReadingOrderParser.TierMain, ReadingOrderParser.TierFromFormat(ComicFormat.SingleIssue));
            Assert.True(ReadingOrderParser.TierMain < ReadingOrderParser.TierAnnual);
            Assert.True(ReadingOrderParser.TierAnnual < ReadingOrderParser.TierSpecial);
            Assert.True(ReadingOrderParser.TierSpecial < ReadingOrderParser.TierCollection);
        }

        [Theory]
        // Keyword first: a name or format tag beats raw size.
        [InlineData(ComicFormat.Omnibus, "Saga Omnibus.cbz", 200, CollectionLevel.Omnibus)]
        [InlineData(ComicFormat.Hardcover, "Watchmen.cbz", 50, CollectionLevel.Book)]
        [InlineData(ComicFormat.Tpb, "Saga v01.cbz", 50, CollectionLevel.Volume)]
        // An explicitly issue-grade format stays level 0 whatever its size.
        [InlineData(ComicFormat.Annual, "Batman Annual 3.cbz", 96, CollectionLevel.Issue)]
        // Page count is the fallback for the DEFAULT "single issue".
        [InlineData(ComicFormat.SingleIssue, "Mystery.cbz", 700, CollectionLevel.Omnibus)]
        [InlineData(ComicFormat.SingleIssue, "Mystery.cbz", 350, CollectionLevel.Book)]
        [InlineData(ComicFormat.SingleIssue, "Mystery.cbz", 150, CollectionLevel.Volume)]
        [InlineData(ComicFormat.SingleIssue, "Mystery.cbz", 32, CollectionLevel.Issue)]
        public void CollectionLevelIsKeywordFirstAndSizeSecond(ComicFormat format, string fileName, int pages, CollectionLevel expected) =>
            Assert.Equal(expected, CollectionLevels.Resolve(format, null, fileName, pages));
    }
}
