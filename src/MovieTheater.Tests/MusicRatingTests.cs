using MovieTheater.Music;
using Xunit;

namespace MovieTheater.Tests;

/// <summary>
/// The external album rating (2026-08-31). The arithmetic IS the claim, the same way
/// <see cref="MusicPopularityTests"/> is: this scale exists to stop a thin community vote reading
/// like a verdict.
/// </summary>
/// <remarks>
/// Why external at all: the house's own rating table will stay empty — a handful of friends whose
/// music taste barely overlaps will never collect enough votes per album to mean anything — so a
/// RATING has to come from outside, and it is a different fact from popularity, not a rename of it.
/// </remarks>
public class MusicRatingTests
{
    [Fact]
    public void No_rating_is_a_MISS_not_a_zero()
    {
        // A zero would say everyone who heard it thought it was worthless. "Nobody has said" belongs
        // in the negative cache, not in the column.
        Assert.Null(MusicRating.FromStars(null, 0));
        Assert.Null(MusicRating.FromStars(null, 12));
        Assert.Null(MusicRating.FromStars(4.5, 0));
        Assert.Null(MusicRating.FromStars(4.5, -3));
    }

    [Fact]
    public void One_enthusiast_does_not_outrank_a_settled_verdict()
    {
        // THE REASON THIS CLASS EXISTS. MusicBrainz ratings are thin — measured over a 40-album
        // sample of this library, 48% carried one and the MEDIAN was 4 votes. Converted raw, a lone
        // 5.0 would top the shelf over a record forty-five people settled at 3.2.
        var loneRave = MusicRating.FromStars(5.0, 1)!.Value;
        var settled = MusicRating.FromStars(3.2, 45)!.Value;

        Assert.True(loneRave < settled,
            $"a single 5.0 scored {loneRave} and 3.2 from 45 people scored {settled}");
    }

    [Fact]
    public void More_agreement_moves_a_score_further_from_the_middle()
    {
        // The shrink is toward a neutral 50, so the same stars with more votes claim more ground.
        var few = MusicRating.FromStars(4.3, 3)!.Value;
        var many = MusicRating.FromStars(4.3, 60)!.Value;

        Assert.True(50 < few && few < many);
        Assert.InRange(many, 80, 86);
    }

    [Fact]
    public void The_five_star_scale_maps_onto_the_same_0_to_100_the_rest_of_the_site_uses()
    {
        // Well-attested extremes land near the ends without ever reaching them — the shrink always
        // keeps a little doubt, which is the honest reading of a community average.
        var top = MusicRating.FromStars(5.0, 500)!.Value;
        var bottom = MusicRating.FromStars(0.0, 500)!.Value;

        Assert.InRange(top, 95, 100);
        Assert.InRange(bottom, 0, 5);
        Assert.True(bottom < MusicRating.FromStars(2.5, 500) && MusicRating.FromStars(2.5, 500) < top);
    }

    [Fact]
    public void A_star_value_outside_the_scale_is_clamped_rather_than_trusted()
    {
        // Nothing should ever send 7/5, but a bad answer must cost one album, never the column.
        Assert.Equal(MusicRating.FromStars(5.0, 10), MusicRating.FromStars(9.9, 10));
        Assert.Equal(MusicRating.FromStars(0.0, 10), MusicRating.FromStars(-4.0, 10));
    }

    [Fact]
    public void A_middling_rating_is_left_where_it_is()
    {
        // 2.5/5 IS the neutral prior, so the shrink has nothing to pull: the score is 50 at any
        // vote count, which is what makes the prior neutral rather than an opinion of its own.
        Assert.Equal(50, MusicRating.FromStars(2.5, 1));
        Assert.Equal(50, MusicRating.FromStars(2.5, 100));
    }
}
