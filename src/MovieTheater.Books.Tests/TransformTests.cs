using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>The pure value transforms and the ported resolver rules, pinned to the standalone site's behaviour.</summary>
    public class TransformTests
    {
        [Theory]
        [InlineData("The Umbrella Academy", "umbrella academy")]
        [InlineData("Umbrella  Academy!", "umbrella academy")]
        [InlineData("#SAD!", "sad")]
        [InlineData("Café", "café")] // no accent folding — deliberately
        public void NormalizeKeyMatchesTheStandaloneRule(string input, string expected) => Assert.Equal(expected, SeriesResolver.NormalizeKey(input));

        [Theory]
        [InlineData("2025-02-03 21:39:47", 2025, 2, 3, 21)]
        [InlineData("2026-05-27 05:54:30.6196618", 2026, 5, 27, 5)]
        [InlineData("2026-05-29T03:38:53.841755+00:00", 2026, 5, 29, 3)]
        [InlineData("2026-06-12T02:34:39.5199730Z", 2026, 6, 12, 2)]
        public void ParseDateAcceptsEveryV1Shape(string s, int y, int m, int d, int h)
        {
            var dt = Transforms.ParseDate(s)!.Value;
            Assert.Equal((y, m, d, h), (dt.Year, dt.Month, dt.Day, dt.Hour));
        }

        [Fact]
        public void ParseDateRejectsGarbageInsteadOfInventing() => Assert.Null(Transforms.ParseDate("not a date"));

        [Theory]
        [InlineData("Single Issue", ComicFormat.SingleIssue)]
        [InlineData("Trade Paper Back", ComicFormat.Tpb)]
        [InlineData("HC", ComicFormat.Hardcover)]
        [InlineData("Limed Series", ComicFormat.LimitedSeries)]
        [InlineData("80-Page Giant", ComicFormat.Special)]
        [InlineData("Edit", ComicFormat.Unknown)]
        [InlineData(null, ComicFormat.Unknown)]
        public void FormatSpellingsMap(string? raw, ComicFormat expected) => Assert.Equal(expected, Transforms.Format(raw));

        [Theory]
        [InlineData(0, LinkStatus.Pending)] [InlineData(1, LinkStatus.Matched)] [InlineData(2, LinkStatus.NoMatch)] [InlineData(3, LinkStatus.Multiple)]
        [InlineData(4, LinkStatus.Error)] [InlineData(5, LinkStatus.Skip)] [InlineData(6, LinkStatus.Manual)] [InlineData(null, LinkStatus.Pending)]
        public void ComicVineIntStatusesMap(int? v, LinkStatus expected) => Assert.Equal(expected, Transforms.LinkStatusOfCvInt(v));

        [Theory]
        [InlineData("matched", LinkStatus.Matched)] [InlineData("NoMatch", LinkStatus.NoMatch)] [InlineData("cleared-pageaudit", LinkStatus.Cleared)]
        [InlineData("Ambiguous", LinkStatus.Multiple)] [InlineData("Pending", LinkStatus.Pending)]
        public void TextStatusesMap(string s, LinkStatus expected) => Assert.Equal(expected, Transforms.LinkStatusOfText(s));

        [Theory]
        [InlineData("claude-opus-4-8", 3)] [InlineData("claude-sonnet-4-6", 2)] [InlineData("claude-haiku-4-5-20251001", 1)]
        [InlineData("file-metadata", 0)] [InlineData("openlibrary", 0)] [InlineData("calibre-tags", 0)] [InlineData("epub-jacket", 0)] [InlineData("gpt-9", 2)]
        public void ModelRankIsTheStandaloneTable(string model, int rank) => Assert.Equal(rank, Transforms.ModelRank(model));

        [Fact]
        public void SplitNamesHandlesCommaSemicolonAndAmpersand()
        {
            Assert.Equal(new[] { "Brian Herbert", "Kevin J. Anderson" }, Transforms.SplitNames("Brian Herbert & Kevin J. Anderson, Kevin J. Anderson"));
            Assert.Equal(new[] { "Pat Mills", "John Wagner" }, Transforms.SplitNames("Pat Mills; John Wagner"));
        }

        [Fact]
        public void TopScoreReadsBothCasings()
        {
            Assert.Equal(108, Transforms.TopScore("[{\"VolumeId\":4058,\"Score\":108},{\"VolumeId\":1,\"Score\":89}]"));
            Assert.Equal(90, Transforms.TopScore("[{\"provider\":\"openlibrary\",\"score\":90}]"));
            Assert.Null(Transforms.TopScore("not json"));
        }

        [Fact]
        public void CreatorsJsonParses()
        {
            var c = Transforms.ParseCreators("[{\"role\":\"Writer\",\"name\":\"Paul Jenkins\",\"peopleId\":\"248\"},{\"name\":\"\"}]");
            Assert.Single(c);
            Assert.Equal(("Writer", "Paul Jenkins", "248"), (c[0].Role, c[0].Name, c[0].PeopleId));
        }

        // ── synopsis gates ──

        [Fact]
        public void LocgSpecTailIsStrippedAndSpecOnlyEntriesFallThrough()
        {
            Assert.Equal("Judge Dredd arrives in Mega-City One in a story that launched a legend.",
                SynopsisRules.Prepare(SynopsisSource.Locg, "Judge Dredd arrives in Mega-City One in a story that launched a legend. Comic • 32 pages • $0.75 Cover Date Feb 1977"));
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.Locg, "Comic • 32 pages • $0.75"));
        }

        [Fact]
        public void CollectionBoilerplateAndMetaCruftAreRejectedForCvAndEmbedded()
        {
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.Cv, "Trade paperback collecting Fallen World."));
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.Embedded, "Collects nothing."));
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.Cv, "Issues #1-715. Continued in Batman (2011)."));
            Assert.NotEqual("", SynopsisRules.Prepare(SynopsisSource.Locg, "Collects issues 1-5 of the acclaimed run in a story about loss and redemption."));
        }

        [Fact]
        public void HtmlIsFlattenedAndMinimumLengthsHold()
        {
            Assert.Equal("Year One continues & Gordon arrives.", SynopsisRules.StripHtml("<p>Year One continues &amp; Gordon arrives.</p>"));
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.Embedded, "<p>Year One continues &amp; Gordon arrives.</p>")); // 36 chars < 40
            Assert.Equal("Short.", SynopsisRules.Prepare(SynopsisSource.AI, "Short."));
            Assert.Equal("", SynopsisRules.Prepare(SynopsisSource.CvDeck, "Tiny"));
        }

        [Fact]
        public void ItemSynopsisOrderIsCvInfoLocgExtMuDeckAi()
        {
            var long40 = new string('x', 45);
            Assert.Equal(SynopsisSource.Embedded, SynopsisRules.ResolveItem("Collects stuff.", long40, long40, null, null, null, "ai"));
            Assert.Equal(SynopsisSource.Locg, SynopsisRules.ResolveItem(null, null, long40 + " Comic • 32 pages", null, null, null, "ai"));
            Assert.Equal(SynopsisSource.CvDeck, SynopsisRules.ResolveItem(null, null, null, null, null, "A deck line", "ai"));
            Assert.Equal(SynopsisSource.AI, SynopsisRules.ResolveItem(null, null, null, null, null, "Tiny", "ai"));
            Assert.Equal(SynopsisSource.None, SynopsisRules.ResolveItem(null, null, null, null, null, null, null));
        }

        // ── title / date / aspect ──

        [Fact]
        public void TitleRuleMatchesTransformComic()
        {
            Assert.Equal("Doppelganger", ItemResolver.ResolveTitle("Doppelganger #4", "Doppelganger", isSingleIssueSeries: true, isCollection: false, 4, "4", null));
            Assert.Equal("Batman #405", ItemResolver.ResolveTitle("Batman 405 scan", "Batman", false, false, 405, "405", 1));
            Assert.Equal("Monsters Vol 2 #1", ItemResolver.ResolveTitle("x", "Monsters", false, false, 1, "1", 2));
            Assert.Equal("Batman #1.5", ItemResolver.ResolveTitle("x", "Batman", false, false, 1.5, null, null));
            Assert.Equal("FF Omnibus", ItemResolver.ResolveTitle("FF Omnibus", "Fantastic Four", false, isCollection: true, null, "1", null));
            Assert.Equal("Weird #none", ItemResolver.ResolveTitle("Weird #none", "Weird", false, false, null, "none", null));
        }

        [Fact]
        public void DateRuleMatchesResolveComicDate()
        {
            Assert.Equal((1977, 2, DatePrecision.Day), ItemResolver.ResolveDate("1977-02-26", DatePrecision.Day, "1977-03", null, null));
            Assert.Equal((1977, 3, DatePrecision.Month), ItemResolver.ResolveDate("1977-07-01", DatePrecision.Year, "1977-03", null, null)); // synthesized month ignored, real one taken
            Assert.Equal((1987, null, DatePrecision.Year), ItemResolver.ResolveDate(null, DatePrecision.None, "1987", null, null));
            Assert.Equal((2020, null, DatePrecision.Year), ItemResolver.ResolveDate(null, DatePrecision.None, null, 2020, null));
            Assert.Equal((2023, null, DatePrecision.Year), ItemResolver.ResolveDate(null, DatePrecision.None, null, null, 2023));
            Assert.Equal(((int?)null, (int?)null, DatePrecision.None), ItemResolver.ResolveDate(null, DatePrecision.None, null, null, null));
        }

        [Theory]
        [InlineData(1000, 1500, 0.6667)] [InlineData(3000, 1000, 1.6)] [InlineData(100, 1000, 0.35)] [InlineData(0, 0, 0.66)] [InlineData(null, 5, 0.66)]
        public void AspectIsClamped(int? w, int? h, double expected) => Assert.Equal(expected, ItemResolver.ClampAspect(w, h), 3);

        // ── folds ──

        [Fact]
        public void InsightTagCanonicalization()
        {
            var aliases = new Dictionary<(string, string), string> { [("genre", "science-fiction")] = "sci-fi" };
            Assert.Equal("Science Fiction", TagFolds.CanonicalizeInsightTag("genre", "science-fiction", aliases));
            Assert.Equal("Anthology", TagFolds.CanonicalizeInsightTag("character-focus", "anthology", aliases));
            Assert.Null(TagFolds.CanonicalizeInsightTag("character-focus", "Judge Dredd", aliases));
            Assert.Null(TagFolds.CanonicalizeInsightTag("genre", "batman", aliases));
            Assert.Equal("Space Western", TagFolds.CanonicalizeInsightTag("genre", "space-western", aliases));
        }

        [Fact]
        public void ProviderFoldsUseTheClosedMaps()
        {
            Assert.Equal(new[] { "Science Fiction", "Superhero" }, TagFolds.FoldSubjects("[\"Science fiction\",\"Superhero comics\",\"Comic books, strips, etc\"]"));
            Assert.Equal(new[] { "Action", "Anime Adaptation", "Post-Apocalyptic", "Science Fiction", "Seinen" }, TagFolds.FoldMu("[\"Action\",\"Sci-fi\",\"Seinen\"]", "[\"Post-Apocalyptic\",\"Adapted to Anime\"]"));
            Assert.Equal(new[] { "Science Fiction", "Superhero" }, TagFolds.FoldGcd("science fiction;superhero;advocacy"));
        }
    }
}
