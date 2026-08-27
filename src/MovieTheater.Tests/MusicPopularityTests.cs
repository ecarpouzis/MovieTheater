using MovieTheater.Music;

namespace MovieTheater.Tests;

/// <summary>
/// The popularity scale and the library blend behind the Music section's "Top rated" order (R9 S10).
/// Both are arithmetic, and the arithmetic IS the claim: a linear popularity map would put the whole
/// library in the bottom two points of the scale, and a plain average would let one enthusiastic 100
/// outrank a record five people agreed was excellent.
/// </summary>
public class MusicPopularityTests
{
    [Fact]
    public void The_scale_is_logarithmic_and_spreads_the_library_across_the_range()
    {
        // Listener counts here span ~200 to ~4,000,000. A linear map would score the 1k-listener
        // record 0 and the 100k-listener record 2; log10 puts real daylight between them.
        var small = MusicPopularity.FromAudience(1_000)!.Value;
        var mid = MusicPopularity.FromAudience(100_000)!.Value;
        var huge = MusicPopularity.FromAudience(2_000_000)!.Value;
        Assert.InRange(small, 40, 55);
        Assert.InRange(mid, 70, 85);
        Assert.InRange(huge, 90, 100);
        Assert.True(small < mid && mid < huge);
    }

    [Fact]
    public void No_number_is_a_MISS_and_zero_listeners_is_a_zero()
    {
        // "We don't know" belongs in the negative cache, not in the column as a 0 that reads as
        // "nobody has heard of it".
        Assert.Null(MusicPopularity.FromAudience(null));
        Assert.Null(MusicPopularity.FromAudience(-1));
        Assert.Equal(0, MusicPopularity.FromAudience(0));
    }

    [Fact]
    public void The_scale_is_clamped_at_both_ends()
    {
        Assert.Equal(100, MusicPopularity.FromAudience(400_000_000));
        Assert.InRange(MusicPopularity.FromAudience(1)!.Value, 0, 10);
    }

    [Fact]
    public void One_enthusiastic_vote_does_not_outrank_five_agreeing_ones()
    {
        // The whole reason the blend is a Bayesian shrink rather than an average: the classic
        // small-sample problem, and the reason a naive "sort by rating" list is always topped by
        // whatever exactly one person scored.
        var oneRave = MusicPopularity.Blend(100, 1, popularity: 50)!.Value;
        var fiveGood = MusicPopularity.Blend(88, 5, popularity: 50)!.Value;
        Assert.True(fiveGood > oneRave);
    }

    [Fact]
    public void An_unrated_album_falls_back_to_the_popularity_signal_and_a_blank_one_has_no_opinion()
    {
        Assert.Equal(63, MusicPopularity.Blend(null, 0, popularity: 63));
        // Nothing known at all: null, so the sort files it LAST instead of inventing a middle.
        Assert.Null(MusicPopularity.Blend(null, 0, popularity: null));
    }

    [Fact]
    public void With_enough_votes_the_house_wins_over_the_prior()
    {
        var many = MusicPopularity.Blend(90, 50, popularity: 10)!.Value;
        Assert.InRange(many, 85, 90);
    }

    [Fact]
    public void A_rated_album_with_no_popularity_is_pulled_toward_a_neutral_fifty_not_toward_zero()
    {
        var blended = MusicPopularity.Blend(100, 1, popularity: null)!.Value;
        Assert.InRange(blended, 60, 90);
        Assert.True(blended < 100);
    }
}
