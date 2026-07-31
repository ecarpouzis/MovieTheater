using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Recompute <see cref="ArcadeGame.RatingWeighted"/> — the key <c>sort=rating</c> orders by.
    ///
    /// <para><b>Why this exists.</b> Sorting on the raw score makes the arcade's "best games" list nonsense:
    /// a single 100 outranks a 4,000-vote 94. The lobby's top PS2 game was <i>American Chopper</i> — 99.5 on
    /// IGDB from 49 user votes and no critic score. Ranking by a vote-shrunk score instead drops it to the
    /// bottom 5% (#1083 of 1144) and leaves a credible top: MGS3, Silent Hill 2, GTA: San Andreas, God of War II,
    /// Shadow of the Colossus.</para>
    ///
    /// <para><b>The formula</b> is the standard Bayesian shrink (IMDb's weighted rating):
    /// <c>weighted = (v/(v+m))·raw + (m/(v+m))·mean</c>, where <c>v</c> is the vote count, <c>m</c> = 20 is the
    /// prior strength, and <c>mean</c> is that SYSTEM's vote-weighted mean. Per-system means matter because the
    /// distributions differ (a mid-tier PS2 game and a mid-tier Atari 2600 game don't score alike).</para>
    ///
    /// <para><b>Which raw score.</b> LaunchBox when present (83% of cards), else IGDB — never a blend. LaunchBox
    /// wins every head-to-head, so IGDB's noise can only ever surface on the ~541 cards LaunchBox doesn't rate,
    /// and the shrink tames those.</para>
    ///
    /// <para>Bounded: one pass over the ~17k card anchors, no network, idempotent — re-running yields identical
    /// values for identical inputs. Dry-run unless <c>--apply</c>.</para>
    /// </summary>
    [Command("arcade-rating-weights", Description = "Recompute the confidence-weighted rating used by sort=rating. Dry-run unless --apply.")]
    public class ArcadeRatingWeightsCommand : BasicDICommand, ICommand
    {
        /// <summary>Prior strength, in votes. A card needs ~m votes before its own score outweighs its system's
        /// mean. 20 keeps well-reviewed niche titles reachable while burying 1-vote flukes.</summary>
        private const double PriorVotes = 20.0;

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("top", Description = "Print the top N per system for eyeballing (default 5).")]
        public int Top { get; set; } = 5;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeRatingWeightsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        /// <summary>The card's effective raw score + confidence: the hand-curated community score first (it
        /// exists only where the importers were wrong or absent), then LaunchBox, then IGDB as a fallback.
        /// A curated score with no vote count reports 0 votes, so the shrink pulls it to the system mean.</summary>
        public static (double Raw, int Votes)? Effective(ArcadeGame a)
        {
            if (a.CommunityRating is double cr) return (cr, Math.Max(0, a.CommunityRatingCount ?? 0));
            if (a.LaunchBoxRating is double lb) return (lb, Math.Max(0, a.LaunchBoxRatingCount ?? 0));
            if (a.RatingScore is double ig) return (ig, Math.Max(0, a.RatingCount ?? 0));
            return null;
        }

        public static double Weighted(double raw, int votes, double systemMean)
            => (votes / (votes + PriorVotes)) * raw + (PriorVotes / (votes + PriorVotes)) * systemMean;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled).ToListAsync();
            var anchors = rows.GroupBy(g => new { g.System, g.Title })
                              .Select(grp => grp.OrderBy(x => x.Id).First())
                              .ToList();

            // Vote-weighted mean per system: a system's consensus, dominated by its well-voted games.
            var means = new Dictionary<string, double>();
            foreach (var bySystem in anchors.GroupBy(a => a.System))
            {
                var scored = bySystem.Select(Effective).Where(x => x != null).Select(x => x!.Value).ToList();
                if (scored.Count == 0) continue;
                double wsum = scored.Sum(s => (double)s.Votes);
                means[bySystem.Key] = wsum > 0
                    ? scored.Sum(s => s.Raw * s.Votes) / wsum
                    : scored.Average(s => s.Raw);
            }

            int set = 0, cleared = 0;
            foreach (var a in anchors)
            {
                var eff = Effective(a);
                double? weighted = eff is { } e && means.TryGetValue(a.System, out var mean)
                    ? Math.Round(Weighted(e.Raw, e.Votes, mean), 4)
                    : null;

                if (a.RatingWeighted != weighted)
                {
                    if (weighted == null) cleared++; else set++;
                    if (Apply) a.RatingWeighted = weighted;
                }
            }
            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"{"system",-10}{"mean",8}{"rated",8}");
            foreach (var kv in means.OrderBy(k => k.Key))
                w.WriteLine($"{kv.Key,-10}{kv.Value,8:0.0}{anchors.Count(a => a.System == kv.Key && Effective(a) != null),8}");

            if (Top > 0)
            {
                foreach (var g in anchors.GroupBy(a => a.System).OrderBy(g => g.Key))
                {
                    if (!means.ContainsKey(g.Key)) continue;
                    var top = g.Where(a => Effective(a) != null)
                        .Select(a => (a, e: Effective(a)!.Value))
                        .OrderByDescending(x => Weighted(x.e.Raw, x.e.Votes, means[g.Key]))
                        .Take(Top).ToList();
                    if (top.Count == 0) continue;
                    w.WriteLine();
                    w.WriteLine($"top {g.Key}:");
                    foreach (var (a, e) in top)
                        w.WriteLine($"  {Weighted(e.Raw, e.Votes, means[g.Key]),5:0.0}  {a.Title}  (raw {e.Raw:0.0}, {e.Votes}v)");
                }
            }

            w.WriteLine();
            w.WriteLine($"{{ anchors: {anchors.Count}, weighted: {set}, cleared: {cleared}, systems: {means.Count} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }
    }
}
