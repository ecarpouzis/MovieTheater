using System.Collections.Generic;
using System.Linq;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The library-wide ranking arithmetic (2026-08-31): turning several services' incompatible
    /// numbers into one order.
    /// </summary>
    /// <remarks>
    /// The percentile is the whole idea and it is where this can be silently wrong. Sources are on
    /// different scales AND differently shaped distributions — listener counts are violently
    /// long-tailed, Deezer's rank much less so — so anything that averaged raw values, or normalised
    /// each to its own maximum, would produce an order nobody measured while looking perfectly
    /// plausible.
    /// </remarks>
    public class MusicScoreRankingTests
    {
        private static Dictionary<int, int> Rank(params (int Id, long Value)[] values)
            => MusicScoreRanking.Percentiles(values);

        [Fact]
        public void PutsTheLeastHeardAtZeroAndTheMostHeardAtOneHundred()
        {
            var p = Rank((1, 5), (2, 500), (3, 50_000));
            Assert.Equal(0, p[1]);
            Assert.Equal(100, p[3]);
            Assert.Equal(50, p[2]);
        }

        [Fact]
        public void IsAboutPOSITION_notMagnitude()
        {
            // The point of a percentile: a source whose top value is a thousand times its median
            // must not be compressed into the bottom of the scale the way a linear map would.
            var p = Rank((1, 1), (2, 2), (3, 3), (4, 4_000_000));
            Assert.Equal(0, p[1]);
            Assert.Equal(33, p[2]);
            Assert.Equal(67, p[3]);
            Assert.Equal(100, p[4]);
        }

        [Fact]
        public void TiesShareOnePercentileRatherThanBeingSpreadArbitrarily()
        {
            // A long tail of tracks all sitting at one listener is REAL. Letting a tie-break spread
            // them across ten points would invent a ranking nobody measured.
            var p = Rank((1, 1), (2, 1), (3, 1), (4, 900));
            Assert.Equal(p[1], p[2]);
            Assert.Equal(p[2], p[3]);
            Assert.True(p[4] > p[1]);
        }

        [Fact]
        public void ALoneObservationIsTheTopOfEverythingThatSourceKnows()
        {
            // There is no distribution to place one point in, and the consensus is what decides how
            // much that lone opinion is worth.
            Assert.Equal(100, Assert.Single(Rank((7, 42))).Value);
        }

        [Fact]
        public void HasNothingToSayAboutAnEmptySource()
        {
            Assert.Empty(MusicScoreRanking.Percentiles(new List<(int, long)>()));
        }

        [Fact]
        public void AveragesOnlyTheSourcesThatACTUALLY_ANSWERED()
        {
            // A service that has never heard of a track knows nothing about it. Counting that silence
            // as a zero would push every obscure record to the bottom for being obscure on Deezer.
            var (rank, sources) = MusicScoreRanking.Consensus(new[] { 90, 80 });
            Assert.Equal(85, rank);
            Assert.Equal(2, sources);
        }

        [Fact]
        public void SaysHowMANY_agreed_becauseAConsensusOfOneIsNot()
        {
            var (rank, sources) = MusicScoreRanking.Consensus(new[] { 64 });
            Assert.Equal(64, rank);
            Assert.Equal(1, sources);
        }

        [Fact]
        public void ATrackNoSourceKnowsHasNoRankAtAll()
        {
            var (rank, sources) = MusicScoreRanking.Consensus(new int[0]);
            Assert.Null(rank);
            Assert.Equal(0, sources);
        }
    }

    /// <summary>
    /// Reading Deezer's public API, and the gate that keeps a confident answer about the WRONG record
    /// out of the database.
    /// </summary>
    public class MusicDeezerTests
    {
        private const string AlbumSearch = """
            {"data":[{"id":103248,"title":"Hoss","artist":{"id":1,"name":"Lagwagon"},"nb_tracks":22}],"total":1}
            """;

        private const string Tracklist = """
            {"data":[
              {"id":1,"title":"Kids Don't Like To Share","rank":208997},
              {"id":2,"title":"Move The Car","rank":195000},
              {"id":3,"title":"Bombs Away (Remastered)","rank":180000}
            ]}
            """;

        [Fact]
        public void ReadsTheAlbumIdAndTheTwoFieldsTheGateJudgesItBy()
        {
            var hit = MusicDeezer.ParseAlbumSearch(AlbumSearch);
            Assert.NotNull(hit);
            Assert.Equal(103248, hit!.Value.Id);
            Assert.Equal("Hoss", hit.Value.Title);
            Assert.Equal("Lagwagon", hit.Value.Artist);
        }

        [Fact]
        public void ReadsEveryTrackAndItsRank()
        {
            var tracks = MusicDeezer.ParseTracks(Tracklist);
            Assert.Equal(3, tracks.Count);
            Assert.Equal(("Kids Don't Like To Share", 208997L), tracks[0]);
        }

        [Fact]
        public void DropsATrackWithNoUsableRankRatherThanScoringItZero()
        {
            // 0 would assert that nobody plays it; the contract is that an unknown track has no row.
            var tracks = MusicDeezer.ParseTracks("""{"data":[{"title":"Unranked"},{"title":"Real","rank":5}]}""");
            Assert.Equal("Real", Assert.Single(tracks).Title);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("""{"data":[]}""")]
        [InlineData("""{"error":{"type":"DataException","message":"no data"}}""")]
        [InlineData("""{"data":[{"title":"cut off""")]
        public void AMalformedOrEmptyAnswerIsAMissRatherThanAThrow(string? json)
        {
            Assert.Null(MusicDeezer.ParseAlbumSearch(json));
            Assert.Empty(MusicDeezer.ParseTracks(json));
        }

        [Fact]
        public void AcceptsTheAlbumItWasActuallyAskedFor()
        {
            Assert.True(MusicDeezer.AcceptsAlbum("Hoss", "Lagwagon", "Hoss", "Lagwagon"));
        }

        [Fact]
        public void AcceptsAnEditionThatDECORATES_theTitleOnEitherSide()
        {
            // Real shapes from the sample run: one side carries a suffix the other does not.
            Assert.True(MusicDeezer.AcceptsAlbum("Hoss (Remastered)", "Lagwagon", "Hoss", "Lagwagon"));
            Assert.True(MusicDeezer.AcceptsAlbum("Pinocchio", "Disney",
                "Pinocchio (Original Motion Picture Soundtrack)", "Disney"));
        }

        [Fact]
        public void REJECTS_theConfidentlyWrongRecord()
        {
            // The measured failure this gate exists for: a search for Johnny Cash's "America" came
            // back with a different album entirely and would have written 21 wrong scores.
            Assert.False(MusicDeezer.AcceptsAlbum("America", "Simon & Garfunkel", "America", "Johnny Cash"));
        }

        [Fact]
        public void NeedsBOTH_halvesToAgree()
        {
            // Artist alone would accept any other record by them; title alone would accept a covers
            // album by somebody else.
            Assert.False(MusicDeezer.AcceptsAlbum("Back in Black", "AC/DC", "High Voltage", "AC/DC"));
            Assert.False(MusicDeezer.AcceptsAlbum("Hoss", "Some Tribute Band", "Hoss", "Lagwagon"));
        }

        [Fact]
        public void RefusesToJudgeWhenEitherSideIsBlank()
        {
            Assert.False(MusicDeezer.AcceptsAlbum("", "Lagwagon", "Hoss", "Lagwagon"));
            Assert.False(MusicDeezer.AcceptsAlbum("Hoss", "Lagwagon", "Hoss", ""));
        }
    }
}
