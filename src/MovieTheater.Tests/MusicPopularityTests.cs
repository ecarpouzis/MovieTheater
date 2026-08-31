using System;
using System.Linq;
using MovieTheater.Db;
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
    public void The_top_of_the_scale_is_a_ceiling_nothing_in_the_library_sits_on()
    {
        // The constant's whole job. Measured 2026-08-31 over the 3,829 albums Last.fm gave a count
        // for: the biggest is 5,225,674 listeners. It had climbed through the previous 4,000,000
        // ceiling, pinning 16 albums at exactly 100 — Coldplay, Daft Punk, Gorillaz, Kanye, Lady Gaga,
        // MGMT and Muse were indistinguishable at the top of "Top rated".
        var biggestInLibrary = MusicPopularity.FromAudience(5_225_674)!.Value;

        Assert.True(biggestInLibrary < 100,
            $"the loudest record in the library scores {biggestInLibrary}; the ceiling is meant to sit above it");
        // …and still near the top: a ceiling so high the library bunches in the middle is no better
        // than one it saturates.
        Assert.InRange(biggestInLibrary, 90, 99);
    }

    [Fact]
    public void Re_tuning_the_ceiling_cannot_reorder_the_library()
    {
        // The map is monotonic in listeners, so moving the ceiling shifts labels and never ranking.
        // That is what makes a re-tune safe to apply to a scored library without re-deciding anything.
        long[] audiences = { 0, 200, 1_000, 64_407, 500_000, 3_088_055, 5_225_674 };
        var scores = audiences.Select(a => MusicPopularity.FromAudience(a)!.Value).ToArray();

        Assert.Equal(scores.OrderBy(s => s).ToArray(), scores);
    }

    [Fact]
    public void One_enthusiastic_vote_does_not_outrank_five_agreeing_ones()
    {
        // The whole reason the blend is a Bayesian shrink rather than an average: the classic
        // small-sample problem, and the reason a naive "sort by rating" list is always topped by
        // whatever exactly one person scored.
        var oneRave = MusicPopularity.Blend(100, 1, prior: 50)!.Value;
        var fiveGood = MusicPopularity.Blend(88, 5, prior: 50)!.Value;
        Assert.True(fiveGood > oneRave);
    }

    [Fact]
    public void An_unrated_album_falls_back_to_the_popularity_signal_and_a_blank_one_has_no_opinion()
    {
        Assert.Equal(63, MusicPopularity.Blend(null, 0, prior: 63));
        // Nothing known at all: null, so the sort files it LAST instead of inventing a middle.
        Assert.Null(MusicPopularity.Blend(null, 0, prior: null));
    }

    [Fact]
    public void With_enough_votes_the_house_wins_over_the_prior()
    {
        var many = MusicPopularity.Blend(90, 50, prior: 10)!.Value;
        Assert.InRange(many, 85, 90);
    }

    [Fact]
    public void A_rated_album_with_no_popularity_is_pulled_toward_a_neutral_fifty_not_toward_zero()
    {
        var blended = MusicPopularity.Blend(100, 1, prior: null)!.Value;
        Assert.InRange(blended, 60, 90);
        Assert.True(blended < 100);
    }

    // --- who may close the popularity queue (music-enrich) --------------------------------------
    // The queue is "PopularityCheckedUtc IS NULL". The stamp is the ONLY stop condition, so which
    // runs are allowed to set it decides whether the ratings job can ever be finished.

    [Fact]
    public void A_run_that_asked_LastFm_stamps_even_on_a_miss_so_the_queue_terminates()
    {
        var album = new MusicAlbum { Title = "x", FolderPath = "x" };
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        // A miss is knowledge: the negative cache is what lets an album leave the work set once.
        MusicPopularity.ApplyToAlbum(album, popularity: null, popularitySource: null,
            consultedLastFm: true, now: now);

        Assert.Equal(now, album.PopularityCheckedUtc);
        Assert.Null(album.Popularity);
    }

    [Fact]
    public void A_run_that_never_asked_LastFm_leaves_the_queue_open()
    {
        var album = new MusicAlbum { Title = "x", FolderPath = "x" };

        // --source musicbrainz, or no LastFmApiKey configured. This run learned nothing about
        // popularity; stamping here would retire the whole library unasked and hand the later run
        // that finally has a key an empty queue.
        MusicPopularity.ApplyToAlbum(album, popularity: null, popularitySource: null,
            consultedLastFm: false, now: DateTime.UtcNow);

        Assert.Null(album.PopularityCheckedUtc);
    }

    [Fact]
    public void A_hit_writes_the_score_and_its_source()
    {
        var album = new MusicAlbum { Title = "x", FolderPath = "x" };
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        MusicPopularity.ApplyToAlbum(album, popularity: 78, popularitySource: MusicGenreSources.LastFm,
            consultedLastFm: true, now: now);

        Assert.Equal(78, album.Popularity);
        Assert.Equal(MusicGenreSources.LastFm, album.PopularitySource);
        Assert.Equal(now, album.PopularityCheckedUtc);
    }

    [Fact]
    public void A_later_miss_never_erases_a_score_an_earlier_run_established()
    {
        var album = new MusicAlbum
        {
            Title = "x",
            FolderPath = "x",
            Popularity = 78,
            PopularitySource = MusicGenreSources.LastFm,
        };

        // Last.fm going quiet about a record we already scored must not blank it: "we don't know
        // this time" is not "nobody has heard of it".
        MusicPopularity.ApplyToAlbum(album, popularity: null, popularitySource: null,
            consultedLastFm: true, now: DateTime.UtcNow);

        Assert.Equal(78, album.Popularity);
        Assert.Equal(MusicGenreSources.LastFm, album.PopularitySource);
    }
}
