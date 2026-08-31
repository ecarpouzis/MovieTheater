using System;

namespace MovieTheater.Music
{
    /// <summary>
    /// Turning an outside community's star rating into <c>MusicAlbum.ExternalRating</c>, 0–100.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this has to come from outside at all.</b> The site has a perfectly good rating
    /// table and it will stay empty: this is a handful of friends whose music taste barely overlaps,
    /// so no album will ever collect enough house votes to mean anything (Eric, 2026-08-31). A
    /// RATING is therefore an external fact here, the way <see cref="MusicPopularity"/> already is —
    /// and it is a DIFFERENT fact. Popularity says how widely a record is heard; this says how good
    /// the people who heard it think it is. Neither substitutes for the other, and the site names
    /// them separately everywhere it shows them.</para>
    ///
    /// <para><b>Why a shrink rather than a floor.</b> MusicBrainz release-group ratings are real but
    /// thin — measured over a 40-album sample of this library, 48% carried one and the MEDIAN was 4
    /// votes. A raw conversion would put a single enthusiast's 5.0 above a record forty-five people
    /// settled at 3.2, which is the same small-sample bug <see cref="MusicPopularity.Blend"/> exists
    /// to avoid. Pulling every score toward a neutral 50 by <see cref="PriorWeight"/> votes' worth of
    /// doubt costs a well-attested rating almost nothing and costs a one-vote rating most of its
    /// claim: 5.0 from one vote lands at 58, while 4.3 from fifteen lands at 77.</para>
    ///
    /// <para>A floor would have been the other option and is worse: it throws away the information
    /// that somebody did rate the record, and it puts a cliff in the middle of the scale where the
    /// shrink puts a slope.</para>
    /// </remarks>
    public static class MusicRating
    {
        /// <summary>What an unrated record is assumed to be worth — the middle, no opinion either way.</summary>
        public const double NeutralPrior = 50.0;

        /// <summary>How many votes of doubt a new rating has to overcome. Five is about where the
        /// median MusicBrainz album (4 votes) still moves the needle without owning it.</summary>
        public const double PriorWeight = 5.0;

        /// <summary>
        /// 0–100 from a 0–5 community rating and its vote count, or null when there is no rating —
        /// which is a MISS, not a zero. A zero would say "everyone who heard it thought it was
        /// worthless", and the negative cache is where "nobody has said" belongs.
        /// </summary>
        public static int? FromStars(double? stars, int votes)
        {
            if (stars == null || votes <= 0) return null;
            var raw = Math.Clamp(stars.Value, 0.0, 5.0) / 5.0 * 100.0;
            var shrunk = (raw * votes + NeutralPrior * PriorWeight) / (votes + PriorWeight);
            return (int)Math.Round(Math.Clamp(shrunk, 0.0, 100.0));
        }
    }
}
