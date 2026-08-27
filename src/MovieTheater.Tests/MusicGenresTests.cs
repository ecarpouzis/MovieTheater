using System.Linq;
using MovieTheater.Music;

namespace MovieTheater.Tests;

/// <summary>
/// The genre normaliser and the two roll-ups (R9 S10). Pinned because the whole leg's value is that
/// eight spellings of "Rock" become ONE pill in the rail — a regression here does not throw, it just
/// quietly shatters the facet into a long tail of near-duplicates that reads as a broken filter.
/// </summary>
public class MusicGenresTests
{
    [Theory]
    [InlineData("Rock", "Rock")]
    [InlineData("  rock  ", "Rock")]
    [InlineData("ROCK", "Rock")]
    [InlineData("hip-hop", "Hip-Hop")]
    [InlineData("rock and roll", "Rock and Roll")]
    [InlineData("BritPop", "BritPop")]      // hand-cased: somebody's choice, kept
    public void Normalize_folds_case_and_whitespace(string raw, string expected)
    {
        Assert.Equal(expected, MusicGenres.Normalize(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown")]
    [InlineData("other")]
    [InlineData("N/A")]
    [InlineData("Music")]   // the commonest value in the library, and it says nothing here
    public void Normalize_drops_the_values_that_mean_nothing(string raw)
    {
        Assert.Null(MusicGenres.Normalize(raw));
    }

    [Fact]
    public void The_id3v1_numeric_form_resolves_to_its_name()
    {
        // TCON was defined to allow a parenthesised ID3v1 index; plenty of files still carry it, and
        // dropping the digits would silently lose the genre of every one of them.
        Assert.Equal(new[] { "Rock" }, MusicGenres.Split("(17)"));
        Assert.Equal(new[] { "Rock" }, MusicGenres.Split("(17)Rock"));
        Assert.Equal(new[] { "Rock" }, MusicGenres.Split("Rock (17)"));
        Assert.Equal(new[] { "Rock", "Ska" }, MusicGenres.Split("(17)(21)"));
        Assert.Equal(new[] { "Rock" }, MusicGenres.Split("17"));
        // Index 12 IS "Other", which is a value that means nothing — it must not sneak back in
        // through the numeric door.
        Assert.Empty(MusicGenres.Split("(12)"));
        // Out of range is not a genre at all (a stray year or track number landing in the field).
        Assert.Empty(MusicGenres.Split("(2001)"));
    }

    [Fact]
    public void Split_separates_on_the_real_separators_and_dedupes()
    {
        Assert.Equal(new[] { "Rock", "Alternative" }, MusicGenres.Split("Rock;Alternative"));
        Assert.Equal(new[] { "Rock", "Pop" }, MusicGenres.Split("Rock/Pop"));
        Assert.Equal(new[] { "Rock", "Pop" }, MusicGenres.Split("Rock, Pop"));
        Assert.Equal(new[] { "Rock" }, MusicGenres.Split("Rock; rock; ROCK"));
        // A real name containing an ampersand or a plus is ONE genre, not two.
        Assert.Equal(new[] { "R&B" }, MusicGenres.Split("R&B"));
    }

    [Fact]
    public void One_external_tag_naming_two_genres_becomes_two_genres()
    {
        // Measured against MusicBrainz's crowd tags for A Perfect Circle's Mer de Noms: people
        // concatenate. Stored whole these are unusable singletons in the rail's long tail; split
        // they are votes on the pills that are already there.
        Assert.Equal(new[] { "Pop", "Rock" }, MusicGenres.Split("pop/rock"));
        Assert.Equal(new[] { "Alternative", "Indie Rock" }, MusicGenres.Split("alternative/indie rock"));
        Assert.Equal(new[] { "Progressive Rock", "Alternative Rock" }, MusicGenres.Split("progressive rock_alternative rock"));
        Assert.Equal(new[] { "Progressive Rock", "Alternative Rock" }, MusicGenres.Split("progressive rock_alternative rock_alternative rock"));
    }

    [Fact]
    public void An_album_is_every_genre_a_third_of_its_tagged_tracks_agree_on()
    {
        var tracks = new[] { "Rock", "Rock", "Rock", "Alternative", "Alternative", null, null, "" };
        var rolled = MusicGenres.RollUpAlbum(tracks).ToList();
        Assert.Equal(new[] { "Rock", "Alternative" }, rolled.Select(r => r.Genre));
        Assert.Equal(3, rolled[0].Count);
        Assert.Equal(2, rolled[1].Count);
    }

    [Fact]
    public void The_share_is_measured_against_the_TAGGED_tracks_not_all_of_them()
    {
        // Half a record's files being untagged is normal and does not make the other half's verdict
        // less true. Two of ten tracks, both saying Jazz, is a Jazz album.
        var tracks = new string?[] { "Jazz", "Jazz", null, null, null, null, null, null, null, null };
        Assert.Equal(new[] { "Jazz" }, MusicGenres.RollUpAlbum(tracks).Select(r => r.Genre));
    }

    [Fact]
    public void An_album_whose_tracks_all_disagree_still_gets_its_commonest_genre()
    {
        var tracks = new[] { "Rock", "Pop", "Jazz", "Funk", "Soul", "Rock" };
        var rolled = MusicGenres.RollUpAlbum(tracks).ToList();
        Assert.Single(rolled);
        Assert.Equal("Rock", rolled[0].Genre);
    }

    [Fact]
    public void An_untagged_album_gets_nothing()
    {
        Assert.Empty(MusicGenres.RollUpAlbum(new string?[] { null, "", "   " }));
    }

    [Fact]
    public void An_artist_is_the_top_three_across_their_albums_one_vote_each()
    {
        // The first album is filed under four genres; it must not outvote the three albums that agree.
        var albums = new[]
        {
            new[] { "Ska", "Reggae", "Dub", "Punk" },
            new[] { "Rock" },
            new[] { "Rock" },
            new[] { "Rock", "Ska" },
        };
        var rolled = MusicGenres.RollUpArtist(albums).ToList();
        Assert.Equal(3, rolled.Count);
        Assert.Equal("Rock", rolled[0].Genre);
        Assert.Equal(3, rolled[0].Count);
        Assert.Equal("Ska", rolled[1].Genre);
        Assert.Equal(2, rolled[1].Count);
    }
}
