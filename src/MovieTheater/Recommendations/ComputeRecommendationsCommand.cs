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
using MovieTheater.Services.Recommendations;

namespace MovieTheater.Recommendations
{
    /// <summary>
    /// Computes each user's personalized recommendations from their ratings and writes them to
    /// <see cref="TitleRecommendation"/> (the source the per-user "For You" channels read). The scoring +
    /// persistence live in <see cref="RecommendationRefresher"/>; this command is the CLI shell.
    ///
    /// <para>Dry-run by default (prints a taste dossier + top picks per user); <c>--apply</c> writes.
    /// Bounded + resumable per the bulk-job rule: <c>--limit</c> caps users per run, a per-user
    /// <see cref="UserTasteProfile.RatingsStamp"/> lets an unchanged user be skipped, and the caller
    /// re-runs until the reported remaining count is 0.</para>
    /// </summary>
    [Command("compute-recommendations", Description = "Compute per-user personalized recommendations into TitleRecommendation.")]
    public class ComputeRecommendationsCommand : BasicDICommand, ICommand
    {
        private const int DossierTopN = 15;

        [CommandOption("user", Description = "Only this username (default: all users with ratings).")]
        public string? User { get; set; }

        [CommandOption("limit", Description = "Max users to (re)compute this run.")]
        public int? Limit { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("force", Description = "Recompute even if the user's ratings/library are unchanged.")]
        public bool Force { get; set; }

        [CommandOption("top", Description = "How many recommendations to store per channel (movies / shows).")]
        public int Top { get; set; } = 100;

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ComputeRecommendationsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();
            var refresher = new RecommendationRefresher();

            w.WriteLine("Loading library feature index…");
            var idx = await refresher.BuildIndexAsync(db);
            w.WriteLine($"  {idx.Movies.Count} movies + {idx.Series.Count} series indexed; {idx.Stats.DocFreq.Count} distinct features.");

            var userQuery = db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(User))
                userQuery = userQuery.Where(u => u.Username == User);
            var users = await userQuery
                .Where(u => db.Viewings.Any(v => v.UserID == u.UserID && v.ViewingType == "Rated"))
                .Select(u => new { u.UserID, u.Username })
                .OrderBy(u => u.UserID)
                .ToListAsync();
            if (users.Count == 0) { w.WriteLine("No users with ratings match."); return; }

            var stamps = await db.UserTasteProfiles.ToDictionaryAsync(p => p.UserId, p => p.RatingsStamp);

            int processed = 0, skipped = 0, remaining = 0;
            foreach (var u in users)
            {
                string stamp = await refresher.StampAsync(db, u.UserID, idx.MaxLibId);
                bool fresh = stamps.TryGetValue(u.UserID, out var have) && have == stamp;
                if (fresh && !Force) { skipped++; continue; }
                if (Limit is int lim && processed >= lim) { remaining++; continue; }

                var result = await refresher.ComputeAsync(db, idx, u.UserID, Top);
                var reasonKeys = result.MovieRecs.Concat(result.SeriesRecs).SelectMany(r => r.ReasonKeys)
                    .Concat(result.Profile.TopSignature.Select(kv => kv.Key));
                var nameOf = await refresher.PersonResolverForKeysAsync(db, reasonKeys);
                PrintDossier(w, u.Username ?? $"#{u.UserID}", result, nameOf);

                if (Apply)
                {
                    await refresher.PersistAsync(db, u.UserID, result, stamp);
                    w.WriteLine($"  wrote {result.MovieRecs.Count} movie + {result.SeriesRecs.Count} series recommendations.");
                }
                processed++;
            }

            w.WriteLine($"\n{{ processed: {processed}, skipped(up-to-date): {skipped}, remaining(stale): {remaining} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }

        private static void PrintDossier(System.IO.TextWriter w, string username, RecommendationRefresher.UserResult r, Func<int, string?> nameOf)
        {
            var p = r.Profile;
            w.WriteLine($"\n══ {username} ══  ratings used: {r.RatedUsed}  mean: {p.MeanRating:F1}  " +
                        $"personalization: {p.PersonalizationWeight:P0}  acclaim-affinity: {p.AcclaimAffinity:+0.00;-0.00}  " +
                        $"candidates: {r.MovieCandidates}mv/{r.SeriesCandidates}sh");
            w.WriteLine("  signature (distinctive) features:");
            foreach (var kv in p.TopSignature.Take(DossierTopN))
            {
                var fw = p.Features[kv.Key];
                w.WriteLine($"    {RecommendationEngine.DescribeFeature(kv.Key, nameOf) ?? kv.Key,-38}  " +
                            $"sig {kv.Value:+0.00;-0.00}  affinity {fw.Affinity:+0.00;-0.00}  n {fw.Support:F1}");
            }
            if (p.Sliders.Count > 0)
                w.WriteLine("  sliders: " + string.Join("  ",
                    p.Sliders.Where(s => s.Importance > 0.05).OrderByDescending(s => s.Importance)
                        .Select(s => $"{s.Name}~{s.Center:F0}(w{s.Importance:F2})")));

            void PrintRecs(string label, IReadOnlyList<Recommendation> recs)
            {
                w.WriteLine($"  ── top {label} ──");
                foreach (var rec in recs.Take(DossierTopN))
                    w.WriteLine($"    {rec.Score,6:F1}  {Trunc(rec.Title, 40),-40}  {RecommendationEngine.RenderReason(rec.ReasonKeys, nameOf, rec.SignalCount)}");
            }
            PrintRecs($"movies ({r.MovieRecs.Count})", r.MovieRecs);
            PrintRecs($"shows ({r.SeriesRecs.Count})", r.SeriesRecs);
        }

        private static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..(n - 1)] + "…");
    }
}
