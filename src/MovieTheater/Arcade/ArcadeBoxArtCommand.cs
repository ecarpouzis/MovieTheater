using System;
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

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Bulk box art for the arcade catalog, from the community libretro-thumbnails repos, downscaled to
    /// thumbnails (via <see cref="ArcadeBoxArt"/> — the same fetch the on-demand <c>/ArcadeImage</c> route
    /// uses). For each enabled game with no <c>BoxArtPath</c>, tries its system's <c>Named_Boxarts/&lt;name&gt;.png</c>,
    /// caches a hit under the posters mount, records the path. Arcade (fbneo) is skipped (MAME names don't
    /// match). Misses are cosmetic non-errors.
    ///
    /// <para>Bulk-job rules: bounded by <c>--limit</c>, resumable via <c>--after-id</c>, reports
    /// <c>{fetched, missed, remaining, nextAfterId}</c>, idempotent (skips games that already have art unless
    /// <c>--overwrite</c>), writes nothing without <c>--apply</c>. Run where the posters mount is present.</para>
    /// </summary>
    [Command("arcade-boxart", Description = "Fetch arcade box art thumbnails from libretro-thumbnails (dry-run unless --apply).")]
    public class ArcadeBoxArtCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write files + rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max games to attempt this run (default 100).")]
        public int Limit { get; set; } = 100;

        [CommandOption("after-id", Description = "Resume cursor: only games with Id greater than this.")]
        public int AfterId { get; set; }

        [CommandOption("overwrite", Description = "Re-fetch games that already have box art.")]
        public bool Overwrite { get; set; }

        [CommandOption("thumb-px", Description = "Max thumbnail dimension in px (default 220).")]
        public int ThumbPx { get; set; } = 220;

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeBoxArtCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (string.IsNullOrEmpty(config.MoviePostersDir))
            { w.WriteLine("MoviePostersDir is not configured — run this where the posters mount is present."); return; }
            var postersRoot = Path.GetFullPath(config.MoviePostersDir);

            await using var db = await dbFactory.CreateDbContextAsync();

            var query = db.ArcadeGames.Where(g => g.IsEnabled && g.Id > AfterId && ArcadeBoxArt.ThumbRepo.Keys.Contains(g.System));
            if (!Overwrite) query = query.Where(g => g.BoxArtPath == null);
            var batch = await query.OrderBy(g => g.Id).Take(Math.Max(1, Limit)).ToListAsync();
            var remaining = await query.CountAsync() - batch.Count;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater-arcade-boxart/1.0");

            int fetched = 0, missed = 0, lastId = AfterId;
            foreach (var game in batch)
            {
                lastId = game.Id;
                var thumb = await ArcadeBoxArt.TryFetchThumbnailAsync(http, game.System, game.CloudRetroGameKey, ThumbPx);
                if (thumb == null) { missed++; w.WriteLine($"  ? [{game.System}] {game.Title} (no match)"); continue; }

                var rel = $"arcade/{game.System}/{game.Id}.png";
                if (Apply)
                {
                    var dest = Path.Combine(postersRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllBytesAsync(dest, thumb);
                    game.BoxArtPath = rel;
                }
                fetched++;
                w.WriteLine($"  + [{game.System}] {game.Title}");
            }

            if (Apply && fetched > 0) await db.SaveChangesAsync();

            w.WriteLine();
            w.WriteLine($"this run: {fetched} fetched, {missed} missed.");
            w.WriteLine($"{{ fetched: {fetched}, missed: {missed}, remaining: {remaining}, nextAfterId: {lastId} }}");
            if (!Apply) w.WriteLine("DRY RUN — no files or rows written. Re-run with --apply.");
            else if (remaining > 0) w.WriteLine($"More to do: re-run with --after-id {lastId}.");
        }
    }
}
