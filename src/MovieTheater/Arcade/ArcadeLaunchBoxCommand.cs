using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;
using MovieTheater.Services.LaunchBox;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Import review scores for arcade cards from the LaunchBox Games Database dump — the PRIMARY rating
    /// source (see <see cref="LaunchBoxMetadata"/> for why it displaced IGDB).
    ///
    /// <para>Coverage measured before writing this: LaunchBox rates 14,285 of our 17,291 cards (82.6%) vs
    /// IGDB's 5,866 (33.9%). It also rescues the systems IGDB barely touched — arcade goes from 442 rated
    /// cards to ~1,975.</para>
    ///
    /// <para>Writes to the card's ANCHOR (lowest-id) row, the same convention as the IGDB fields and box art.
    /// Also back-fills Genres / Summary / Developer / Publisher when — and only when — they're still null, so
    /// an IGDB value is never clobbered.</para>
    ///
    /// <para>Bulk-job rules: dry-run unless <c>--apply</c>; bounded by <c>--limit</c> cards; resumable via
    /// <c>--after-id</c>; idempotent (re-running rewrites the same values, and <c>--skip-rated</c> leaves
    /// already-imported cards untouched). No network per card — one dump download, then pure lookups.</para>
    ///
    /// <para>After a full pass run <c>arcade-rating-weights</c> to recompute the sort key.</para>
    /// </summary>
    [Command("arcade-launchbox", Description = "Import LaunchBox community ratings (+ fill missing genres/summary/dev/pub) onto arcade cards. Dry-run unless --apply.")]
    public class ArcadeLaunchBoxCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to process this run (default 2000).")]
        public int Limit { get; set; } = 2000;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. snes, arcade).")]
        public string System { get; set; } = "";

        [CommandOption("zip", Description = "Path to a Metadata.zip (default data/launchbox/Metadata.zip; downloaded if absent).")]
        public string Zip { get; set; } = "data/launchbox/Metadata.zip";

        [CommandOption("refresh", Description = "Re-download the dump even if cached.")]
        public bool Refresh { get; set; }

        [CommandOption("skip-rated", Description = "Skip cards that already carry a LaunchBox rating.")]
        public bool SkipRated { get; set; }

        [CommandOption("no-metadata", Description = "Import the rating only; don't back-fill genres/summary/dev/pub.")]
        public bool NoMetadata { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ArcadeLaunchBoxCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-launchbox/1.0");

            var zipPath = await LaunchBoxMetadata.EnsureDumpAsync(http, Zip, Refresh, w.WriteLine);
            var index = LaunchBoxMetadata.BuildIndex(zipPath, w.WriteLine);

            var sys = System.Trim().ToLowerInvariant();
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && (sys == "" || g.System == sys)).ToListAsync();
            // One card = one (System, Title) group; the anchor is its lowest-id row.
            var cards = rows.GroupBy(g => new { g.System, g.Title })
                .Select(grp => grp.OrderBy(x => x.Id).First())
                .Where(a => a.Id > AfterId)
                .OrderBy(a => a.Id)
                .ToList();
            var batch = cards.Take(Math.Max(1, Limit)).ToList();
            var remaining = cards.Count - batch.Count;

            int matched = 0, missed = 0, skipped = 0, metaFilled = 0, lastId = AfterId, sinceSave = 0;
            foreach (var anchor in batch)
            {
                lastId = anchor.Id;
                if (SkipRated && anchor.LaunchBoxRating != null) { skipped++; continue; }

                var key = LaunchBoxMetadata.NormalizeTitle(anchor.Title);
                if (!index.TryGetValue((anchor.System, key), out var e)) { missed++; continue; }

                if (Apply)
                {
                    anchor.LaunchBoxRating = Math.Round(e.Score100, 4);
                    anchor.LaunchBoxRatingCount = e.Votes;

                    if (!NoMetadata)
                    {
                        // Fill-if-null only: IGDB's curated values win where they exist.
                        bool filled = false;
                        if (anchor.Genres == null && e.Genres != null) { anchor.Genres = e.Genres; filled = true; }
                        if (anchor.Summary == null && e.Overview != null) { anchor.Summary = e.Overview; filled = true; }
                        if (anchor.Developer == null && e.Developer != null) { anchor.Developer = e.Developer; filled = true; }
                        if (anchor.Publisher == null && e.Publisher != null) { anchor.Publisher = e.Publisher; filled = true; }
                        if (filled) metaFilled++;
                    }
                }
                matched++;

                if (matched <= 12)
                    w.WriteLine($"  + [{anchor.System}] {anchor.Title} → {e.Score100:0.0} ({e.Votes} votes)"
                              + (anchor.RatingScore is double ig ? $"   [IGDB had {ig:0.0}]" : ""));

                if (Apply && ++sinceSave >= 500) { await db.SaveChangesAsync(); sinceSave = 0; }
            }
            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"this run: {matched} rated, {missed} no-match, {skipped} already-rated, {metaFilled} metadata back-fills.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId}.");
            else w.WriteLine("Done. Now run: arcade-rating-weights --apply");
        }
    }
}
