using MovieTheater.Music;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The gate that decides whether a search result is actually the album we asked about.
    ///
    /// <para>Every case here is a cover that really did ship to the live site. The lookup used to take
    /// the FIRST result of a MusicBrainz search with no verification at all, so a search for a Disney
    /// compilation would happily return whatever the credit "Disney" matched first. These tests exist
    /// so that behaviour cannot come back.</para>
    /// </summary>
    public class MusicRemoteArtMatchTests
    {
        // ── the wrong covers that actually shipped ──────────────────────────────────────────────────

        [Theory]
        // Queen's "Greatest Hits" for a Disney compilation of the same shape.
        [InlineData("Greatest Hits", "Queen", "Disney's Greatest Hits", "Disney")]
        // The ZOMBIES trilogy landed on three separate Disney compilations.
        [InlineData("ZOMBIES", "Cast of ZOMBIES", "Disney's Ballads", "Disney")]
        [InlineData("ZOMBIES 2", "Cast of ZOMBIES 2", "Disney's Buddy Songs - Volume 1", "Disney")]
        // High School Musical 3 for "Disney's Hero Songs".
        [InlineData("High School Musical 3", "Various Artists", "Disney's Hero Songs", "Disney")]
        // Spidey and His Amazing Friends for Pixar's "Up".
        [InlineData("Marvel's Spidey and His Amazing Friends", "Patrick Stump", "Up", "Disney")]
        // Coda's sleeve for Led Zeppelin I.
        [InlineData("Coda", "Led Zeppelin", "I", "Led Zeppelin")]
        // A Bruno Mars single for the Gorillaz singles compilation.
        [InlineData("Finesse", "Bruno Mars", "Singles & B-Sides", "Gorillaz")]
        public void Rejects_TheWrongCoversThatShipped(string gotTitle, string gotCredit, string album, string artist)
        {
            var titleOnly = MusicRemoteArt.IsBucketArtist(artist);
            Assert.False(MusicRemoteArt.Accepts(gotTitle, gotCredit, album, artist, titleOnly));
        }

        [Theory]
        [InlineData("The Lion King", "Elton John Hans Zimmer", "The Lion King", "Disney")]
        [InlineData("Toy Story", "Randy Newman", "Toy Story", "Disney")]
        [InlineData("Hybrid Theory", "Linkin Park", "Hybrid Theory", "Linkin Park")]
        [InlineData("Sheik Yerbouti", "Frank Zappa", "Sheik Yerbouti", "Frank Zappa")]
        // Ours carries a qualifier the database does not (short enough to still count as the
        // same name).
        [InlineData("Hybrid Theory EP", "Linkin Park", "Hybrid Theory", "Linkin Park")]
        public void Accepts_TheRightCover(string gotTitle, string gotCredit, string album, string artist)
        {
            var titleOnly = MusicRemoteArt.IsBucketArtist(artist);
            Assert.True(MusicRemoteArt.Accepts(gotTitle, gotCredit, album, artist, titleOnly));
        }

        // ── the individual rules ────────────────────────────────────────────────────────────────────

        [Fact]
        public void DisagreeingVolumeNumbers_AreAVeto()
        {
            // Letters agree almost entirely; the volume does not, and they are different records.
            Assert.Equal(0, MusicRemoteArt.Similar("Back and Forth Series, Vol. 2",
                                                   "Back and Forth, Vol. 4"));
            Assert.True(MusicRemoteArt.Similar("Back and Forth, Vol. 4",
                                               "Back and Forth Series, Vol. 4") > 0.7);
        }

        [Fact]
        public void ShortSubstrings_DoNotCountAsContainment()
        {
            // "Jet Set" sits inside the longer name but is a different record entirely.
            Assert.True(MusicRemoteArt.Similar("Jet Set", "Jet Set Radio Future Original Sound Tracks") < 0.7);
            // A shared name that dominates the longer one still matches.
            Assert.True(MusicRemoteArt.Similar("Hybrid Theory", "Hybrid Theory EP") > 0.9);
        }

        [Fact]
        public void AShortTitleAgainstALongSubtitledOne_IsDeliberatelyRejected()
        {
            // "Beware" really is the same record as "Beware: The Complete Singles 77-82", but nothing
            // separates that pair from "Greatest Hits" vs "Disney's Greatest Hits" — which is Queen's
            // album against a Disney compilation. The gate stays conservative; these are recovered by
            // the release-GROUP fallback or by naming the release precisely. A wrong cover costs more
            // than a missing one.
            Assert.True(MusicRemoteArt.Similar("Beware", "Beware: The Complete Singles 77-82") < 0.72);
            Assert.True(MusicRemoteArt.Similar("Come On Pilgrim", "Come On Pilgrim (remastered)") < 0.72);
        }

        [Fact]
        public void Sanitize_ReplacesLuceneOperatorsWithSpace_NeverDeletesThem()
        {
            // Deleting the hyphen collapsed this to "SingAlong" and matched nothing at MusicBrainz,
            // which is why Dr. Horrible's soundtrack looked unfindable for so long.
            Assert.Equal("Dr. Horrible's Sing Along Blog",
                         MusicRemoteArt.Sanitize("Dr. Horrible's Sing-Along Blog"));
        }

        [Fact]
        public void Karaoke_And_Tribute_AreNeverTheAlbum()
        {
            Assert.True(MusicRemoteArt.LooksLikeImpostor("The Rocky Horror Picture Show (Karaoke Version)"));
            Assert.True(MusicRemoteArt.LooksLikeImpostor("A Musical Tribute to Anthony Burgess"));
            Assert.False(MusicRemoteArt.LooksLikeImpostor("The Rocky Horror Picture Show"));
        }

        [Fact]
        public void StudioNames_AreBuckets_NotPerformers()
        {
            Assert.True(MusicRemoteArt.IsBucketArtist("Disney"));
            Assert.True(MusicRemoteArt.IsBucketArtist("Various Artists"));
            Assert.False(MusicRemoteArt.IsBucketArtist("Danny Elfman"));
        }

        [Fact]
        public void TitleOnly_HoldsTheTitleToAHigherBar()
        {
            // Good enough when the credit corroborates it...
            Assert.True(MusicRemoteArt.Accepts("Enchanted Songs", "Various Artists",
                                               "Enchanted", "Various Artists", titleOnly: false));
            // ...but not when the title is carrying the identity alone.
            Assert.False(MusicRemoteArt.Accepts("Enchanted Songs", "Various Artists",
                                                "Enchanted", "Disney", titleOnly: true));
        }
    }
}
