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
    /// internal rank (roughly 0 … 1,000,000 and not a count of anything), Spotify a 0–100 index of
    /// its own construction. Averaging those raw values would be arithmetic on three different
    /// units. Even normalising each to its own maximum would not help, because the distributions are
    /// differently shaped — listener counts are violently long-tailed, Deezer's rank much less so.</para>
    ///
    /// <para><b>The answer is percentiles.</b> Each source's raw value becomes "where this sits among
    /// everything that source told us about", which is unit-free, distribution-free, and is
    /// precisely the question being asked of the collection: rank our music. Two sources that
    /// disagree about how FAMOUS something is can still agree about where it belongs, and it is the
    /// belonging that a shelf ordering needs.</para>
    ///
    /// <para><b>What it costs.</b> A percentile is a statement about a population, so it is only as
    /// good as that source's coverage and must be recomputed when the coverage changes — which is
    /// why the raw value is banked and this is a pure function of it rather than something written
    /// once at fetch time.</para>
    /// </remarks>
    public static class MusicScoreRanking
    {
        /// <summary>
        /// Percentiles for one source's values, keyed by track id: 0 for the least-heard, 100 for the
        /// most.
        /// </summary>
        /// <remarks>
        /// <para><b>Ties share a percentile</b>, and that matters more here than it usually does: a
        /// long tail of tracks all sitting at 1 listener is real, and letting an arbitrary
        /// tie-break spread them across ten points of the scale would invent a ranking nobody
        /// measured. The rank used is the AVERAGE of the positions a tied group spans, the standard
        /// treatment, so a group of equals lands together in the middle of the room it occupies.</para>
        /// <para>A source with a single value gives it 100 — with one observation there is no
        /// distribution to place it in, and 100 ("top of everything I know") is the honest reading of
        /// a lone data point, which <see cref="Consensus"/> then weighs against whatever else voted.</para>
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

        /// <summary>
        /// One track's consensus rank from the sources that answered for it, and how many those were.
        /// </summary>
        /// <remarks>
        /// <para>A plain mean of the sources present, NOT of all sources: a service that has never
        /// heard of a track knows nothing about it, and treating that silence as a zero would push
        /// every obscure record to the bottom for the crime of being obscure on Deezer. Only votes
        /// cast are counted.</para>
        /// <para>No weighting between services. It was tempting to trust the largest audience most,
        /// but the measured agreement between the two sources here is ρ = 0.788 — close enough that
        /// a weighting would be tuning noise, and any weight would have to be justified by evidence
        /// that does not exist yet.</para>
        /// </remarks>
        public static (int? Rank, int Sources) Consensus(IEnumerable<int> sourcePercentiles)
        {
            var scores = sourcePercentiles?.ToList() ?? new List<int>();
            if (scores.Count == 0) return (null, 0);
            return ((int)Math.Round(scores.Average()), scores.Count);
        }
    }
}
