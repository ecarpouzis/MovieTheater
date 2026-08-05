using MovieTheater.Music;

namespace MovieTheater.Tests
{
    public class MusicNamingTests
    {
        [Fact]
        public void ArtistFolder_WithYearRange_ParsesBaseAndRange()
        {
            var a = MusicNaming.ParseArtistFolder("AC-DC (1975-2000)");
            Assert.Equal("AC-DC", a.Display);
            Assert.Equal("AC-DC", a.Sort);
            Assert.Equal("1975-2000", a.YearRange);
        }

        [Fact]
        public void ArtistFolder_SingleYear_Parses()
        {
            var a = MusicNaming.ParseArtistFolder("ABBA (1992)");
            Assert.Equal("ABBA", a.Display);
            Assert.Equal("1992", a.YearRange);
        }

        [Fact]
        public void ArtistFolder_NoYears_KeepsName()
        {
            var a = MusicNaming.ParseArtistFolder("Daft Punk");
            Assert.Equal("Daft Punk", a.Display);
            Assert.Null(a.YearRange);
        }

        [Fact]
        public void ArtistFolder_InvertedThe_RestoresArticleForDisplay()
        {
            var a = MusicNaming.ParseArtistFolder("Offspring, The (1994-2003)");
            Assert.Equal("The Offspring", a.Display);
            Assert.Equal("Offspring, The", a.Sort);
        }

        [Fact]
        public void ArtistFolder_LiteralAPrefix_StaysLiteral()
        {
            // Library rule: only "The" inverts; "A"/"An" file literally under A.
            var a = MusicNaming.ParseArtistFolder("A Perfect Circle (2000-2004)");
            Assert.Equal("A Perfect Circle", a.Display);
            Assert.Equal("A Perfect Circle", a.Sort);
        }

        [Fact]
        public void AlbumFolder_ArtistPrefixAndYear_Stripped()
        {
            var al = MusicNaming.ParseAlbumFolder("AC-DC - Back in Black (1980)", "AC-DC");
            Assert.Equal("Back in Black", al.Title);
            Assert.Equal(1980, al.Year);
            Assert.Null(al.Tag);
        }

        [Fact]
        public void AlbumFolder_BracketTag_Extracted()
        {
            var al = MusicNaming.ParseAlbumFolder("AC-DC - Live [Collector's] (1992)", "AC-DC");
            Assert.Equal("Live", al.Title);
            Assert.Equal(1992, al.Year);
            Assert.Equal("Collector's", al.Tag);
        }

        [Fact]
        public void AlbumFolder_DifferentArtistPrefix_KeptVerbatim()
        {
            // Compilation folders name a different artist — that prefix is content, not grammar.
            var al = MusicNaming.ParseAlbumFolder("Armin van Buuren - A State of Trance 383 (2008)", "A State of Trance");
            Assert.Equal("Armin van Buuren - A State of Trance 383", al.Title);
            Assert.Equal(2008, al.Year);
        }

        [Fact]
        public void AlbumFolder_NoYear_TitleOnly()
        {
            var al = MusicNaming.ParseAlbumFolder("Beck - Odelay", "Beck");
            Assert.Equal("Odelay", al.Title);
            Assert.Null(al.Year);
        }

        [Fact]
        public void TrackFile_DashPrefix_Parses()
        {
            var (no, title) = MusicNaming.ParseTrackFileName("07 - Highway to Hell");
            Assert.Equal(7, no);
            Assert.Equal("Highway to Hell", title);
        }

        [Fact]
        public void TrackFile_DotPrefix_Parses()
        {
            var (no, title) = MusicNaming.ParseTrackFileName("12. Loser");
            Assert.Equal(12, no);
            Assert.Equal("Loser", title);
        }

        [Fact]
        public void TrackFile_BareNumberTitle_NotMisparsed()
        {
            // "1979" (the song) must not become track 19 + title "79" or similar.
            var (no, title) = MusicNaming.ParseTrackFileName("1979");
            Assert.Null(no);
            Assert.Equal("1979", title);
        }

        [Fact]
        public void TrackFile_NumberSpaceTitle_NotTreatedAsTrackNo()
        {
            // Ambiguous with real titles ("99 Problems") — deliberately left alone.
            var (no, title) = MusicNaming.ParseTrackFileName("99 Problems");
            Assert.Null(no);
            Assert.Equal("99 Problems", title);
        }
    }
}
