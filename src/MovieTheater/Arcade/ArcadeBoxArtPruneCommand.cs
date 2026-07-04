using System;
using System.Collections.Generic;
using System.IO;
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
    /// Reclaims box-art disk space after the lobby became one-card-per-game: only each game's
    /// representative version (the card's <c>artId</c>) needs a thumbnail, so the thumbnails the old
    /// per-ROM warm fetched for the OTHER versions (other regions/revisions/discs of the same game) are now
    /// orphaned. For every <c>(System, Title)</c> group this deletes the box-art file of each
    /// non-representative row and nulls its <c>BoxArtPath</c>. The representative is picked by the SAME
    /// <see cref="ArcadeVersions.Rank"/> the lobby uses, so the kept file is exactly the one served.
    ///
    /// <para>Run where the posters mount is present — on prod that's the API pod:
    /// <c>kubectl exec deploy/movietheater-api -- dotnet /app/MovieTheater.dll arcade-boxart-prune [--apply]</c>.
    /// Bulk-job rules: bounded (one <c>--system</c> at a time, or all with per-system output), idempotent,
    /// guarded (only deletes .png files under <c>{posters}/arcade/</c>, never elsewhere), dry-run unless
    /// <c>--apply</c>. Safe to re-run; a pruned game just re-fetches its representative art on next view.</para>
    /// </summary>
    [Command("arcade-boxart-prune", Description = "Delete box-art thumbnails of non-representative game versions (dry-run unless --apply).")]
    public class ArcadeBoxArtPruneCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Delete files + null rows. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("system", Description = "Only this system (e.g. snes). Omit to process every system.")]
        public string? System { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly MovieTheaterConfiguration config;

        public ArcadeBoxArtPruneCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            if (string.IsNullOrEmpty(config.MoviePostersDir))
            { w.WriteLine("MoviePostersDir is not configured — run where the posters mount is present (the API pod)."); return; }
            var root = Path.GetFullPath(config.MoviePostersDir);
            var arcadeRoot = Path.Combine(root, "arcade") + Path.DirectorySeparatorChar;

            await using var db = await dbFactory.CreateDbContextAsync();
            var systems = string.IsNullOrWhiteSpace(System)
                ? await db.ArcadeGames.Where(g => g.IsEnabled).Select(g => g.System).Distinct().OrderBy(s => s).ToListAsync()
                : new List<string> { System.Trim() };

            int totalDeleted = 0, totalNulled = 0, totalMissing = 0; long totalFreed = 0;
            foreach (var sys in systems)
            {
                var rows = await db.ArcadeGames.Where(g => g.IsEnabled && g.System == sys).ToListAsync();
                int deleted = 0, nulled = 0, missing = 0; long freed = 0;
                foreach (var grp in rows.GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
                {
                    var rep = grp.OrderBy(ArcadeVersions.Rank).First();
                    foreach (var g in grp)
                    {
                        if (ReferenceEquals(g, rep) || string.IsNullOrWhiteSpace(g.BoxArtPath)) continue;
                        nulled++;
                        var full = Path.GetFullPath(Path.Combine(root, g.BoxArtPath));
                        // Guard: only ever touch .png files under {posters}/arcade/.
                        if (full.StartsWith(arcadeRoot, StringComparison.Ordinal) &&
                            full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                        {
                            freed += new FileInfo(full).Length;
                            deleted++;
                            if (Apply) { try { File.Delete(full); } catch { } }
                        }
                        else missing++;
                        if (Apply) g.BoxArtPath = null;
                    }
                }
                if (Apply) await db.SaveChangesAsync();
                totalDeleted += deleted; totalNulled += nulled; totalMissing += missing; totalFreed += freed;
                if (deleted > 0 || nulled > 0)
                    w.WriteLine($"  [{sys,-9}] deleted={deleted,-5} freed={freed / 1024 / 1024,-4}MB nulled={nulled,-5} missingFile={missing}");
            }

            w.WriteLine();
            w.WriteLine($"{{ deleted: {totalDeleted}, freedMB: {totalFreed / 1024 / 1024}, nulled: {totalNulled}, missingFile: {totalMissing} }}");
            if (!Apply) w.WriteLine("DRY RUN — nothing deleted. Re-run with --apply.");
        }
    }
}
