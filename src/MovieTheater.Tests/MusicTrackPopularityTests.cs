using System.Collections.Generic;
using System.Linq;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The two pure rules behind track-level popularity (2026-08-31): the title fold that joins our
    /// catalogue to Last.fm's, and the parse of the answer it joins against.
    /// </summary>
    /// <remarks>
    /// These are the only two places the feature can be WRONG rather than merely absent. A parse bug
    /// loses a whole artist quietly; a fold bug is worse — it puts a real number on the wrong song,
    /// and nothing downstream can tell. Every example below is a real shape from the library or from
    /// Last.fm's own answers, not an invented one.
    /// </remarks>
    public class MusicTrackTitlesTests
    {
        [Theory]
        // Case, punctuation and spacing are all noise.
        [InlineData("Don't Look Back In Anger", "dont look back in anger")]
        [InlineData("don't look back in anger", "dont look back in anger")]
        [InlineData("Don’t Look Back in Anger", "dont look back in anger")]
        // The edition suffixes the two catalogues disagree about, in both spellings.
        [InlineData("Something (Remastered 2009)", "something")]
        [InlineData("Hurt - 2011 Remaster", "hurt")]
        [InlineData("Come As You Are [Remastered]", "come as you are")]
        [InlineData("Blue Monday (12\" Version)", "blue monday")]
        [InlineData("Layla (Acoustic)", "layla")]
        [InlineData("Bohemian Rhapsody - Live Aid", "bohemian rhapsody")]
        [InlineData("Idioteque (Live in Paris)", "idioteque")]
        // Two markers on one title: one pass would leave the second behind.
        [InlineData("Hurt (Live) [Remastered]", "hurt")]
        // Accents fold, so one song tagged two ways makes one key.
        [InlineData("Björk - Jóga", "bjork joga")]
        // "&" is spelled out rather than dropped, or two spellings of one name split apart.
        [InlineData("Simon & Garfunkel", "simon and garfunkel")]
        // The artist page's "Most popular" section dedupes on THIS key, so these four rows — the
        // studio cut, a singles-compilation copy with different punctuation, a remix and a live take
        // — have to fold into one song, or the Stones' top ten is three songs repeated.
        [InlineData("Paint It Black", "paint it black")]
        [InlineData("Paint It, Black", "paint it black")]
        [InlineData("Sympathy for the Devil (Fatboy Slim remix)", "sympathy for the devil")]
        [InlineData("Gimme Shelter (Live)", "gimme shelter")]
        public void FoldsAwayEverythingThatIsNotTheSong(string raw, string expected)
            => Assert.Equal(expected, MusicTrackTitles.Normalize(raw));

        [Theory]
        // THE DANGEROUS HALF. A leading parenthetical is very often the title itself, and a rule that
        // stripped every bracket would map all of these onto some other song entirely.
        [InlineData("(Don't Fear) The Reaper", "dont fear the reaper")]
        [InlineData("(I Can't Get No) Satisfaction", "i cant get no satisfaction")]
        [InlineData("(What's the Story) Morning Glory?", "whats the story morning glory")]
        // A reprise is a DIFFERENT track with a different length; folding it onto the parent would
        // hand two rows one number. "Reprise" is deliberately not an edition word.
        [InlineData("Sgt. Pepper's Lonely Hearts Club Band (Reprise)", "sgt peppers lonely hearts club band reprise")]
        // A hyphen inside a word, and a title that simply contains a dash, must both survive: the
        // trailing rule only fires on a spaced dash followed by an edition word.
        [InlineData("Sun-Dried", "sun dried")]
        [InlineData("Wish You Were Here - Part Two", "wish you were here part two")]
        public void KeepsTheTitleWhenABracketIsPartOfIt(string raw, string expected)
            => Assert.Equal(expected, MusicTrackTitles.Normalize(raw));

        /// <summary>
        /// A stand-in for one artist's Last.fm catalogue: names keyed the way the real one is, each
        /// with the listener count that decides ties.
        /// </summary>
        private static Dictionary<string, long> Catalogue(params (string Name, long Listeners)[] entries)
            => entries.ToDictionary(e => MusicTrackTitles.Normalize(e.Name), e => e.Listeners);

        [Fact]
        public void MatchesExactlyBeforeItTriesAnythingCleverer()
        {
            // "Tension" is also the opening of "Tension Makes a Tangle" and is under the prefix
            // minimum besides — but it is present verbatim, so none of that is ever consulted.
            var catalogue = Catalogue(("Tension", 14_814), ("Tension Makes a Tangle", 900_000));
            Assert.True(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("Tension"), out var v));
            Assert.Equal(14_814, v);
        }

        [Fact]
        public void CompletesATruncatedTagFromTheBestKnownSongThatFinishesIt()
        {
            // The real case, with the real numbers: the file is "What's The Matter Here.mp3", the ID3
            // title frame says "What's The Matte", and 566 rows in this library are cut the same way.
            var catalogue = Catalogue(("What's the Matter Here?", 36_063), ("Like the Weather", 112_303));
            Assert.True(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("What's The Matte"), out var v));
            Assert.Equal(36_063, v);
        }

        [Fact]
        public void ATypoVariantDoesNotBlockTheSongItMisspells()
        {
            // Why "exactly one completion" was the wrong guard: both of these are really in 10,000
            // Maniacs' catalogue, and requiring uniqueness discarded a 14,803-listener answer because
            // 63 people had scrobbled a misspelling of it.
            var catalogue = Catalogue(("Planned Obsolescence", 14_803), ("Planned Obsolescene", 63));
            Assert.True(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("Planned Obsolesc"), out var v));
            Assert.Equal(14_803, v);
        }

        [Fact]
        public void RefusesWhenTwoREALSongsCouldFinishIt()
        {
            // Comparable audiences means these are two different songs, not one song and its typo —
            // so completing the prefix would be a coin flip, and a miss is the honest answer.
            var catalogue = Catalogue(("Everyday Is Like Sunday", 7_550), ("Everyday Is Like Monday", 5_200));
            Assert.False(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("Everyday Is Like"), out var v));
            Assert.Equal(0, v);
        }

        [Fact]
        public void RefusesShortPrefixesEvenWhenNothingElseCouldFinishThem()
        {
            // "Creep" opens "Creep Show" and nothing else HERE, and completing it would still be a
            // guess — the catalogue simply does not happen to hold the other songs that start that way.
            var catalogue = Catalogue(("Creep Show", 4_210_229));
            Assert.False(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("Creep"), out _));
        }

        [Fact]
        public void AnEmptyKeyMatchesNothingRatherThanTheFirstEntry()
        {
            var catalogue = Catalogue(("Anything At All", 100));
            Assert.False(MusicTrackTitles.TryMatch(catalogue, MusicTrackTitles.Normalize("(Remastered)"), out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        // Nothing but an edition marker leaves nothing to compare — which the caller must treat as
        // "cannot be matched", never as a key that other empty titles would also land on.
        [InlineData("(Remastered)")]
        [InlineData("!!!")]
        public void EmptyWhenNothingIsLeftToCompare(string? raw)
            => Assert.Equal("", MusicTrackTitles.Normalize(raw));
    }

    /// <summary>
    /// The truncated-title repair's DETECTOR (2026-08-31) — what it recovers from a filename, and
    /// every shape it must refuse.
    /// </summary>
    /// <remarks>
    /// This half only proposes; the command will not write a proposal the artist's cached Last.fm
    /// catalogue does not also confirm. That two-source rule is why the refusals below are allowed to
    /// be generous: a row this returns null for is simply left alone, and a row it returns the WRONG
    /// title for would still have to get past the catalogue to reach the database.
    /// </remarks>
    public class MusicFixTitlesRecoveryTests
    {
        private static string? Recover(string title, string fileName)
            => MusicTitleRepair.Recover(title, fileName, out _);

        [Fact]
        public void RecoversTheTitleTheFileNameStillCarries()
        {
            // The real row: id 7, the frame cut at 16 characters.
            Assert.Equal("What's The Matter Here",
                Recover("What's The Matte", "07_10,000 Maniacs - What's The Matter Here.mp3"));
        }

        [Fact]
        public void ReadsFromWHERE_THE_TITLE_STARTS_ratherThanParsingTheNamesGrammar()
        {
            // Track numbers and artist prefixes vary across the library; guessing at that grammar is
            // how a repair pass invents titles. The stored title's own position is the anchor.
            Assert.Equal("Stockton Gala Days", Recover("Stockton Gala Da", "14 Stockton Gala Days.flac"));
            Assert.Equal("Poison In The Well", Recover("Poison In The We", "Poison In The Well.mp3"));
        }

        [Fact]
        public void LeavesAWholeTitleAlone()
        {
            // The name ends where the title ends: nothing was cut off.
            Assert.Null(Recover("Like The Weather", "05_10,000 Maniacs - Like The Weather.mp3"));
        }

        [Fact]
        public void RefusesWhenTheTitleAppearsTwiceInTheName()
        {
            MusicTitleRepair.Recover("Hey Jack Kerouac", "Hey Jack Kerouac - Hey Jack Kerouac (live).mp3", out var ambiguous);
            Assert.True(ambiguous);
        }

        [Fact]
        public void RefusesATitleTooShortToMeanAnything()
        {
            // A four-letter title occurs inside half the filenames in a folder by accident.
            Assert.Null(Recover("Hurt", "03 - Hurt Me Badly.mp3"));
        }

        [Fact]
        public void RefusesWhenTheNameDoesNotContainTheStoredTitleAtAll()
        {
            Assert.Null(Recover("Planned Obsolesc", "01 - track one.mp3"));
        }

        [Fact]
        public void RefusesAFileNameThatMERELY_SAYS_MORE_thanTheTag()
        {
            // THE RULE THAT HAD TO BE ADDED AFTER READING THE FIRST PASS BY EYE. All three of these
            // are complete titles whose files carry a composer, a performing act or a different
            // spelling after them — and all three were confirmed by the outside catalogue as well,
            // because the same badly-named files are what people scrobbled. Only the MECHANISM of
            // truncation separates them: none is a cut at a known width.
            Assert.Null(Recover("Fly Like a Butterfly", "05 Fly Like a Butterfly - Hideki Naganuma.mp3"));
            Assert.Null(Recover("Der Schrei", "01 Der Schrei [Laboratory X].mp3"));
            Assert.Null(Recover("This Lullaby", "09 This Lullabye.mp3"));
            Assert.Null(Recover("Everyday Is Like Sunday", "02 - Everyday Is Like Sunday (Live).mp3"));
        }

        [Fact]
        public void AcceptsOnlyACutAtAWIDTH_DECIDED_IN_ADVANCE()
        {
            // 30 is ID3v1's title field; 16 is a ripper's own limit. Both show as spikes in the
            // library's length histogram. A width fitted per row would make the check vacuous — the
            // stored title is a prefix by construction, so SOME width always reproduces it.
            Assert.Equal("Break on Through (To the Other Side)",
                Recover("Break on Through (To the Other", "01 Break on Through (To the Other Side).flac"));
            // A cut landing on a space leaves one behind, which ingest trimmed: 21 characters cut at
            // 16 is stored as 15.
            Assert.Equal("Candy Everybody Wants",
                Recover("Candy Everybody", "15 Candy Everybody Wants.mp3"));
            // Same shape, but the tail was never cut at 16 or 30 — refused.
            Assert.Null(Recover("Carnival of Sorts", "07 Carnival of Sorts (Box Cars).mp3"));
        }

        [Fact]
        public void RefusesATailThatOpensAFreshBracketedClause()
        {
            // These DO line up with a known cut width, by coincidence — and the coincidence is the
            // point: a cut lands mid-word or mid-phrase, not exactly on the boundary before "(".
            // When it appears to, the tag was right all along and the FILENAME carries an annotation.
            Assert.Null(Recover("Rapper's Delight", "01 Rapper's Delight (1979).mp3"));          // a year
            Assert.Null(Recover("Shape Da Future", "03 Shape Da Future - Hideki Naganuma.mp3")); // a composer
            Assert.Null(Recover("Cross Eyed Mary", "04 Cross Eyed Mary (Jethro Tull Cover).mp3"));// a note
        }

        [Fact]
        public void StillTakesACutThatLandsMidPhrase()
        {
            // The tail continues the sentence rather than opening a new clause, so the bracket rule
            // must not touch these — they are the bulk of the real 30-character ID3v1 cuts.
            Assert.Equal("Sit down. Stand up. (Snakes & Ladders.)",
                Recover("Sit down. Stand up. (Snakes &", "02 Sit down. Stand up. (Snakes & Ladders.).flac"));
            Assert.Equal("Myxomatosis (Judge, Jury & Executioner.)",
                Recover("Myxomatosis (Judge, Jury & Exe", "10 Myxomatosis (Judge, Jury & Executioner.).flac"));
        }
    }

    public class MusicLastFmTopTracksTests
    {
        /// <summary>The shape Last.fm actually returns, trimmed to the fields we read.</summary>
        private const string RealShape = """
            {"toptracks":{"track":[
              {"name":"Creep","playcount":"12345","listeners":"4210229"},
              {"name":"Karma Police","playcount":"999","listeners":"2913066"},
              {"name":"No Surprises","listeners":"2455310"}
            ],"@attr":{"artist":"Radiohead","total":"1000"}}}
            """;

        [Fact]
        public void ReadsNameAndListenersInTheOrderGiven()
        {
            var tracks = MusicLastFm.ParseTopTracks(RealShape);
            Assert.Equal(3, tracks.Count);
            Assert.Equal(("Creep", 4210229L), tracks[0]);
            Assert.Equal("No Surprises", tracks[2].Name);
            // Already descending by listeners — the caller relies on nothing else.
            Assert.True(tracks.Select(t => t.Listeners).SequenceEqual(tracks.Select(t => t.Listeners).OrderByDescending(x => x)));
        }

        [Fact]
        public void OneKnownTrackArrivesBareRatherThanInAListOfOne()
        {
            // The same collapse that cost the album parse 115 rows when tags did it.
            var tracks = MusicLastFm.ParseTopTracks("""{"toptracks":{"track":{"name":"Solo","listeners":"42"}}}""");
            Assert.Equal(("Solo", 42L), Assert.Single(tracks));
        }

        [Fact]
        public void ANumericListenerCountIsAcceptedToo()
        {
            var tracks = MusicLastFm.ParseTopTracks("""{"toptracks":{"track":[{"name":"Solo","listeners":42}]}}""");
            Assert.Equal(42L, Assert.Single(tracks).Listeners);
        }

        [Fact]
        public void ATrackWithNoUsableCountIsDroppedRatherThanScoredZero()
        {
            // A 0 would claim nobody has heard it. Leaving the row unmatched is the honest state.
            var tracks = MusicLastFm.ParseTopTracks("""{"toptracks":{"track":[{"name":"Nameless"},{"name":"Real","listeners":"7"}]}}""");
            Assert.Equal("Real", Assert.Single(tracks).Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        // An unknown artist answers with an error and no toptracks at all — a clean miss.
        [InlineData("""{"error":6,"message":"The artist you supplied could not be found"}""")]
        [InlineData("""{"toptracks":{}}""")]
        // A truncated body is a miss, never a throw: one odd answer costs one artist, not the run.
        [InlineData("""{"toptracks":{"track":[{"name":"Cut""")]
        // Last.fm has served an empty STRING where an object belongs before (that is what broke the
        // album parse); asking for a property of one throws InvalidOperationException, not JsonException.
        [InlineData("""{"toptracks":""}""")]
        public void AMalformedOrEmptyAnswerIsAMiss(string? json)
            => Assert.Empty(MusicLastFm.ParseTopTracks(json));
    }
}
