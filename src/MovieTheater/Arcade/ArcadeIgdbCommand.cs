using System;
using System.Collections.Generic;
using System.IO;
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
using MovieTheater.Services.Igdb;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Single-pass IGDB enrichment for arcade cards: one lookup per card fills the review score
    /// (<c>total_rating</c>), discovery metadata (genres, themes, game modes, summary, developer, publisher,
    /// ESRB, offline max players), fills a missing release Year, and — with <c>--art</c> — fetches an IGDB
    /// cover for cards libretro-thumbnails couldn't provide. Metadata is stored on the card's anchor (lowest-id)
    /// row, same convention as box art. A normalized-name gate on the IGDB match keeps wrong art/scores off a card.
    ///
    /// <para>Bulk-job rules: dry-run unless <c>--apply</c>; bounded by <c>--limit</c> (cards), resumable via
    /// <c>--after-id</c> (a card's min id); idempotent — skips already-enriched cards (non-null IgdbId) unless
    /// <c>--overwrite</c>. Paced under IGDB's 4 req/s.</para>
    /// </summary>
    [Command("arcade-igdb", Description = "Enrich arcade cards from IGDB: score + genres/themes/modes/summary/dev/pub/esrb (+ --art cover fallback). Dry-run unless --apply.")]
    public class ArcadeIgdbCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max cards to enrich this run (default 100).")]
        public int Limit { get; set; } = 100;

        [CommandOption("after-id", Description = "Resume cursor: only cards whose min version id is greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("system", Description = "Restrict to one system code (e.g. arcade, ps2, psp).")]
        public string System { get; set; } = "";

        [CommandOption("overwrite", Description = "Re-enrich cards that already have an IgdbId.")]
        public bool Overwrite { get; set; }

        [CommandOption("art", Description = "Also fetch an IGDB cover for cards that still have no box art.")]
        public bool Art { get; set; }

        [CommandOption("thumb-px", Description = "Max cover thumbnail dimension in px (default 300).")]
        public int ThumbPx { get; set; } = 300;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeIgdbCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (!IgdbClient.IsConfigured(config))
            { w.WriteLine("IGDB is not configured — set IgdbClientId + IgdbClientSecret."); return; }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-igdb/1.0");
            var igdb = new IgdbClient(http, config.IgdbClientId!, config.IgdbClientSecret!);
            var postersRoot = string.IsNullOrEmpty(config.MoviePostersDir) ? null : Path.GetFullPath(config.MoviePostersDir);
            var sys = System.Trim().ToLowerInvariant();

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(180);

            var rows = await db.ArcadeGames.Where(g => g.IsEnabled && (sys == "" || g.System == sys)).ToListAsync();
            var cards = rows.GroupBy(g => new { g.System, g.CollapseKey })
                .Select(grp => grp.OrderBy(x => x.Id).ToList())
                .Where(c => c[0].Id > AfterId)
                .OrderBy(c => c[0].Id)
                .ToList();
            var batch = cards.Take(Math.Max(1, Limit)).ToList();
            var remaining = cards.Count - batch.Count;

            int matched = 0, missed = 0, skipped = 0, artFetched = 0, seatFlags = 0, lastId = AfterId, sinceSave = 0;

            // Download an IGDB cover, thumbnail it, and point the anchor at the card art file. Shared by the
            // fresh-enrich path and the "already enriched but still no art" backfill below. Returns true on write.
            async Task<bool> TryWriteCover(ArcadeGame anchor, string coverImageId)
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(IgdbClient.CoverUrl(coverImageId));
                    var thumb = ArcadeBoxArt.Thumbnail(bytes, ThumbPx);
                    if (Apply)
                    {
                        var rel = $"arcade/{anchor.System}/{anchor.Id}.png";
                        var dest = Path.Combine(postersRoot!, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await File.WriteAllBytesAsync(dest, thumb);
                        anchor.BoxArtPath = rel;
                    }
                    return true;
                }
                catch (Exception ex) { w.WriteLine($"    (cover fetch failed: {ex.Message})"); return false; }
            }

            foreach (var versions in batch)
            {
                var anchor = versions[0];
                lastId = anchor.Id;
                if (anchor.IgdbId != null && !Overwrite)
                {
                    // Already enriched — but when we're backfilling art (--art) and the card still has NONE,
                    // fetch just the cover by the STORED IGDB id (no re-search, metadata untouched). This is
                    // what fills the saturn/cdi/etc. gaps: those cards were enriched long ago but libretro has
                    // no box-art repo for them, so they never got a cover until now.
                    if (Art && postersRoot != null && anchor.IgdbId is long gid && !CardHasArt(versions, anchor, postersRoot))
                    {
                        string? coverId = null;
                        try { coverId = await igdb.CoverImageIdAsync(gid); }
                        catch (Exception ex) { w.WriteLine($"  ! [{anchor.System}] {anchor.Title} cover lookup: {ex.Message}"); }
                        if (coverId != null && await TryWriteCover(anchor, coverId))
                        {
                            artFetched++;
                            w.WriteLine($"  = art [{anchor.System}] {anchor.Title} (cover from stored IGDB id)");
                            if (Apply && ++sinceSave >= 50) { await db.SaveChangesAsync(); sinceSave = 0; }
                        }
                        await Task.Delay(260); // we made an IGDB request — stay under 4 req/s
                    }
                    skipped++; continue;
                }

                IgdbClient.IgdbGame? game;
                try { game = await igdb.ResolveGameAsync(anchor.Title, IgdbClient.PlatformId(anchor.System)); }
                catch (Exception ex) { w.WriteLine($"  ! [{anchor.System}] {anchor.Title}: {ex.Message}"); missed++; continue; }
                if (game == null) { missed++; w.WriteLine($"  ? [{anchor.System}] {anchor.Title} (no confident IGDB match)"); continue; }

                if (Apply)
                {
                    anchor.IgdbId = game.Id;
                    anchor.RatingScore = game.TotalRating;
                    anchor.RatingCount = game.TotalRatingCount;
                    anchor.Genres = game.Genres;
                    anchor.Themes = game.Themes;
                    anchor.GameModes = game.GameModes;
                    anchor.Summary = game.Summary;
                    anchor.Developer = game.Developer;
                    anchor.Publisher = game.Publisher;
                    anchor.OfflineMaxPlayers = game.OfflineMaxPlayers;
                    anchor.EsrbRating = game.EsrbRating;
                    if (anchor.Year == null && game.FirstReleaseYear is int y) anchor.Year = y;
                }
                matched++;

                // Seat-count cross-check: IGDB offline-max vs our configured MaxPlayers.
                if (game.OfflineMaxPlayers is int omp && omp != anchor.MaxPlayers)
                { seatFlags++; w.WriteLine($"  ~ seats [{anchor.System}] {anchor.Title}: ours={anchor.MaxPlayers} IGDB offline-max={omp}"); }

                var score = game.TotalRating is double tr ? $"{tr:0}★({game.TotalRatingCount})" : "no-rating";
                w.WriteLine($"  + [{anchor.System}] {anchor.Title} → \"{game.Name}\" {score} [{game.Genres}]");

                // Art fallback: when the card still has no box art and IGDB has a cover — OR when the box-art
                // audit flagged its libretro box as a mis-shaped outlier (arcade-boxart-audit --flag), in which
                // case we replace it with the IGDB cover even though art exists.
                bool preferIgdb = string.Equals(anchor.Notes, "boxart-prefer-igdb", StringComparison.Ordinal);
                if (Art && game.CoverImageId != null && postersRoot != null && (preferIgdb || !CardHasArt(versions, anchor, postersRoot)))
                {
                    if (await TryWriteCover(anchor, game.CoverImageId)) artFetched++;
                }

                // Persist periodically so a multi-hour run is crash-resumable (idempotent skip keys off the
                // saved IgdbId) rather than losing everything on a late failure.
                if (Apply && ++sinceSave >= 50) { await db.SaveChangesAsync(); sinceSave = 0; }
                if ((matched + missed) % 500 == 0)
                    w.WriteLine($"  … {matched + missed}/{batch.Count} processed ({matched} enriched, {missed} no-match)");

                await Task.Delay(260); // stay under IGDB's 4 req/s
            }

            if (Apply) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"this run: {matched} enriched, {missed} no-match, {skipped} already-done, {artFetched} covers, {seatFlags} seat mismatches.");
            w.WriteLine($"{{ processed: {batch.Count}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId}.");
        }

        private static bool CardHasArt(List<ArcadeGame> versions, ArcadeGame anchor, string postersRoot)
        {
            if (versions.Any(r => !string.IsNullOrWhiteSpace(r.BoxArtPath))) return true;
            var dest = Path.Combine(postersRoot, "arcade", anchor.System, anchor.Id + ".png");
            return File.Exists(dest);
        }
    }
}
