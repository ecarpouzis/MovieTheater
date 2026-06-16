using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// Fills IMDb-sourced metadata (rating, certificate, year, plot, genre/cast fallbacks, poster) for titles
    /// that were never enriched — chiefly the pre-existing series that predate the ingest's scrape, so their
    /// cards/modals show data. Uses <see cref="TitleEnrichService"/> (OMDB → IMDb API, never TMDB). Resumes on
    /// ImdbVerifiedDate IS NULL. Dry-run by default. (The heavyweight Playwright `scrape-imdb` — nm-linked
    /// cast, plot summaries — remains the deeper pass for the Movie table.)
    /// </summary>
    [Command("enrich-titles", Description = "Enrich un-enriched series/movies from IMDb (OMDB): rating, cert, year, plot, poster.")]
    public class EnrichTitlesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("kind", Description = "series | movies | all (default: series).")]
        public string Kind { get; set; } = "series";

        [CommandOption("include-pending", Description = "Also include still-pending (ReviewBatch) rows. Default: approved only.")]
        public bool IncludePending { get; set; }

        [CommandOption("limit", Description = "Max titles to process this run.")]
        public int? Limit { get; set; }

        [CommandOption("id", Description = "Only this one id (use with --kind to disambiguate); implies --force.")]
        public int? OnlyId { get; set; }

        [CommandOption("force", Description = "Re-enrich even rows already marked ImdbVerifiedDate.")]
        public bool Force { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly TitleEnrichService enrich;

        public EnrichTitlesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            enrich = GetRequiredService<TitleEnrichService>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var cancel = console.RegisterCancellationHandler();
            var kind = (Kind ?? "series").Trim().ToLowerInvariant();
            bool doSeries = kind is "series" or "all";
            bool doMovies = kind is "movies" or "all";

            bool force = Force || OnlyId != null;
            var targets = new List<(int id, bool isSeries, string title)>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                if (doSeries)
                {
                    var sq = db.Series.Where(s => s.imdbID != null && s.imdbID != "" && (force || s.ImdbVerifiedDate == null));
                    if (OnlyId != null) sq = sq.Where(s => s.Id == OnlyId.Value);
                    if (!IncludePending) sq = sq.Where(s => s.ReviewBatch == null);
                    targets.AddRange((await sq.OrderBy(s => s.Title).Select(s => new { s.Id, s.Title }).ToListAsync())
                        .Select(s => (s.Id, true, s.Title)));
                }
                if (doMovies)
                {
                    var mq = db.Movies.Where(m => m.imdbID != null && m.imdbID != "" && (force || m.ImdbVerifiedDate == null)
                        && m.TitleType != TitleType.TvSeries && m.TitleType != TitleType.TvMiniSeries);
                    if (OnlyId != null) mq = mq.Where(m => m.id == OnlyId.Value);
                    if (!IncludePending) mq = mq.Where(m => m.ReviewBatch == null);
                    targets.AddRange((await mq.OrderBy(m => m.Title).Select(m => new { m.id, m.Title }).ToListAsync())
                        .Select(m => (m.id, false, m.Title)));
                }
            }
            if (Limit.HasValue) targets = targets.Take(Limit.Value).ToList();

            w.WriteLine($"un-enriched titles to process: {targets.Count} ({kind}{(IncludePending ? ", incl. pending" : "")})");
            foreach (var t in targets.Take(40)) w.WriteLine($"    {(t.isSeries ? "S" : "M")}{t.id}  {t.title}");
            if (!Apply) { w.WriteLine("\nDRY RUN — nothing written. Re-run with --apply."); return; }

            int ok = 0, miss = 0;
            foreach (var t in targets)
            {
                if (cancel.IsCancellationRequested) { w.WriteLine("Cancelled — progress saved per-title."); break; }
                if (await enrich.EnrichAsync(t.id, t.isSeries, force)) ok++; else { miss++; w.WriteLine($"    ! no data: {(t.isSeries ? "S" : "M")}{t.id} {t.title}"); }
                if ((ok + miss) % 25 == 0) w.WriteLine($"  …{ok + miss}/{targets.Count} (enriched {ok}, no-data {miss})");
            }
            w.WriteLine($"\nDONE. enriched {ok}, no-data {miss}, of {targets.Count}.");
        }
    }
}
