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

        private static ComicTitleParser.Parsed Parse(string relative, ComicTitleParser.Embedded? meta = null, int pageCount = 0)
        {
            var path = @"\\share\comics\" + relative.Replace('/', '\\');
            return ComicTitleParser.Parse(System.IO.Path.GetFileName(path), path, meta, Roots, pageCount);
        }

        /// <summary>The ComicInfo those files carry: "Single Issue", which is what made this whole class of
        /// misparse — 9,407 files in 2,693 series — invisible to the format detector.</summary>
        private static ComicTitleParser.Embedded SingleIssueTag(string? number = null) =>
            new(Series: null, Number: number, Format: "Single Issue");

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
        [Theory]
        // A padded library index ahead of the arc's own "(of N)" count is the series' number. Baltimore's
        // ladder tiles 1-40 with it and collides on five different #1s without it — and a range cannot be
        // judged while the numbering under it is wrong.
        [InlineData("Baltimore 016 - The Infernal Train 01 (of 03) (2013) (digital) (Son of Ultron-Empire)", "16")]
        [InlineData("Baltimore 006 - The Curse Bells 01 (of 05) (2011) (digital-Empire)", "6")]
        [InlineData("Sir Edward Grey, Witchfinder 021 - The Gates of Heaven 01 (of 05) (2018) (digital)", "21")]
        [InlineData("Lobster Johnson 024 - Metal Monsters of Midtown 01 (of 03) (2016) (digital)", "24")]
        [InlineData("Abe Sapien - 009 - The Devil Does Not Jest 01 (of 02) (2011) (digital) (G85NW-Empire)", "9")]
        [InlineData("X 013 - Better Off Dead 01 (of 04) (2014) (digital) (Son of Ultron-Empire)", "13")]
        [InlineData("2000 AD 0371 - Dredd - Super Bowl (2 of 2)", "371")]
        [InlineData("The Acme Novelty Library 11 - Jimmy Corrigan 05 (of 08)", "11")]
        public void APaddedLibraryIndexOutranksTheArcsOwnNumber(string stem, string expected) =>
            Assert.Equal(expected, ComicTitleParser.ExtractIssueNo(stem));

        [Theory]
        // The shape is the discriminator, and these are why it has to be. Each carries a number that belongs
        // to the title (or a date) ahead of an "(of N)" arc count, and each already parses correctly.
        [InlineData("Cyberpunk 2077 - Kickdown 01 (of 04) (2024) (digital) (Son of Ultron-Empire)", "1")]
        [InlineData("Fantastic Four vs. the X-Men, 1986-11-04 (#01) (of 04) (digital) (Glorith-HD)", "1")]
        [InlineData("Doppelganger 01 (of 04) (2017)", "1")]
        public void AnUnpaddedNumberInTheTitleIsNotALibraryIndex(string stem, string expected) =>
            Assert.Equal(expected, ComicTitleParser.ExtractIssueNo(stem));

        [Theory]
        // A #N inside brackets cites another publication. 2000AD prog 1002 also ran in the Judge Dredd
        // Megazine, and taking the citation put 535 progs on the Megazine's volume numbers.
        [InlineData("2000AD #1002b (JDMeg. #3.20-3.25) Judge Dredd (America II) - Fading of the Light", "1002")]
        [InlineData("2000AD #741b (JDMeg. #1.11-1.17) Judge Dredd - Raptaur (JDMeg. #297 - Reprint)", "741")]
        [InlineData("2000AD #1033 Judge Dredd - He Came from Outer Space", "1033")]
        public void ABracketedHashNumberIsACrossReferenceNotTheNumber(string stem, string expected) =>
            Assert.Equal(expected, ComicTitleParser.ExtractIssueNo(stem));

        [Fact]
        public void WhenEveryHashNumberIsBracketedTheLastOneStillAnswers()
        {
            // The fallback: a name with nothing outside brackets has only the citation to offer.
            Assert.Equal("7", ComicTitleParser.ExtractIssueNo("Some Special (#7)"));
        }

        [Theory]
        // 965 comics store the mini-series count inside the ComicInfo number, so IssueNo is a string that can
        // never sort, compare, or attach to a range.
        [InlineData("01 (of 04)", "1")]
        [InlineData("4 (of 4)", "4")]
        [InlineData("02 (OF 05)", "2")]
        [InlineData("5.1 (of 6)", "5.1")]
        public void TheMiniSeriesCountIsNotPartOfTheNumber(string raw, string expected) =>
            Assert.Equal(expected, ComicTitleParser.NormalizeMetaNumber(raw));

        [Theory]
        // The 54 that carry a real qualifier keep it: "Annual 4" is not issue 4.
        [InlineData("Annual 04")]
        [InlineData("Part 03")]
        [InlineData("18 (GL I Only)")]
        [InlineData("22 (Edit)")]
        [InlineData("7")]
        public void AQualifiedNumberIsLeftAlone(string raw) =>
            Assert.Equal(raw, ComicTitleParser.NormalizeMetaNumber(raw));

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

        // ── F1: a collected-edition label with no #N beats a "Single Issue" ComicInfo ────────────────────

        [Theory]
        // The seven Saga files. Three deluxe hardcovers (Book-level) and four TPBs (Volume-level), every one of
        // them tagged "Single Issue" and every one of them previously landing on the main line at #1..#10.
        [InlineData("Saga Book 1 (2014) (Saga 01-018) (digital-Empire).cbr", 505, ComicFormat.Hardcover, 1)]
        [InlineData("Saga Book 2 (2017) (Digital) (Zone-Empire).cbr", 462, ComicFormat.Hardcover, 2)]
        [InlineData("Saga Book 03 (2019) (Digital-Empire).cbz", 457, ComicFormat.Hardcover, 3)]
        [InlineData("Saga Vol. 07 (2017) (Digital) (Zone-Empire).cbr", 152, ComicFormat.Tpb, 7)]
        [InlineData("Saga Vol. 08 (2017) (Digital) (Zone-Empire).cbr", 153, ComicFormat.Tpb, 8)]
        [InlineData("Saga Vol. 09 (2018) (Digital) (Zone-Empire).cbr", 152, ComicFormat.Tpb, 9)]
        [InlineData("Saga Vol. 10 (2022) (Digital-Empire).cbz", 169, ComicFormat.Tpb, 10)]
        // Compendium and Omnibus are omnibus-grade whatever else the name says.
        [InlineData("The Walking Dead Compendium Book 04 (2019) (digital-Empire).cbz", 1204, ComicFormat.Omnibus, 4)]
        [InlineData("Spawn Compendium Book 01 (2021) (F) (Digital) (danke-Empire).cbz", 1118, ComicFormat.Omnibus, 1)]
        [InlineData("Wolverine Omnibus Book 03 (2023) (Digital) (Kileko-Empire).cbz", 1226, ComicFormat.Omnibus, 3)]
        // "Epic Collection Vol. NN" is a numbered trade line — Volume grade.
        [InlineData("Silver Surfer Epic Collection Vol. 14 - Sun Rise and Shadow Fall (2024) (Digital) (Shan-Empire).cbz", 474, ComicFormat.Tpb, 14)]
        // A deluxe edition enumerated as "Book NN" is Book grade.
        [InlineData("Ninjak - Deluxe Edition - Book 02 (2018) (digital) (Son of Ultron-Empire).cbr", 478, ComicFormat.Hardcover, 2)]
        public void ACollectedEditionLabelWithNoHashBeatsASingleIssueTag(string fileName, int pages, ComicFormat format, int volume)
        {
            var parsed = Parse(@"Image\Saga (2012)\" + fileName, SingleIssueTag(), pages);
            Assert.Equal(format, parsed.Format);
            Assert.True(parsed.IsCollection);
            Assert.Equal(volume, parsed.VolumeNo);
            // The volume number is NOT an issue number: leaving it interleaved the edition with the run.
            Assert.Null(parsed.IssueNo);
            Assert.Equal(ParseSource.None, parsed.IssueSource);
            // The ComicInfo spelling survives as provenance — FormatRaw says what the file claimed.
            Assert.Equal("Single Issue", parsed.FormatRaw);
            Assert.Contains("collected edition", parsed.ParseNotes);
        }

        [Theory]
        // Genuine floppies whose names carry a collection-shaped label. All three are in the library; all three
        // are under the page floor, which is the whole reason the floor exists.
        // "Vol. 2" here is the PRINTING RUN carried into every issue's name.
        [InlineData("Powers Vol. 2 01 (2004) (Digital) (ZoneKing-Empire).cbr", 22)]
        // "Book 2" is the mini-series' own name; "001" is the issue inside it.
        [InlineData("Zorro - Legendary Adventures Book 2 001 (2019) (digital) (Son of Ultron-Empire).cbr", 22)]
        // "The Comic Book 01 (of 5)" — "Book" is part of the TITLE and the number is the issue.
        [InlineData("Comic Book Guy - The Comic Book 01 (of 5) (2010) (4 covers) (digital) (Minutemen-InnerDemons).cbr", 28)]
        public void AThinFileKeepsItsSingleIssueTagHoweverItIsLabelled(string fileName, int pages)
        {
            var parsed = Parse(@"Image\Whatever (2004)\" + fileName, SingleIssueTag("1"), pages);
            Assert.Equal(ComicFormat.SingleIssue, parsed.Format);
            Assert.False(parsed.IsCollection);
            Assert.NotNull(parsed.IssueNo);
        }

        [Fact]
        public void AnExplicitHashNumberMeansItIsAnIssueHoweverThickTheFile()
        {
            // "Vol. 2 #7" numbers an ISSUE inside a run — the '#' token is half the collected-edition signal.
            var parsed = Parse(@"DC\Hellblazer (1988)\Hellblazer Vol. 2 #7 (1988).cbz", SingleIssueTag(), 400);
            Assert.Equal(ComicFormat.SingleIssue, parsed.Format);
            Assert.False(parsed.IsCollection);
            Assert.Equal("7", parsed.IssueNo);
        }

        [Fact]
        public void AnUnknownPageCountCanNeverPromoteARow()
        {
            // 0 = "we do not know", the default for every caller that has no size to offer.
            var parsed = Parse(@"Image\Saga (2012)\Saga Vol. 07 (2017) (Digital) (Zone-Empire).cbr", SingleIssueTag());
            Assert.Equal(ComicFormat.SingleIssue, parsed.Format);
            Assert.False(parsed.IsCollection);
        }

        [Theory]
        [InlineData("Wolverine Omnibus Book 03 (2023).cbz", ComicFormat.Omnibus)]
        [InlineData("The Walking Dead Compendium Book 04 (2019).cbz", ComicFormat.Omnibus)]
        [InlineData("Ninjak - Deluxe Edition - Book 02 (2018).cbr", ComicFormat.Hardcover)]
        [InlineData("Astro City Metrobook Book 02 (2022).cbz", ComicFormat.Hardcover)]
        [InlineData("Saga Vol. 07 (2017).cbr", ComicFormat.Tpb)]
        [InlineData("Silver Surfer Epic Collection Vol. 14 (2024).cbz", ComicFormat.Tpb)]
        [InlineData("Deadpool - The Complete Collection Vol. 02 (2019).cbz", ComicFormat.Tpb)]
        public void TheCollectedFormatComesFromTheLabelKeyword(string stem, ComicFormat expected) =>
            Assert.Equal(expected, ComicTitleParser.CollectedFormatFor(stem));

        [Fact]
        public void TheCollectedFormatAgreesWithTheContainmentLevel()
        {
            // The two ladders must not disagree, or an omnibus nests inside the TPB it contains.
            Assert.Equal(CollectionLevel.Omnibus, CollectionLevels.Resolve(ComicFormat.Omnibus, "Single Issue", "Wolverine Omnibus Book 03.cbz", 1226));
            Assert.Equal(CollectionLevel.Book, CollectionLevels.Resolve(ComicFormat.Hardcover, "Single Issue", "Saga Book 1.cbr", 505));
            Assert.Equal(CollectionLevel.Volume, CollectionLevels.Resolve(ComicFormat.Tpb, "Single Issue", "Saga Vol. 07.cbr", 152));
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
