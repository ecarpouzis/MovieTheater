using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Services.Recommendations
{
    /// <summary>
    /// The staleness contract for personalized recommendations: knows, from cheap aggregate queries
    /// alone, whether any user's recs could be out of date. Lives here (not with the refresher in the
    /// web app) so the test project can reach it through the Services reference — the compute half
    /// stays web-side because it also maintains the reco channels.
    /// </summary>
    public sealed class RecommendationStaleness
    {
        private readonly int algoVersion;

        public RecommendationStaleness(int algoVersion) => this.algoVersion = algoVersion;

        /// <summary>Fingerprint of a user's inputs: their latest rating + rating count, the library's max
        /// title id, and the algo version. Unchanged stamp ⇒ nothing to recompute.</summary>
        public async Task<string> StampAsync(MovieDb db, int userId, int maxLibId, CancellationToken cancel = default)
        {
            var agg = await db.Viewings
                .Where(v => v.UserID == userId && v.ViewingType == "Rated")
                .GroupBy(_ => 1)
                .Select(g => new { Max = g.Max(v => v.ViewingID), Count = g.Count() })
                .FirstOrDefaultAsync(cancel);
            return $"{agg?.Max ?? 0}:{agg?.Count ?? 0}:{maxLibId}:{algoVersion}";
        }

        /// <summary>Cheap library-growth fingerprint: the max title id across movies + series (all rows).
        /// Part of the staleness stamp so new library content makes every user's recs recomputable.</summary>
        public async Task<int> MaxLibIdAsync(MovieDb db, CancellationToken cancel = default)
        {
            int maxMovie = await db.Movies.MaxAsync(m => (int?)m.id, cancel) ?? 0;
            int maxSeries = await db.Series.MaxAsync(s => (int?)s.Id, cancel) ?? 0;
            return Math.Max(maxMovie, maxSeries);
        }

        /// <summary>One string that changes iff some user could be stale: the global rated max-id/count,
        /// the library fingerprint, the algo version, and whether any stored stamp has been blanked (an
        /// in-place score edit moves neither max nor count, so SetRatings blanks the stamp instead — see
        /// there). Lets the maintenance loop skip even the per-user scan on a quiet tick: three aggregate
        /// queries whose cost stays constant as Viewing grows, instead of a GROUP BY over every rated row.</summary>
        public async Task<string> SentinelAsync(MovieDb db, CancellationToken cancel = default)
        {
            var agg = await db.Viewings
                .Where(v => v.ViewingType == "Rated")
                .GroupBy(_ => 1)
                .Select(g => new { Max = g.Max(v => v.ViewingID), Count = g.Count() })
                .FirstOrDefaultAsync(cancel);
            int maxLibId = await MaxLibIdAsync(db, cancel);
            bool blanked = await db.UserTasteProfiles
                .AnyAsync(p => p.RatingsStamp == null || p.RatingsStamp == "", cancel);
            return $"{agg?.Max ?? 0}:{agg?.Count ?? 0}:{maxLibId}:{algoVersion}:{(blanked ? "blanked" : "clean")}";
        }

        /// <summary>Users with ratings whose stored profile stamp no longer matches — they've rated
        /// something new, re-scored something (SetRatings blanks the stamp; a blank never matches), or
        /// the library grew. Returns each with its fresh stamp so the caller needn't recompute it.
        /// Cheap: no feature index is built.</summary>
        public async Task<List<(int UserId, string Stamp)>> StaleUsersAsync(MovieDb db, CancellationToken cancel = default)
        {
            int maxLibId = await MaxLibIdAsync(db, cancel);
            // One grouped query for every rater's stamp inputs, instead of a per-user StampAsync round-trip.
            var perUser = await db.Viewings.Where(v => v.ViewingType == "Rated")
                .GroupBy(v => v.UserID)
                .Select(g => new { UserId = g.Key, Max = g.Max(v => v.ViewingID), Count = g.Count() })
                .ToListAsync(cancel);
            var stamps = await db.UserTasteProfiles.ToDictionaryAsync(p => p.UserId, p => p.RatingsStamp, cancel);
            var stale = new List<(int, string)>();
            foreach (var u in perUser.OrderBy(x => x.UserId))
            {
                // Must match StampAsync's format exactly.
                var stamp = $"{u.Max}:{u.Count}:{maxLibId}:{algoVersion}";
                if (!stamps.TryGetValue(u.UserId, out var have) || have != stamp) stale.Add((u.UserId, stamp));
            }
            return stale;
        }
    }
}
