using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// Persists ROUGH, model-judged MPAA equivalents for movies/series that never carried a real
    /// certificate, into <c>MpaaRatingInferred</c> (+ a provenance <c>MpaaRatingInferredSource</c>),
    /// so the age gate has something to work with instead of treating them as Unknown and hiding
    /// them from kids/teens. The model is the generator (it reads each title's genres / vibe /
    /// intensity / tags and decides the rating); this command is just the idempotent, validated,
    /// resumable <em>sink</em> — it calls no external API.
    ///
    /// <para><b>Guards.</b> A judgment is written only when neither the scraped <c>MpaaRating</c> nor
    /// the legacy <c>Rating</c> maps to a real bucket (G..X) — real certificates are never overridden.
    /// A subject that already has an inferred rating is skipped unless <c>--force</c>. Misc videos are
    /// not stamped: they inherit their related movie/series rating at gate time.</para>
    ///
    /// <para><b>Dry-run-first</b> (global bulk-job rule): prints <c>{planned, skipped, invalid}</c> and
    /// the remaining work-queue size; writes nothing unless <c>--apply</c>. Idempotent, so re-running a
    /// file (or loading files chunk by chunk) is safe. <c>--heuristic-fill</c> is an optional safety
    /// net that fills any still-unrated targets via <see cref="MpaaInference"/> so coverage can be
    /// driven to 100% even if a hand pass missed a few.</para>
    /// </summary>
    [Command("backfill-mpaa-rating", Description = "Load model-judged rough MPAA equivalents (MpaaRatingInferred) for unrated movies/series.")]
    public class BackfillMpaaRatingCommand : BasicDICommand, ICommand
    {
        [CommandOption("file", 'f', Description = "JSON file of judgments: [{\"kind\":\"movie|series\",\"id\":123,\"rating\":\"PG-13\"}].")]
        public string? File { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("force", Description = "Re-infer subjects that already have an inferred rating.")]
        public bool Force { get; set; }

        [CommandOption("model", Description = "Provenance label stamped on each row (default: claude-opus-4-8).")]
        public string ModelId { get; set; } = "claude-opus-4-8";

        [CommandOption("heuristic-fill", Description = "After the file (or instead of it), fill remaining unrated targets via the deterministic heuristic.")]
        public bool HeuristicFill { get; set; }

        [CommandOption("misc-inherit", Description = "Copy each related misc video's parent movie/series effective rating into its inferred rating.")]
        public bool MiscInherit { get; set; }

        [CommandOption("limit", Description = "Max titles to heuristic-fill this run (default 1000).")]
        public int Limit { get; set; } = 1000;

        private const int MaxRealBucket = 6;   // 1..6 are real certs; 7 is Unknown.
        private static readonly HashSet<string> ValidRatings = new(StringComparer.OrdinalIgnoreCase)
            { "G", "PG", "PG-13", "R", "NC-17", "X" };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public BackfillMpaaRatingCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            if (!string.IsNullOrWhiteSpace(File))
                await LoadFileAsync(db, w);
            else if (!HeuristicFill && !MiscInherit)
            { w.WriteLine("Nothing to do: pass --file <json>, --misc-inherit, and/or --heuristic-fill."); return; }

            if (MiscInherit)
                await MiscInheritAsync(db, w);

            if (HeuristicFill)
                await HeuristicFillAsync(db, w);

            var (rm, rs, rmisc) = await RemainingTargetsAsync(db);
            w.WriteLine($"\nwork queue remaining (unrated targets, no inferred yet): {rm} movies + {rs} series + {rmisc} misc.");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
        }

        // ── file sink: load the model's per-title judgments ──────────────────────────
        private async Task LoadFileAsync(MovieDb db, ConsoleWriter w)
        {
            if (!System.IO.File.Exists(File)) { w.WriteLine($"File not found: {File}"); return; }
            List<JudgmentDto>? batch;
            try { batch = JsonSerializer.Deserialize<List<JudgmentDto>>(await System.IO.File.ReadAllTextAsync(File), JsonOpts); }
            catch (JsonException ex) { w.WriteLine($"Bad JSON: {ex.Message}"); return; }
            if (batch is null || batch.Count == 0) { w.WriteLine("Batch is empty."); return; }

            var movieIds = batch.Where(b => b.IsMovie).Select(b => b.id).Distinct().ToList();
            var seriesIds = batch.Where(b => b.IsSeries).Select(b => b.id).Distinct().ToList();
            var miscIds = batch.Where(b => b.IsMisc).Select(b => b.id).Distinct().ToList();
            var movies = await db.Movies.Where(m => movieIds.Contains(m.id)).ToDictionaryAsync(m => m.id);
            var series = await db.Series.Where(s => seriesIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
            var miscs = await db.MiscVideos.Where(v => miscIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);

            int planned = 0, skipReal = 0, skipDone = 0, invalid = 0;
            var source = "ai:" + ModelId;

            foreach (var d in batch)
            {
                var rating = NormalizeRating(d.rating);
                if (d.id <= 0 || (!d.IsMovie && !d.IsSeries && !d.IsMisc) || rating == null)
                { w.WriteLine($"  ! invalid judgment kind='{d.kind}' id={d.id} rating='{d.rating}'"); invalid++; continue; }

                if (d.IsMovie)
                {
                    if (!movies.TryGetValue(d.id, out var m)) { w.WriteLine($"  ! no movie id {d.id}"); invalid++; continue; }
                    if (HasRealCert(db, m.MpaaRating, m.Rating)) { skipReal++; continue; }
                    if (m.MpaaRatingInferred != null && !Force) { skipDone++; continue; }
                    if (Apply) { m.MpaaRatingInferred = rating; m.MpaaRatingInferredSource = source; }
                    planned++;
                }
                else if (d.IsSeries)
                {
                    if (!series.TryGetValue(d.id, out var s)) { w.WriteLine($"  ! no series id {d.id}"); invalid++; continue; }
                    if (HasRealCert(db, s.MpaaRating, s.Rating)) { skipReal++; continue; }
                    if (s.MpaaRatingInferred != null && !Force) { skipDone++; continue; }
                    if (Apply) { s.MpaaRatingInferred = rating; s.MpaaRatingInferredSource = source; }
                    planned++;
                }
                else // misc — no real cert column ever; just don't clobber an existing inferred value
                {
                    if (!miscs.TryGetValue(d.id, out var v)) { w.WriteLine($"  ! no misc id {d.id}"); invalid++; continue; }
                    if (v.MpaaRatingInferred != null && !Force) { skipDone++; continue; }
                    if (Apply) { v.MpaaRatingInferred = rating; v.MpaaRatingInferredSource = source; }
                    planned++;
                }
            }

            if (Apply && planned > 0) await db.SaveChangesAsync();
            w.WriteLine($"file {Path.GetFileName(File)}: {planned} to write, {skipDone} already-inferred, " +
                        $"{skipReal} have a real cert (left alone), {invalid} invalid.");
        }

        // ── optional deterministic safety net for any titles a hand pass didn't cover ──
        private async Task HeuristicFillAsync(MovieDb db, ConsoleWriter w)
        {
            int filled = 0;

            var mTargets = await TargetMovies(db).Where(m => m.MpaaRatingInferred == null)
                .OrderBy(m => m.id).Take(Limit).ToListAsync();
            var mIds = mTargets.Select(m => m.id).ToList();
            var mGenres = await LoadGenresAsync(db.MovieGenres.Where(mg => mIds.Contains(mg.MovieID)).Select(mg => new GenreRow { Id = mg.MovieID, Name = mg.Genre.Name }));
            var mSig = await LoadInsightSignalsAsync(db, InsightSubjectKind.Movie, mIds);
            foreach (var m in mTargets)
            {
                mSig.TryGetValue(m.id, out var sig); mGenres.TryGetValue(m.id, out var gs);
                var (rating, _) = MpaaInference.Infer(gs, sig.intensity, sig.tags, sig.prose);
                if (Apply) { m.MpaaRatingInferred = rating; m.MpaaRatingInferredSource = "heuristic"; }
                filled++;
            }

            var sTargets = await TargetSeries(db).Where(s => s.MpaaRatingInferred == null)
                .OrderBy(s => s.Id).Take(Math.Max(0, Limit - filled)).ToListAsync();
            var sIds = sTargets.Select(s => s.Id).ToList();
            var sGenres = await LoadGenresAsync(db.SeriesGenres.Where(sg => sIds.Contains(sg.SeriesId)).Select(sg => new GenreRow { Id = sg.SeriesId, Name = sg.Genre.Name }));
            var sSig = await LoadInsightSignalsAsync(db, InsightSubjectKind.Series, sIds);
            foreach (var s in sTargets)
            {
                sSig.TryGetValue(s.Id, out var sig); sGenres.TryGetValue(s.Id, out var gs);
                var (rating, _) = MpaaInference.Infer(gs, sig.intensity, sig.tags, sig.prose);
                if (Apply) { s.MpaaRatingInferred = rating; s.MpaaRatingInferredSource = "heuristic"; }
                filled++;
            }

            if (Apply && filled > 0) await db.SaveChangesAsync();
            w.WriteLine($"heuristic-fill: {filled} title(s) {(Apply ? "written" : "planned")}.");
        }

        // ── misc videos inherit their parent movie/series effective rating ────────────
        private async Task MiscInheritAsync(MovieDb db, ConsoleWriter w)
        {
            var misc = db.MiscVideos.Where(v => v.ReviewBatch == null
                && (v.RelatedMovieId != null || v.RelatedSeriesId != null));
            if (!Force) misc = misc.Where(v => v.MpaaRatingInferred == null);
            var rows = await misc.ToListAsync();

            // Bulk-load the parent effective-rating text for every referenced parent.
            var movieParents = rows.Where(v => v.RelatedMovieId != null).Select(v => v.RelatedMovieId!.Value).Distinct().ToList();
            var seriesParents = rows.Where(v => v.RelatedSeriesId != null).Select(v => v.RelatedSeriesId!.Value).Distinct().ToList();
            var mEff = await db.Movies.Where(m => movieParents.Contains(m.id))
                .Select(m => new { m.id, m.MpaaRating, m.Rating, m.MpaaRatingInferred }).ToDictionaryAsync(x => x.id);
            var sEff = await db.Series.Where(s => seriesParents.Contains(s.Id))
                .Select(s => new { s.Id, s.MpaaRating, s.Rating, s.MpaaRatingInferred }).ToDictionaryAsync(x => x.Id);

            int set = 0, parentUnrated = 0;
            foreach (var v in rows)
            {
                string? rating = null, src = null;
                if (v.RelatedMovieId != null && mEff.TryGetValue(v.RelatedMovieId.Value, out var m))
                { rating = EffectiveText(db, m.MpaaRating, m.Rating, m.MpaaRatingInferred); src = $"inherit:movie:{m.id}"; }
                else if (v.RelatedSeriesId != null && sEff.TryGetValue(v.RelatedSeriesId.Value, out var s))
                { rating = EffectiveText(db, s.MpaaRating, s.Rating, s.MpaaRatingInferred); src = $"inherit:series:{s.Id}"; }

                if (rating == null) { parentUnrated++; continue; }   // parent still unrated → re-run after it's set
                if (Apply) { v.MpaaRatingInferred = rating; v.MpaaRatingInferredSource = src; }
                set++;
            }

            if (Apply && set > 0) await db.SaveChangesAsync();
            w.WriteLine($"misc-inherit: {set} inherited from parent, {parentUnrated} skipped (parent still unrated).");
        }

        // The parent's effective rating TEXT (the canonical RatingMap key) for copying onto a misc row.
        private static string? EffectiveText(MovieDb db, string? mpaa, string? legacy, string? inferred)
        {
            foreach (var candidate in new[] { mpaa, legacy })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var t = candidate.Trim();
                if (db.RatingMaps.Any(rm => rm.MovieRating == t && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket))
                    return t;
            }
            return string.IsNullOrWhiteSpace(inferred) ? null : inferred.Trim();
        }

        // ── target predicates / helpers ──────────────────────────────────────────────
        private static IQueryable<Movie> TargetMovies(MovieDb db) => db.Movies
            .Where(m => m.ReviewBatch == null)
            .Where(m => m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries)
            .Where(m => !db.RatingMaps.Any(rm => rm.MovieRating == m.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket)
                     && !db.RatingMaps.Any(rm => rm.MovieRating == m.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket));

        private static IQueryable<Series> TargetSeries(MovieDb db) => db.Series
            .Where(s => s.ReviewBatch == null)
            .Where(s => !db.RatingMaps.Any(rm => rm.MovieRating == s.MpaaRating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket)
                     && !db.RatingMaps.Any(rm => rm.MovieRating == s.Rating && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket));

        private async Task<(int movies, int series, int misc)> RemainingTargetsAsync(MovieDb db) => (
            await TargetMovies(db).CountAsync(m => m.MpaaRatingInferred == null),
            await TargetSeries(db).CountAsync(s => s.MpaaRatingInferred == null),
            await db.MiscVideos.CountAsync(v => v.ReviewBatch == null && v.MpaaRatingInferred == null));

        private static bool HasRealCert(MovieDb db, string? mpaa, string? legacy) =>
            db.RatingMaps.Any(rm => rm.MovieRating == mpaa && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket)
            || db.RatingMaps.Any(rm => rm.MovieRating == legacy && rm.MPARatingID >= 1 && rm.MPARatingID <= MaxRealBucket);

        private static string? NormalizeRating(string? r)
        {
            if (string.IsNullOrWhiteSpace(r)) return null;
            var t = r.Trim().ToUpperInvariant().Replace("PG13", "PG-13").Replace("NC17", "NC-17");
            return ValidRatings.Contains(t) ? t : null;   // canonical forms are all uppercase (G/PG/PG-13/R/NC-17/X)
        }

        private static readonly TagCategory[] SignalCategories =
            { TagCategory.Mood, TagCategory.Tone, TagCategory.Subgenre, TagCategory.ContentDescriptor };

        private static async Task<Dictionary<int, List<string>>> LoadGenresAsync(IQueryable<GenreRow> q) =>
            (await q.ToListAsync()).GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).Where(n => n != null).ToList()!);

        private static async Task<Dictionary<int, (int? intensity, List<MpaaInference.WeightedTag> tags, string? prose)>> LoadInsightSignalsAsync(
            MovieDb db, InsightSubjectKind kind, List<int> ids)
        {
            var insights = await db.TitleInsights
                .Where(ti => ti.SubjectKind == kind && ids.Contains(ti.SubjectId))
                .Include(ti => ti.Tags).ToListAsync();
            return insights.GroupBy(ti => ti.SubjectId).ToDictionary(g => g.Key, g =>
            {
                var latest = g.OrderByDescending(ti => ti.GeneratedUtc).First();
                var tags = latest.Tags.Where(t => t.Value != null && SignalCategories.Contains(t.Category))
                    .Select(t => new MpaaInference.WeightedTag(t.Value!, t.Weight ?? 60)).ToList();
                var prose = string.Join(". ", new[] { latest.Vibe, latest.WhyInteresting }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return (latest.Intensity, tags, (string?)prose);
            });
        }

        private sealed class GenreRow { public int Id { get; set; } public string? Name { get; set; } }

        private sealed class JudgmentDto
        {
            public string? kind { get; set; }
            public int id { get; set; }
            public string? rating { get; set; }
            public bool IsMovie => string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase);
            public bool IsSeries => string.Equals(kind, "series", StringComparison.OrdinalIgnoreCase);
            public bool IsMisc => string.Equals(kind, "misc", StringComparison.OrdinalIgnoreCase);
        }
    }
}
