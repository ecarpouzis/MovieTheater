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

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Stage D of the series split (THE FLIP, irreversible): delete the series-typed <see cref="Movie"/>
    /// twins (<c>TitleType IN (4,5)</c>) that have lived alongside their <see cref="Series"/> rows during
    /// dual-existence. Every twin's metadata is already copied to the Series side (verified by pre-flight),
    /// so this is lossless. Before deleting, it SNAPSHOTS every affected row into <c>_flip_backup_*</c>
    /// tables (logical rollback) and runs inside a transaction that ROLLS BACK if the episode count changes
    /// or any twin survives. The <c>FK_Episode_Movie_SeriesMovieId</c> constraint is CASCADE, so it is
    /// dropped first (otherwise deleting a twin would cascade-delete its episodes); the now-orphaned
    /// <c>Episode.SeriesMovieId</c> COLUMN is left in place so the still-deployed app (which maps it) keeps
    /// working — drop the column later in a deploy. Dry-run by default; <c>--apply</c> to execute.
    /// </summary>
    [Command("flip-series", Description = "THE FLIP (Stage D): delete the 244 series-typed Movie twins. Snapshots to _flip_backup_*, transactional, rolls back on anomaly. Dry-run by default.")]
    public class FlipSeriesCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Execute the flip. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("finish-orphans", Description = "Post-flip cleanup: delete orphan twin Playables the flip skipped (referenced by a stale channel schedule) + those schedule items.")]
        public bool FinishOrphans { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public FlipSeriesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        private const string TWINS = "SELECT id FROM Movie WHERE TitleType IN (4,5)";

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            await using var db = await dbFactory.CreateDbContextAsync();

            async Task<int> Count(string sql) => await db.Database.SqlQueryRaw<int>(sql).FirstAsync();

            if (FinishOrphans) { await FinishOrphansAsync(console, db, Count); return; }

            int twins = await Count("SELECT COUNT(*) AS Value FROM Movie WHERE TitleType IN (4,5)");
            int episodes = await Count("SELECT COUNT(*) AS Value FROM Episode");
            int series = await Count("SELECT COUNT(*) AS Value FROM Series");

            if (twins == 0) { w.WriteLine("No series-typed Movie twins remain — already flipped. Nothing to do."); return; }

            int genres = await Count($"SELECT COUNT(*) AS Value FROM MovieGenre WHERE MovieID IN ({TWINS})");
            int credits = await Count($"SELECT COUNT(*) AS Value FROM MovieCredit WHERE MovieID IN ({TWINS})");
            int plots = await Count($"SELECT COUNT(*) AS Value FROM MoviePlotSummary WHERE MovieID IN ({TWINS})");
            int posters = await Count($"SELECT COUNT(*) AS Value FROM MoviePosterDetails WHERE MovieId IN ({TWINS})");
            int files = await Count($"SELECT COUNT(*) AS Value FROM MediaFile WHERE PlayableId IN (SELECT PlayableId FROM Movie WHERE TitleType IN (4,5) AND PlayableId IS NOT NULL)");
            int playables = await Count($"SELECT COUNT(*) AS Value FROM Playable WHERE Id IN (SELECT PlayableId FROM Movie WHERE TitleType IN (4,5) AND PlayableId IS NOT NULL)");
            int viewings = await Count($"SELECT COUNT(*) AS Value FROM Viewing WHERE MovieID IN ({TWINS})");

            w.WriteLine($"FLIP — would delete {twins} series-typed Movie twins{(Apply ? "" : "  (DRY RUN)")}:");
            w.WriteLine($"  Movie twins         {twins}");
            w.WriteLine($"  MovieGenre          {genres}");
            w.WriteLine($"  MovieCredit         {credits}");
            w.WriteLine($"  MoviePlotSummary    {plots}");
            w.WriteLine($"  MoviePosterDetails  {posters}");
            w.WriteLine($"  MediaFile (stray)   {files}");
            w.WriteLine($"  Playable (fileless) {playables}");
            w.WriteLine($"  Viewing.MovieID nulled (SeriesId preserved)  {viewings}");
            w.WriteLine($"  (Series rows {series} and Episodes {episodes} are NOT touched.)");

            // Preconditions — abort rather than risk loss.
            int orphanTwins = await Count("SELECT COUNT(*) AS Value FROM Movie m WHERE m.TitleType IN (4,5) AND NOT EXISTS(SELECT 1 FROM Series s WHERE s.Id=m.id)");
            int badViewings = await Count("SELECT COUNT(*) AS Value FROM Viewing v WHERE v.SeriesId IS NULL AND EXISTS(SELECT 1 FROM Movie m WHERE m.id=v.MovieID AND m.TitleType IN (4,5))");
            if (orphanTwins > 0) { console.Error.WriteLine($"ABORT: {orphanTwins} twin(s) have no Series row — their data would be lost."); return; }
            if (badViewings > 0) { console.Error.WriteLine($"ABORT: {badViewings} twin viewing(s) have no SeriesId — would lose the watch/want."); return; }

            if (!Apply) { w.WriteLine("\nDRY RUN — re-run with --apply to execute (snapshots + transactional)."); return; }

            // ── apply, in one transaction ──
            var steps = new (string label, string sql)[]
            {
                ("snapshot Movie",      "IF OBJECT_ID('dbo._flip_backup_movie') IS NOT NULL DROP TABLE dbo._flip_backup_movie; SELECT * INTO dbo._flip_backup_movie FROM Movie WHERE TitleType IN (4,5);"),
                ("snapshot MovieGenre", $"IF OBJECT_ID('dbo._flip_backup_moviegenre') IS NOT NULL DROP TABLE dbo._flip_backup_moviegenre; SELECT * INTO dbo._flip_backup_moviegenre FROM MovieGenre WHERE MovieID IN ({TWINS});"),
                ("snapshot MovieCredit",$"IF OBJECT_ID('dbo._flip_backup_moviecredit') IS NOT NULL DROP TABLE dbo._flip_backup_moviecredit; SELECT * INTO dbo._flip_backup_moviecredit FROM MovieCredit WHERE MovieID IN ({TWINS});"),
                ("snapshot MoviePlot",  $"IF OBJECT_ID('dbo._flip_backup_movieplot') IS NOT NULL DROP TABLE dbo._flip_backup_movieplot; SELECT * INTO dbo._flip_backup_movieplot FROM MoviePlotSummary WHERE MovieID IN ({TWINS});"),
                ("snapshot MoviePoster",$"IF OBJECT_ID('dbo._flip_backup_movieposter') IS NOT NULL DROP TABLE dbo._flip_backup_movieposter; SELECT * INTO dbo._flip_backup_movieposter FROM MoviePosterDetails WHERE MovieId IN ({TWINS});"),
                ("snapshot MediaFile",  "IF OBJECT_ID('dbo._flip_backup_mediafile') IS NOT NULL DROP TABLE dbo._flip_backup_mediafile; SELECT * INTO dbo._flip_backup_mediafile FROM MediaFile WHERE PlayableId IN (SELECT PlayableId FROM Movie WHERE TitleType IN (4,5) AND PlayableId IS NOT NULL);"),
                ("snapshot Playable",   "IF OBJECT_ID('dbo._flip_backup_playable') IS NOT NULL DROP TABLE dbo._flip_backup_playable; SELECT * INTO dbo._flip_backup_playable FROM Playable WHERE Id IN (SELECT PlayableId FROM Movie WHERE TitleType IN (4,5) AND PlayableId IS NOT NULL);"),
                ("snapshot Viewing",    $"IF OBJECT_ID('dbo._flip_backup_viewing') IS NOT NULL DROP TABLE dbo._flip_backup_viewing; SELECT * INTO dbo._flip_backup_viewing FROM Viewing WHERE MovieID IN ({TWINS});"),

                ("drop CASCADE FK Episode->Movie", "IF EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_Episode_Movie_SeriesMovieId') ALTER TABLE Episode DROP CONSTRAINT FK_Episode_Movie_SeriesMovieId;"),

                ("null twin viewings",  $"UPDATE Viewing SET MovieID=NULL WHERE SeriesId IS NOT NULL AND MovieID IN ({TWINS});"),
                ("delete MoviePoster",  $"DELETE FROM MoviePosterDetails WHERE MovieId IN ({TWINS});"),
                ("delete MovieGenre",   $"DELETE FROM MovieGenre WHERE MovieID IN ({TWINS});"),
                ("delete MovieCredit",  $"DELETE FROM MovieCredit WHERE MovieID IN ({TWINS});"),
                ("delete MoviePlot",    $"DELETE FROM MoviePlotSummary WHERE MovieID IN ({TWINS});"),
                ("delete stray MediaFile","DELETE FROM MediaFile WHERE PlayableId IN (SELECT PlayableId FROM Movie WHERE TitleType IN (4,5) AND PlayableId IS NOT NULL);"),
                ("delete Movie twins",  "DELETE FROM Movie WHERE TitleType IN (4,5);"),
                // Leave Playables still referenced by a channel schedule (NO_ACTION FK) as harmless orphans;
                // 3 series were scheduled on a TV channel. All 244 Movie twins still delete regardless.
                ("delete orphan Playables", "DELETE FROM Playable WHERE Id IN (SELECT PlayableId FROM dbo._flip_backup_movie WHERE PlayableId IS NOT NULL) AND NOT EXISTS(SELECT 1 FROM Movie m WHERE m.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM MiscVideo v WHERE v.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM Episode e WHERE e.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM ChannelScheduleItem c WHERE c.PlayableId=Playable.Id);"),
            };

            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                foreach (var (label, sql) in steps)
                {
                    await db.Database.ExecuteSqlRawAsync(sql);
                    w.WriteLine($"  ✓ {label}");
                }

                int twinsAfter = await Count("SELECT COUNT(*) AS Value FROM Movie WHERE TitleType IN (4,5)");
                int epAfter = await Count("SELECT COUNT(*) AS Value FROM Episode");
                int seriesAfter = await Count("SELECT COUNT(*) AS Value FROM Series");
                if (twinsAfter != 0) throw new Exception($"twins remain ({twinsAfter})");
                if (epAfter != episodes) throw new Exception($"episode count changed {episodes} -> {epAfter} (cascade?!)");
                if (seriesAfter != series) throw new Exception($"series count changed {series} -> {seriesAfter}");

                await tx.CommitAsync();
                w.WriteLine($"\nFLIP COMMITTED. Twins {twins} -> 0. Episodes {episodes} intact, Series {series} intact.");
                w.WriteLine("Restore tables: _flip_backup_*. The orphaned Episode.SeriesMovieId column remains (drop in a later deploy).");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                console.Error.WriteLine($"\nROLLED BACK - {ex.Message}. No changes committed.");
            }
        }

        // Post-flip cleanup of orphan twin Playables the flip left behind because a (stale, past) channel
        // schedule item still referenced them (NO_ACTION FK). Drops those schedule items (snapshotted) then
        // the now-unreferenced Playables. Transactional. Reads the _flip_backup_movie restore table.
        private const string ORPH = "SELECT PlayableId FROM dbo._flip_backup_movie WHERE PlayableId IS NOT NULL";

        private async Task FinishOrphansAsync(IConsole console, MovieDb db, Func<string, Task<int>> count)
        {
            var w = console.Output;
            if (await count("SELECT COUNT(*) AS Value FROM sys.tables WHERE name='_flip_backup_movie'") == 0)
            { console.Error.WriteLine("No _flip_backup_movie restore table — run the flip first."); return; }

            int orphans = await count($"SELECT COUNT(*) AS Value FROM Playable WHERE Id IN ({ORPH})");
            int sched = await count($"SELECT COUNT(*) AS Value FROM ChannelScheduleItem WHERE PlayableId IN ({ORPH})");
            w.WriteLine($"orphan twin Playables left: {orphans}; stale channel schedule items on them: {sched}{(Apply ? "" : "  (DRY RUN)")}");
            if (orphans == 0) { w.WriteLine("Nothing to clean."); return; }
            if (!Apply) { w.WriteLine("DRY RUN — re-run with --finish-orphans --apply."); return; }

            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                await db.Database.ExecuteSqlRawAsync($"IF OBJECT_ID('dbo._flip_backup_scheduleitem') IS NOT NULL DROP TABLE dbo._flip_backup_scheduleitem; SELECT * INTO dbo._flip_backup_scheduleitem FROM ChannelScheduleItem WHERE PlayableId IN ({ORPH});");
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM ChannelScheduleItem WHERE PlayableId IN ({ORPH});");
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM Playable WHERE Id IN ({ORPH}) AND NOT EXISTS(SELECT 1 FROM Movie m WHERE m.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM MiscVideo v WHERE v.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM Episode e WHERE e.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM ChannelScheduleItem c WHERE c.PlayableId=Playable.Id) AND NOT EXISTS(SELECT 1 FROM MediaFile f WHERE f.PlayableId=Playable.Id);");
                int left = await count($"SELECT COUNT(*) AS Value FROM Playable WHERE Id IN ({ORPH})");
                if (left != 0) throw new Exception($"{left} orphan Playable(s) still present after cleanup");
                await tx.CommitAsync();
                w.WriteLine($"Cleaned {orphans} orphan Playable(s) + {sched} stale schedule item(s). Schedule items snapshotted to _flip_backup_scheduleitem.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                console.Error.WriteLine($"ROLLED BACK - {ex.Message}. No changes committed.");
            }
        }
    }
}
