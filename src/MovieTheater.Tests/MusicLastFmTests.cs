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

    [Fact]
    public void A_numeric_listener_count_is_accepted_too()
    {
        // Documented as a string, and always one so far. Read leniently anyway: the cost of being
        // wrong is a lost score rather than a visible failure.
        var (listeners, _) = MusicLastFm.ParseAlbum("""{"album":{"listeners":8675309,"tags":""}}""");
        Assert.Equal(8675309, listeners);
    }
}
