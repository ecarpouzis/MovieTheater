using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Music
{
    /// <summary>
    /// Turning several services' incompatible numbers into one library-wide ranking (2026-08-31).
    /// Pure arithmetic, so the judgement in it can be argued with in tests rather than in production.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem.</b> Last.fm counts listeners (1 … 4,210,229), Deezer publishes an
    /// internal rank (roughly 0 … 1,000,000 and not a count of anything), ListenBrainz counts listens
    /// from an audience orders of magnitude smaller. Averaging those raw values would be arithmetic
    /// on three different units. Even normalising each to its own maximum would not help, because the
    /// distributions are differently shaped — listener counts are violently long-tailed, Deezer's
    /// rank much less so.</para>
    ///
    /// <para><b>Percentiles make them commensurable.</b> Each source's raw value becomes "where this
    /// sits among everything that source told us about" — unit-free, distribution-free, and precisely
    /// the question being asked of the collection.</para>
    ///
    /// <para><b>But a percentile alone is a confident-looking number over a possibly tiny sample,
    /// and that is the trap this class exists to avoid.</b> Two different things can make one thin:
    /// <list type="number">
    /// <item>the OBSERVATION is thin — a track with 3 listens sits at some percentile, but whether it
    /// belongs above or below its neighbours at 2 and 4 is noise, not signal;</item>
    /// <item>the SOURCE is thin — a service with a small audience is measuring a smaller, less
    /// representative crowd, so even its confident numbers deserve less of the vote.</item>
    /// </list>
    /// So each source's percentile is SHRUNK toward the middle in proportion to how little evidence
    /// backs it, and then the sources are averaged with weights reflecting the audience behind each.
    /// This is the same instinct as <c>MusicPopularity.Blend</c>, which already refuses to let one
    /// enthusiastic rating outrank five people agreeing — applied to audience counts instead of votes.</para>
    /// </remarks>
    public static class MusicScoreRanking
    {
        /// <summary>
        /// Percentiles for one source's values, keyed by row id: 0 for the least-heard, 100 for the
        /// most.
        /// </summary>
        /// <remarks>
        /// <para><b>Ties share a percentile</b>, and that matters more here than it usually does: a
        /// long tail of tracks all sitting at 1 listener is real, and letting an arbitrary tie-break
        /// spread them across ten points of the scale would invent a ranking nobody measured. The
        /// rank used is the AVERAGE of the positions a tied group spans, the standard treatment, so a
        /// group of equals lands together in the middle of the room it occupies.</para>
        /// <para>A source with a single value gives it 100 — with one observation there is no
        /// distribution to place it in, and the consensus then weighs that lone opinion accordingly.</para>
        /// </remarks>
        public static Dictionary<int, int> Percentiles(IReadOnlyCollection<(int TrackId, long Value)> values)
        {
            var result = new Dictionary<int, int>();
            if (values == null || values.Count == 0) return result;
            if (values.Count == 1)
            {
                result[values.First().TrackId] = 100;
                return result;
            }

            var ordered = values.OrderBy(v => v.Value).ToList();
            var n = ordered.Count;
            var i = 0;
            while (i < n)
            {
                // The whole run of equal values, so every member gets the same answer.
                var j = i;
                while (j + 1 < n && ordered[j + 1].Value == ordered[i].Value) j++;
                var averagePosition = (i + j) / 2.0;
                var percentile = (int)Math.Round(100.0 * averagePosition / (n - 1));
                for (var k = i; k <= j; k++) result[ordered[k].TrackId] = Math.Clamp(percentile, 0, 100);
                i = j + 1;
            }
            return result;
        }

        /// <summary>One source's opinion about one track, with everything needed to weigh it.</summary>
        /// <param name="Percentile">Where the source placed it, 0–100.</param>
        /// <param name="RawValue">The source's own count behind that placement.</param>
        /// <param name="ValueScale">What a big number looks like in THIS source's own units, measured
        /// from its data (<see cref="ScaleOf"/>). Decides whether one observation is thin.</param>
        /// <param name="Audience">How many people are behind the source, declared rather than
        /// measured (<see cref="MusicScoreSources.AudienceOf"/>). Decides how much say it gets.</param>
        /// <remarks>
        /// <b>The two scales are different things and conflating them was a real bug.</b> The first
        /// version of this weighed sources by <see cref="ScaleOf"/>, on the assumption that a bigger
        /// service produces bigger numbers. Measured against the live table it produced weights of
        /// 1.00, 1.00 and 1.00: Last.fm's 95th-percentile listener count is 733,295, Deezer's rank
        /// index 525,785 and ListenBrainz's listens 618,632 — all the same order of magnitude, while
        /// the audiences behind them differ by three. The units are not comparable and never were, so
        /// the weighting was inert while looking principled.
        /// </remarks>
        public readonly record struct Opinion(int Percentile, long RawValue, long ValueScale, long Audience);

        /// <summary>
        /// How big a count has to be before a source's placement of it is worth taking at face value.
        /// </summary>
        /// <remarks>
        /// Below this the observation is shrunk toward the middle. It is deliberately generous:
        /// the aim is to stop a handful of plays from asserting a confident position, not to
        /// disbelieve everything under a megahit. Expressed as a fraction of the source's own scale
        /// so it means the same thing to a service counting millions and one counting thousands.
        /// </remarks>
        private const double ConfidentAtFractionOfScale = 0.001;

        /// <summary>
        /// A source's scale: a high percentile of its own values, used as "what a big number looks
        /// like here". The maximum would be hostage to one freak megahit, so this takes a robust
        /// upper quantile instead.
        /// </summary>
        public static long ScaleOf(IReadOnlyCollection<long> rawValues)
        {
            if (rawValues == null || rawValues.Count == 0) return 1;
            var ordered = rawValues.Where(v => v > 0).OrderBy(v => v).ToList();
            if (ordered.Count == 0) return 1;
            var at = (int)Math.Floor(0.95 * (ordered.Count - 1));
            return Math.Max(1, ordered[at]);
        }

        /// <summary>
        /// How much to believe one observation, 0–1, from the count behind it.
        /// </summary>
        /// <remarks>
        /// Logarithmic, because the difference between 3 listens and 30 is enormous evidence-wise
        /// while the difference between 300,000 and 3,000,000 barely changes how sure we are of the
        /// ORDER. Reaches 1 at <see cref="ConfidentAtFractionOfScale"/> of the source's scale and
        /// stays there — beyond that point more listens make a song more popular, not more certain.
        /// </remarks>
        public static double ConfidenceOf(long rawValue, long sourceScale)
        {
            if (rawValue <= 0) return 0;
            var confidentAt = Math.Max(2.0, sourceScale * ConfidentAtFractionOfScale);
            var confidence = Math.Log10(1 + rawValue) / Math.Log10(1 + confidentAt);
            return Math.Clamp(confidence, 0, 1);
        }

        /// <summary>
        /// How much of the vote a source gets, 0–1, from the size of the audience behind it.
        /// </summary>
        /// <remarks>
        /// <para>The reason this exists: a service with tens of thousands of users and one with tens
        /// of millions should not have equal say about what is popular, even when both answer
        /// confidently. The larger crowd is measuring something closer to "the world", and the
        /// smaller one is measuring its own membership — which for ListenBrainz skews heavily toward
        /// people who self-host music software.</para>
        /// <para>Takes AUDIENCES, not value scales. Those are different quantities and using the
        /// wrong one made this function inert once already — see <see cref="Opinion"/>.</para>
        /// <para>Logarithmic, and only ever between <see cref="MinSourceWeight"/> and 1: a smaller
        /// service is quieter, never silent. On the real numbers ListenBrainz lands near 0.6 — it can
        /// move a ranking it disagrees with and can never decide one alone, which is the intent.</para>
        /// </remarks>
        public static double SourceWeight(long audience, long largestAudience)
        {
            if (audience <= 0 || largestAudience <= 0) return MinSourceWeight;
            if (audience >= largestAudience) return 1.0;
            var weight = Math.Log10(1 + audience) / Math.Log10(1 + largestAudience);
            return Math.Clamp(weight, MinSourceWeight, 1.0);
        }

        /// <summary>The quietest a source can be made and still count. A source we bothered to fetch
        /// has something to say; this floor stops the arithmetic from silently discarding it.</summary>
        private const double MinSourceWeight = 0.25;

        /// <summary>
        /// The middle of the scale, which a thin observation is pulled toward. Not 0: an unreliable
        /// reading is a statement of ignorance, and ignorance belongs in the middle rather than at the
        /// bottom, where it would masquerade as "measured to be unpopular".
        /// </summary>
        private const double NeutralPercentile = 50.0;

        /// <summary>
        /// One track's consensus rank across the sources that answered, and how many those were.
        /// </summary>
        /// <remarks>
        /// <para>Each opinion is first shrunk toward <see cref="NeutralPercentile"/> by its own
        /// confidence — so a source that placed a track at 4 on the strength of two listens
        /// contributes something much nearer 50 than 4 — and the shrunken values are then averaged
        /// with the per-source audience weights. A source that has never heard of the track is simply
        /// absent: silence is NOT a zero, or every record obscure on one service would be pushed to
        /// the bottom for it.</para>
        /// <para>The count returned is of sources that ANSWERED, not of confident ones, because it is
        /// shown to a reader as "how many services agree" and dropping a thin one from that number
        /// would overstate the agreement.</para>
        /// </remarks>
        public static (int? Rank, int Sources) Consensus(IEnumerable<Opinion> opinions)
        {
            var list = opinions?.ToList() ?? new List<Opinion>();
            if (list.Count == 0) return (null, 0);

            var largestAudience = list.Max(o => o.Audience);
            double weighted = 0, totalWeight = 0;
            foreach (var opinion in list)
            {
                var confidence = ConfidenceOf(opinion.RawValue, opinion.ValueScale);
                // Shrink toward the middle: at confidence 1 the source's own placement stands, at 0
                // it says nothing and contributes the neutral value.
                var shrunk = NeutralPercentile + confidence * (opinion.Percentile - NeutralPercentile);
                // …and weight the vote by the audience behind the service, and by how much of a
                // reading it actually had. A thin observation from a big service is still thin.
                var weight = SourceWeight(opinion.Audience, largestAudience) * Math.Max(confidence, MinObservationWeight);
                weighted += shrunk * weight;
                totalWeight += weight;
            }
            if (totalWeight <= 0) return (null, list.Count);
            return ((int)Math.Round(Math.Clamp(weighted / totalWeight, 0, 100)), list.Count);
        }

        /// <summary>A floor under an observation's weight so a track only ONE thin source has heard of
        /// still gets a rank rather than dividing by zero — it just gets one very close to neutral.</summary>
        private const double MinObservationWeight = 0.05;
    }
}
