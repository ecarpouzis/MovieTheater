using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Two clean-ups on the pending-review batch so the queue is trustworthy:
    ///
    /// <para><b>Phase 1 — fix stale titles.</b> The ID-hunt's web-search pass corrected some <c>imdbID</c>s
    /// but left the original suggestion-API candidate label behind, so a handful of rows carry a title that
    /// disagrees with their (correct) id — e.g. Lexx stored as "Lex &amp; Klatten". The IMDB scrape already
    /// captured the right title in <see cref="Movie.ImdbScrapedTitle"/> / <see cref="Series.ImdbScrapedTitle"/>,
    /// so we align <c>Title</c> (and the auto-seeded <c>SimpleTitle</c>) to it. This is IMDB data, never TMDB.</para>
    ///
    /// <para><b>Phase 2 — auto-approve slam-dunk series.</b> A pending <see cref="Series"/> is approved only when
    /// its title matches its on-disk folder, its year matches the folder year, and EVERY episode is mapped to a
    /// file by a confident strategy (not the position-based <c>absolute</c>/<c>combined</c> guesses). Conservative
    /// by design — anything fuzzy is left for manual review (e.g. "Star Wars: Clone Wars" vs the folder's "The
    /// Clone Wars").</para>
    ///
    /// Dry-run by default; pass <c>--apply</c> to write.
    /// </summary>
    [Command("review-autofix", Description = "Fix stale ingest titles (Title <- ImdbScrapedTitle) and auto-approve clean series (title+year match, every episode confidently mapped).")]
    public class ReviewAutofixCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("backfill-posters", Description = "Also fetch posters for any already-approved movie/series that lacks one.")]
        public bool BackfillPosters { get; set; }

        // Position-based episode matches we do NOT trust for auto-approval.
        private static readonly HashSet<string> RiskyStrategies = new(StringComparer.OrdinalIgnoreCase) { "absolute", "combined" };

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheater.Services.Poster.PosterFetchService posterFetch;

        public ReviewAutofixCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            posterFetch = GetRequiredService<MovieTheater.Services.Poster.PosterFetchService>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var w = console.Output;

            // ── Phase 1: align stale Titles (and auto-seeded SimpleTitles) to the IMDb-scraped title ──
            var movieFixes = await db.Movies
                .Where(m => m.ReviewBatch != null && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries
                    && m.ImdbScrapedTitle != null && m.ImdbScrapedTitle != "" && m.Title != m.ImdbScrapedTitle)
                .ToListAsync();
            var seriesFixes = await db.Series
                .Where(s => s.ReviewBatch != null && s.ImdbScrapedTitle != null && s.ImdbScrapedTitle != "" && s.Title != s.ImdbScrapedTitle)
                .ToListAsync();

            w.WriteLine($"Phase 1 — stale titles: {movieFixes.Count} movie(s) + {seriesFixes.Count} series to realign to ImdbScrapedTitle");
            foreach (var m in movieFixes) w.WriteLine($"    M{m.id}: \"{m.Title}\"  ->  \"{m.ImdbScrapedTitle}\"");
            foreach (var s in seriesFixes) w.WriteLine($"    S{s.Id}: \"{s.Title}\"  ->  \"{s.ImdbScrapedTitle}\"");

            foreach (var m in movieFixes)
            {
                if (string.IsNullOrEmpty(m.SimpleTitle) || m.SimpleTitle == m.Title) m.SimpleTitle = m.ImdbScrapedTitle;
                m.Title = m.ImdbScrapedTitle;
            }
            foreach (var s in seriesFixes)
            {
                if (string.IsNullOrEmpty(s.SimpleTitle) || s.SimpleTitle == s.Title) s.SimpleTitle = s.ImdbScrapedTitle;
                s.Title = s.ImdbScrapedTitle;
            }

            // ── Phase 2: auto-approve clean series ──
            var pending = await db.Series.Where(s => s.ReviewBatch != null).ToListAsync();
            var ids = pending.Select(s => s.Id).ToList();
            var eps = await db.Episodes.Where(e => e.SeriesId != null && ids.Contains(e.SeriesId.Value))
                .Select(e => new { SeriesId = e.SeriesId!.Value, e.PlayableId }).ToListAsync();
            var epPids = eps.Where(e => e.PlayableId != null).Select(e => e.PlayableId!.Value).Distinct().ToList();
            var labelsByPlayable = (await db.MediaFiles.Where(f => epPids.Contains(f.PlayableId))
                    .Select(f => new { f.PlayableId, f.Label }).ToListAsync())
                .GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.Select(x => x.Label).ToList());
            var epsBySeries = eps.GroupBy(e => e.SeriesId).ToDictionary(g => g.Key, g => g.ToList());

            var approve = new List<Series>();
            int noTitle = 0, noYear = 0, epGap = 0, riskyEp = 0;
            foreach (var s in pending)
            {
                var folder = ImmediateFolder(s.ReviewSourcePath);
                bool titleMatch = NormTitle(s.Title).Length > 0 && NormTitle(s.Title) == NormTitle(StripYearAndTags(folder));
                var folderYear = YearOf(folder);
                // Ingested series carry their year in ImdbReleaseDate (ReleaseDate/StartYear are unset);
                // the folder is usually a "(start-end)" range, so compare against its start year.
                var seriesYear = s.ReleaseDate?.Year ?? s.ImdbReleaseDate?.Year ?? s.StartYear;
                bool yearMatch = folderYear != null && seriesYear != null && folderYear == seriesYear;

                var sEps = epsBySeries.TryGetValue(s.Id, out var le) ? le : new();
                bool allMapped = sEps.Count > 0 && sEps.All(e => e.PlayableId != null && labelsByPlayable.ContainsKey(e.PlayableId.Value));
                bool allConfident = allMapped && sEps.All(e =>
                    labelsByPlayable[e.PlayableId!.Value].All(lbl => !RiskyStrategies.Contains(Strat(lbl))));

                if (titleMatch && yearMatch && allConfident) { approve.Add(s); continue; }
                if (!titleMatch) noTitle++;
                else if (!yearMatch) noYear++;
                else if (!allMapped) epGap++;
                else riskyEp++;
            }

            w.WriteLine($"\nPhase 2 — auto-approve candidates: {approve.Count} of {pending.Count} pending series");
            foreach (var s in approve.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase))
            {
                var n = epsBySeries.TryGetValue(s.Id, out var le) ? le.Count : 0;
                w.WriteLine($"    ✓ S{s.Id} \"{s.Title}\" ({s.ReleaseDate?.Year ?? s.StartYear}) — {n} eps, all confidently mapped");
            }
            w.WriteLine($"  held for manual review: {noTitle} title-mismatch, {noYear} year-mismatch, {epGap} episode-gap, {riskyEp} risky-episode-match");

            foreach (var s in approve) { s.ReviewBatch = null; s.ReviewProvenance = null; s.ReviewConfidence = null; }

            // ── Phase 3: posters. Newly auto-approved series + (with --backfill-posters) any already-
            // approved movie/series that lacks one. EnsurePosterAsync no-ops when a poster already exists. ──
            var posterTargets = approve.Select(s => (id: s.Id, tt: s.imdbID, series: true)).ToList();
            if (BackfillPosters)
            {
                // Only rows that actually lack a poster record — don't re-walk the whole library.
                var approvedSeries = await db.Series.Where(s => s.ReviewBatch == null && s.imdbID != null && s.PosterDetails == null)
                    .Select(s => new { s.Id, s.imdbID }).ToListAsync();
                var approvedMovies = await db.Movies.Where(m => m.ReviewBatch == null && m.imdbID != null && m.PosterDetails == null
                        && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
                    .Select(m => new { m.id, m.imdbID }).ToListAsync();
                foreach (var s in approvedSeries) if (!posterTargets.Any(p => p.id == s.Id && p.series)) posterTargets.Add((s.Id, s.imdbID, true));
                foreach (var m in approvedMovies) posterTargets.Add((m.id, m.imdbID, false));
            }
            w.WriteLine($"\nPhase 3 — posters to ensure: {posterTargets.Count}" + (BackfillPosters ? " (incl. backfill of already-approved)" : ""));

            if (!Apply)
            {
                w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply to commit.");
                return;
            }

            await db.SaveChangesAsync();

            int gotPoster = 0;
            await Parallel.ForEachAsync(posterTargets, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                async (t, _) => { if (await posterFetch.EnsurePosterAsync(t.id, t.tt, t.series)) System.Threading.Interlocked.Increment(ref gotPoster); });

            w.WriteLine($"\nAPPLIED: realigned {movieFixes.Count + seriesFixes.Count} title(s); auto-approved {approve.Count} series; posters present for {gotPoster}/{posterTargets.Count}.");
        }

        private static string ImmediateFolder(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : "";
        }

        private static int? YearOf(string folder)
        {
            var m = Regex.Match(folder, @"\((\d{4})(?:\s*[-–]\s*\d{0,4})?\)");
            return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
        }

        // Drop (year)/(range) and [tags] so the folder's title text is left for comparison.
        private static string StripYearAndTags(string folder)
        {
            var s = Regex.Replace(folder, @"\([^)]*\)", " ");
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            return s.Trim();
        }

        // Normalize a title for an exact-but-tolerant compare: lowercase, fold ", The"/leading "The",
        // keep only alphanumerics. Conservative — near-misses (Bros vs Brothers) won't match, so they
        // stay in manual review.
        private static string NormTitle(string? t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = t.ToLowerInvariant();
            t = Regex.Replace(t, @",\s*the\b", " the");
            var sb = new StringBuilder();
            foreach (var ch in t) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            var n = sb.ToString();
            if (n.StartsWith("the")) n = n.Substring(3);
            return n;
        }

        private static string Strat(string? label) =>
            label != null && label.StartsWith("match:", StringComparison.OrdinalIgnoreCase) ? label.Substring(6) : "";
    }
}
