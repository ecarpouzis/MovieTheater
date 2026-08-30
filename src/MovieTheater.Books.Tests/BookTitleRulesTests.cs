using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Tests
{
    /// <summary>
    /// The title rules, and — more to the point — the titles they must LEAVE ALONE. Every book in
    /// the library took its title from a file name, so the cleaning rules run over all 126,389 of
    /// them; a rule that overreaches damages far more than it repairs. The refusal cases here are
    /// the ones an earlier pass got wrong.
    /// </summary>
    public class BookTitleRulesTests
    {
        private static string? Clean(string t, params string[] authors) =>
            BookTitleRules.Clean(t, authors).Title;

        [Theory]
        [InlineData("The Eyre Affair_ A Novel", "The Eyre Affair: A Novel")]   // sanitised colon
        [InlineData("Ten Big Ones .Html", "Ten Big Ones")]                     // surviving extension
        [InlineData("Unknown (epub)", "Unknown")]                              // format tag
        [InlineData("SS Corpse Vision (v5.0)", "SS Corpse Vision")]            // release version
        [InlineData("05 - Warrior Priest", "Warrior Priest")]                  // leading series index
        [InlineData("Microsoft Word - Doctor Who", "Doctor Who")]              // Word's export prefix
        public void StripsTheArtefactsOfAFileName(string input, string expected) =>
            Assert.Equal(expected, Clean(input));

        [Theory]
        [InlineData("1984")]                          // a title that IS a number
        [InlineData("57 Chevy")]
        [InlineData("802.11 Wireless Networks")]
        [InlineData("101 Things to do with Ramen Noodles")]
        public void KeepsATitleThatMerelyBeginsWithDigits(string input) =>
            Assert.Equal(input, Clean(input));

        [Fact]
        public void StripsTheAuthorRepeatedAsASuffix() =>
            Assert.Equal("Wintersmith", Clean("Wintersmith - Terry Pratchett", "Terry Pratchett"));

        [Fact]
        public void MatchesTheAuthorRegardlessOfHowTheNameIsOrdered() =>
            Assert.Equal("Wintersmith", Clean("Wintersmith - Terry Pratchett", "Pratchett, Terry"));

        [Fact]
        public void StripsTheAuthorRepeatedAsAPrefix() =>
            Assert.Equal("An Affair To Forget",
                Clean("Rachel Lindsay - An Affair To Forget", "Rachel Lindsay"));

        [Fact]
        public void StripsAPrefixWrittenSurnameFirst() =>
            Assert.Equal("Scales and a Tail",
                Clean("Glenn, Stormy - Scales and a Tail", "Stormy Glenn"));

        [Fact]
        public void KeepsTheRestOfTheTitleWhenThePrefixIsStripped() =>
            Assert.Equal("Rusalka 2 - Chernevog",
                Clean("CJ Cherryh - Rusalka 2 - Chernevog", "CJ Cherryh"));

        [Fact]
        public void KeepsALeadingClauseThatIsNotThisBooksAuthor() =>
            Assert.Equal("Herbert West - Reanimator",
                Clean("Herbert West - Reanimator", "H.P. Lovecraft"));

        [Fact]
        public void KeepsATrailingClauseThatIsNotThisBooksAuthor() =>
            Assert.Equal("Dune - Messiah", Clean("Dune - Messiah", "Frank Herbert"));

        [Fact]
        public void WillNotLeaveTheAuthorStandingAsTheTitle()
        {
            // Stripping here would title the book with its own author's name.
            const string t = "Terry Pratchett - Terry Pratchett";
            Assert.Equal(t, Clean(t, "Terry Pratchett"));
        }

        [Theory]
        [InlineData("(epub)")]   // nothing but a format tag
        [InlineData("05 - ")]    // nothing but an index
        [InlineData(".epub")]    // nothing but an extension
        public void NeverBlanksATitle(string input) =>
            Assert.False(string.IsNullOrWhiteSpace(Clean(input)));

        [Fact]
        public void KeepsATitleWholeWhenTheRulesWouldConsumeAllOfIt() =>
            Assert.Equal("(epub)", Clean("(epub)"));

        [Fact]
        public void LiftsTheAuthorWhenTheTitleIsTheOnlyPlaceItWasRecorded()
        {
            // The 865 items Calibre never matched: nothing supplied an author, so the filename's
            // trailing clause is the only record of one and is kept rather than discarded.
            var (title, lifted) = BookTitleRules.Clean("Sunshine - Robin McKinley", Array.Empty<string>());
            Assert.Equal("Sunshine", title);
            Assert.Equal("Robin McKinley", lifted);
        }

        [Theory]
        // An initial is not the article "a" — the check that missed these read 'A.' as a stop word.
        [InlineData("The Crystal Shard - R. A. Salvatore", "The Crystal Shard", "R. A. Salvatore")]
        [InlineData("Leviathan Wakes - James S. A. Corey", "Leviathan Wakes", "James S. A. Corey")]
        // A surname's particle may be lowercase.
        [InlineData("Birds Without Wings - Louis de Bernieres", "Birds Without Wings", "Louis de Bernieres")]
        [InlineData("The Dispossessed - Ursula K. Le Guin", "The Dispossessed", "Ursula K. Le Guin")]
        public void LiftsANameWrittenWithInitialsOrAParticle(string input, string title, string author)
        {
            var (t, lifted) = BookTitleRules.Clean(input, Array.Empty<string>());
            Assert.Equal(title, t);
            Assert.Equal(author, lifted);
        }

        [Theory]
        [InlineData("Journey to the Center of the Earth")]  // "the" is still a stop word
        [InlineData("A Tale of Two Cities")]
        public void StillRejectsAnOrdinaryPhraseAsAName(string phrase)
        {
            var (_, lifted) = BookTitleRules.Clean("Something - " + phrase, Array.Empty<string>());
            Assert.Null(lifted);
        }

        [Fact]
        public void DoesNotLiftATrailingClauseThatIsNotAName()
        {
            var (title, lifted) = BookTitleRules.Clean("The Hobbit - Part 2", Array.Empty<string>());
            Assert.Equal("The Hobbit - Part 2", title);
            Assert.Null(lifted);
        }

        [Fact]
        public void DoesNotLiftAnAuthorWhenTheBookAlreadyHasOne()
        {
            var (_, lifted) = BookTitleRules.Clean("Sunshine - Robin McKinley", new[] { "Frank Herbert" });
            Assert.Null(lifted);
        }

        [Fact]
        public void LeavesAnAlreadyCleanTitleUntouched() =>
            Assert.Equal("The Lies of Locke Lamora", Clean("The Lies of Locke Lamora", "Scott Lynch"));
    }
}
