using System.Linq;
using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests;

/// <summary>
/// Reading a Last.fm <c>album.getinfo</c> body. The shapes ARE the subject: Last.fm serves
/// <c>tags</c> three different ways, and the one that reads like a curiosity was a silent
/// data-loss bug in the popularity backfill.
/// </summary>
/// <remarks>
/// The counts quoted below are measured over the 995 answers cached for this library on
/// 2026-08-30, not invented for the test — 869 arrays, 115 empty strings, 11 bare objects.
/// </remarks>
public class MusicLastFmTests
{
    private const string TwoTags = """
        {"album":{"name":"Relationship of Command","artist":"At the Drive-In",
          "listeners":"432101","playcount":"9000000","mbid":"abc",
          "tags":{"tag":[{"name":"post-hardcore","url":"u1"},{"name":"rock","url":"u2"}]}}}
        """;

    // The 115-of-995 shape: nobody has tagged the record, so `tags` is not an empty object or an
    // empty list -- it is the empty STRING.
    private const string NoTags = """
        {"album":{"name":"The Downward Spiral","artist":"Nine Inch Nails",
          "listeners":"1234567","playcount":"50000000","tags":""}}
        """;

    // The 11-of-995 shape: a lone tag is not wrapped in a list of one.
    private const string OneBareTag = """
        {"album":{"name":"Labfunk","artist":"Atjazz","listeners":"5000",
          "tags":{"tag":{"name":"electronic","url":"u"}}}}
        """;

    [Fact]
    public void The_ordinary_shape_yields_listeners_and_rank_weighted_tags()
    {
        var (listeners, tags) = MusicLastFm.ParseAlbum(TwoTags);

        Assert.Equal(432101, listeners);
        // Rank IS the weight, descending, so the strongest tag carries the biggest number.
        Assert.Equal(new[] { ("post-hardcore", 2), ("rock", 1) }, tags.ToArray());
    }

    [Fact]
    public void An_untagged_album_still_gives_up_its_listener_count()
    {
        // THE REGRESSION. TryGetProperty throws InvalidOperationException -- not JsonException --
        // when asked for a property of a string, so reaching for tags.tag here used to throw past
        // the parse guard and discard the listener count that had already been read. ~12% of the
        // library lost a popularity score Last.fm had handed over.
        var (listeners, tags) = MusicLastFm.ParseAlbum(NoTags);

        Assert.Equal(1234567, listeners);
        Assert.Empty(tags);
    }

    [Fact]
    public void A_single_tag_arrives_bare_and_still_counts()
    {
        var (listeners, tags) = MusicLastFm.ParseAlbum(OneBareTag);

        Assert.Equal(5000, listeners);
        Assert.Equal(("electronic", 1), Assert.Single(tags));
    }

    [Fact]
    public void A_not_found_answer_is_a_clean_miss_not_a_throw()
    {
        // Last.fm answers an unknown album with {"error":6,...} and no album element at all.
        var (listeners, tags) = MusicLastFm.ParseAlbum("""{"error":6,"message":"Album not found"}""");

        Assert.Null(listeners);
        Assert.Empty(tags);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("""{"album":"unexpectedly a string"}""")]
    [InlineData("""{"album":{"listeners":{"nested":"nonsense"},"tags":{"tag":[42,"x"]}}}""")]
    public void Nothing_a_server_can_send_turns_into_an_exception(string? body)
    {
        // This runs inside a bulk pass over thousands of albums: one odd answer must cost one
        // album, never the run -- and never a scoreless stamp that retires the album for good.
        var (listeners, tags) = MusicLastFm.ParseAlbum(body);

        Assert.Null(listeners);
        Assert.Empty(tags);
    }

    // --- art out of the same answer ------------------------------------------------------------

    private const string WithArt = """
        {"album":{"name":"Peace Sells","artist":"Megadeth","mbid":"11e4b0f9-0000-4000-8000-000000000001",
          "listeners":"900000",
          "image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/34s/abc123.png","size":"small"},
                   {"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/abc123.png","size":"extralarge"},
                   {"#text":"https://lastfm.freetls.fastly.net/i/u/174s/abc123.png","size":"large"}],
          "tags":""}}
        """;

    // Last.fm answers "no picture" with a grey star, at every size, with a 200 and a valid JPEG.
    private const string StarOnly = """
        {"album":{"name":"Obscure","artist":"Nobody","mbid":"",
          "image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/34s/2a96cbd8b46e442fc41c2b86b821562f.png","size":"small"},
                   {"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png","size":"extralarge"}],
          "tags":""}}
        """;

    [Fact]
    public void Art_takes_the_biggest_size_and_the_release_mbid()
    {
        var (url, mbid) = MusicLastFm.ParseArt(WithArt);

        // Ranked by size, not taken in document order -- "large" is listed last here.
        Assert.Equal("https://lastfm.freetls.fastly.net/i/u/300x300/abc123.png", url);
        Assert.Equal("11e4b0f9-0000-4000-8000-000000000001", mbid);
    }

    [Fact]
    public void The_grey_star_placeholder_is_never_offered_as_a_cover()
    {
        // It passes every pixel test there is, so it has to be refused by identity. Shipping it would
        // be worse than leaving the album blank: a blank album stays in the work queue, a starred one
        // looks finished.
        var (url, mbid) = MusicLastFm.ParseArt(StarOnly);

        Assert.Null(url);
        Assert.Null(mbid);   // empty mbid is absent, not ""
    }

    [Theory]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/abc.png",
                "https://lastfm.freetls.fastly.net/i/u/abc.png")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/174s/abc.png",
                "https://lastfm.freetls.fastly.net/i/u/abc.png")]
    public void The_size_segment_can_be_dropped_for_the_image_as_uploaded(string sized, string original)
    {
        // The largest size advertised is 300x300, below the 600px the mount keeps, so the sized URL
        // would bank a softer cover than the one actually available.
        Assert.Equal(original, MusicLastFm.OriginalSizeUrl(sized));
    }

    [Theory]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/abc.png")]  // already unsized
    [InlineData("https://example.com/cover.jpg")]                  // not a Last.fm URL at all
    [InlineData("")]
    [InlineData(null)]
    public void A_url_with_no_resize_directive_is_left_alone(string? url)
    {
        // Null means "nothing to try differently", and the caller falls back to the advertised URL --
        // it must never be read as "no art".
        Assert.Null(MusicLastFm.OriginalSizeUrl(url));
    }

    [Fact]
    public void An_album_with_art_but_no_mbid_still_offers_its_picture()
    {
        var (url, mbid) = MusicLastFm.ParseArt("""
            {"album":{"image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/z.png","size":"extralarge"}]}}
            """);
        Assert.EndsWith("z.png", url);
        Assert.Null(mbid);
    }

    [Fact]
    public void A_numeric_listener_count_is_accepted_too()
    {
        // Documented as a string, and always one so far. Read leniently anyway: the cost of being
        // wrong is a lost score rather than a visible failure.
        var (listeners, _) = MusicLastFm.ParseAlbum("""{"album":{"listeners":8675309,"tags":""}}""");
        Assert.Equal(8675309, listeners);
    }
}
