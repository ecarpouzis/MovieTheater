using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The single source of the age-gate rule. Both the browse path (GetMovie / base queries) and
    /// the streaming path (StreamController) resolve a title's MPA rating id through here so the
    /// two can never drift (streaming-plan.md §6).
    ///
    /// <para><b>Effective rating.</b> A title's gating rating is resolved by precedence:
    /// the real scraped certificate (<c>MpaaRating</c>) wins; then the frozen legacy <c>Rating</c>;
    /// then the rough, inferred guess (<c>MpaaRatingInferred</c>) for titles that never carried a
    /// real certificate. Only values that map to a real bucket (G..X) count at each step —
    /// "Not Rated"/"Unrated"/blank are skipped so the next source gets a chance. If nothing
    /// resolves, the title is <see cref="UnknownRatingId"/> (conservative: visible only to adults)
    /// until the backfill gives it an inferred rating.</para>
    /// </summary>
    public static class RatingGate
    {
        /// <summary>The lookup id of the "Unknown" MPA bucket (MinAge 99). Used as the conservative
        /// fallback when no rating source resolves to a real bucket.</summary>
        public const int UnknownRatingId = 7;

        /// <summary>Highest real bucket id (X). Buckets 1..6 are real certificates; 7 is Unknown.</summary>
        private const int MaxRealBucket = 6;

        /// <summary>
        /// Resolves a single free-text rating to its MPA bucket id (1..6), or null if it doesn't
        /// map to a real bucket (blank, "Not Rated"/"Unrated"/"N/A", or simply unmapped). Matching
        /// is case-insensitive (DB collation) and trims surrounding whitespace.
        /// </summary>
        private static int? RealBucket(MovieDb db, string? rating)
        {
            if (string.IsNullOrWhiteSpace(rating)) return null;
            var trimmed = rating.Trim();
            return db.RatingMaps
                .Where(rm => rm.MovieRating == trimmed && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket)
                .Select(rm => (int?)rm.MPARatingID)
                .FirstOrDefault();
        }

        /// <summary>
        /// The effective MPA rating id for a title: the real certificate, else the legacy rating,
        /// else the inferred guess, else <see cref="UnknownRatingId"/>. This is the value the age
        /// gate compares against the user's restriction.
        /// </summary>
        public static int EffectiveMpaRatingId(MovieDb db, string? mpaaRating, string? legacyRating, string? inferred)
            => RealBucket(db, mpaaRating)
               ?? RealBucket(db, legacyRating)
               ?? RealBucket(db, inferred)
               ?? UnknownRatingId;

        /// <summary>
        /// Single-field resolver kept for callers that only hold one rating string. Unknown/blank
        /// maps to 0 (most permissive). Prefer <see cref="EffectiveMpaRatingId"/> when the title's
        /// other rating sources are available.
        /// </summary>
        public static int MpaRatingIdFor(MovieDb movieDb, string? movieRating)
            => RealBucket(movieDb, movieRating) ?? 0;

        // ── EF-translatable browse predicates ──────────────────────────────────────
        // These resolve the effective bucket inline (correlated subqueries + COALESCE) so the age
        // gate runs in SQL over the whole table. A title is visible when its effective bucket is
        // ≤ the user's restriction; truly-unknown titles fall to UnknownRatingId (adults only).

        public static Expression<Func<Movie, bool>> MovieVisibleAtAge(MovieDb db, int ageRestriction) =>
            m => (
                db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRatingInferred && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? UnknownRatingId
            ) <= ageRestriction;

        public static Expression<Func<Series, bool>> SeriesVisibleAtAge(MovieDb db, int ageRestriction) =>
            s => (
                db.RatingMaps.Where(rm => rm.MovieRating == s.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == s.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == s.MpaaRatingInferred && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? UnknownRatingId
            ) <= ageRestriction;

        /// <summary>EF-translatable predicate: is this misc video visible at the given age? Misc
        /// carries only an inferred rating (it has no real certificate); an unrated misc video falls
        /// to Unknown (adults only) until the backfill stamps one.</summary>
        public static Expression<Func<Db.MiscVideo, bool>> MiscVisibleAtAge(MovieDb db, int ageRestriction) =>
            v => (
                db.RatingMaps.Where(rm => rm.MovieRating == v.MpaaRatingInferred && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? UnknownRatingId
            ) <= ageRestriction;

        /// <summary>EF-translatable predicate: does this movie's effective bucket equal
        /// <paramref name="bucket"/>? Powers the "browse by exact rating" grid so a title appears
        /// under the rating actually used to gate it (real cert → legacy → inferred).</summary>
        public static Expression<Func<Movie, bool>> MovieEffectiveBucketIs(MovieDb db, int bucket) =>
            m => (
                db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRatingInferred && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? UnknownRatingId
            ) == bucket;

        /// <summary>
        /// EF-translatable predicate: is this movie's effective bucket one of <paramref name="buckets"/>?
        /// One UI button can stand for more than one bucket — NC-17 covers both NC-17(5) and X(6), which
        /// are one certificate as far as anyone browsing is concerned — so the browse-by-rating grid
        /// takes a SET, not a single id.
        /// </summary>
        public static Expression<Func<Movie, bool>> MovieEffectiveBucketIn(MovieDb db, ICollection<int> buckets) =>
            m => buckets.Contains(
                db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? db.RatingMaps.Where(rm => rm.MovieRating == m.MpaaRatingInferred && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket).Select(rm => (int?)rm.MPARatingID).FirstOrDefault()
                ?? UnknownRatingId
            );
    }
}
