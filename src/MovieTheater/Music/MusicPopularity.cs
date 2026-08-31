using System;
using MovieTheater.Db;

namespace MovieTheater.Music
{
    /// <summary>
    /// Turning an external audience count into <c>MusicAlbum.Popularity</c>, 0–100 (R9 S10).
    /// </summary>
    /// <remarks>
    /// <para><b>The scale is logarithmic, and it has to be.</b> Last.fm listener counts across this
    /// library span roughly 200 to 4,000,000 — four orders of magnitude — and a linear map would put
    /// every record except a dozen megahits in the bottom two points of the scale, which is the same
    /// as having no signal at all. Log10 spreads the library across the range instead: ~1k listeners
    /// scores about 46, ~100k about 78, a million about 95.</para>
    /// <para><b>What this number is NOT.</b> It is not a rating, not a judgement, and not comparable
    /// with the site's own 0–100 scores — it says "the world has heard of this", which is why the
    /// "Top rated" order blends it with <c>MusicAlbumRating</c> rather than substituting for it, and
    /// why the column is called Popularity.</para>
    /// </remarks>
    public static class MusicPopularity
    {
        /// <summary>The listener count that scores 100. Chosen above the biggest number the library
        /// actually produces so the top of the scale is a ceiling nothing sits on rather than a
        /// plateau several records share.</summary>
        private const double Ceiling = 4_000_000;

        /// <summary>
        /// 0–100 from an audience count, or null when the source gave no usable number (which is a
        /// MISS, not a zero — a zero would say "nobody has heard of it", and the negative cache is
        /// where "we don't know" belongs).
        /// </summary>
        public static int? FromAudience(long? listeners)
        {
            if (listeners == null || listeners < 0) return null;
            if (listeners == 0) return 0;
            var score = 100.0 * Math.Log10(1 + listeners.Value) / Math.Log10(1 + Ceiling);
            return (int)Math.Round(Math.Clamp(score, 0, 100));
        }

        /// <summary>
        /// The library blend behind the "Top rated" order: the house's own average where there is one,
        /// pulled toward the popularity signal while the sample is small.
        /// </summary>
        /// <remarks>
        /// A Bayesian shrink rather than a plain average, because one enthusiastic 100 must not
        /// outrank a record five people agreed was excellent — the classic small-sample problem, and
        /// the reason a naive "sort by rating" list is always topped by whatever exactly one person
        /// scored. <paramref name="prior"/> is what an unrated album is assumed to be worth (the
        /// popularity signal when there is one, else a neutral 50) and <paramref name="priorWeight"/>
        /// is how many votes it takes to overcome it.
        /// <para>An album with NO rating and NO popularity has no opinion attached to it and returns
        /// null — the sort files those last rather than inventing a 50 for them.</para>
        /// </remarks>
        public static double? Blend(double? averageScore, int voteCount, int? popularity, double priorWeight = 3.0)
        {
            var prior = popularity.HasValue ? (double)popularity.Value : (double?)null;
            if (voteCount <= 0 || averageScore == null) return prior;
            var p = prior ?? 50.0;
            return (averageScore.Value * voteCount + p * priorWeight) / (voteCount + priorWeight);
        }

        /// <summary>
        /// Writes one album's popularity result, and decides whether the run that produced it has
        /// earned the right to close that album's popularity queue (<c>music-enrich</c>).
        /// </summary>
        /// <remarks>
        /// <para>The stamp goes on a MISS as well as a hit — that is the negative cache, and it is the
        /// queue's ONLY stop condition (the queue is <c>PopularityCheckedUtc IS NULL</c>), so an album
        /// the internet declined leaves the work set instead of being retried forever.</para>
        /// <para><b>But only a run that actually ASKED may stamp.</b> A <c>--source musicbrainz</c>
        /// run, or any run started with no <c>LastFmApiKey</c> configured, never consults Last.fm and
        /// has therefore learned nothing about popularity. Stamping there would retire the whole
        /// library unasked and hand the later run that finally has a key an empty queue — a state
        /// indistinguishable from a finished job, recoverable only by clearing every row. The genre
        /// half must stay runnable without a key, so the two halves get separate stop conditions.</para>
        /// <para>A miss never erases a score an earlier run established: "we don't know this time" is
        /// not "nobody has heard of it".</para>
        /// </remarks>
        public static void ApplyToAlbum(MusicAlbum album, int? popularity, string? popularitySource,
            bool consultedLastFm, DateTime now)
        {
            if (popularity != null)
            {
                album.Popularity = popularity;
                album.PopularitySource = popularitySource;
            }
            if (consultedLastFm) album.PopularityCheckedUtc = now;
        }
    }
}
