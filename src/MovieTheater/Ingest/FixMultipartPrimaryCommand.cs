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
    /// One-time data fix: a multi-part / lone-part movie that ended up with files but NO
    /// <see cref="MovieFileRole.Primary"/> (every file tagged <see cref="MovieFileRole.Part"/>) is
    /// unplayable — the movie sync pass keys off <see cref="Movie.FilePath"/>, which is only ever set
    /// from a Primary, so such a movie is skipped entirely and shows "not synced — unplayable".
    ///
    /// For each affected movie-kind Playable, promotes the first part (lowest PartNumber, then file id)
    /// to Primary and clears its PartNumber, leaving the remaining parts ordered. Also sets
    /// <see cref="Movie.FilePath"/> from that Primary when empty so the movie pass picks it up. Idempotent
    /// (movies that already have a Primary are untouched). Dry-run by default.
    /// </summary>
    [Command("fix-multipart-primary", Description = "Promote the first part to Primary for movies that have Part files but no Primary. Dry-run by default.")]
    public class FixMultipartPrimaryCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write the changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public FixMultipartPrimaryCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            using var db = await dbFactory.CreateDbContextAsync();

            // Movie-kind playables that have files but no Primary among them.
            var noPrimaryPids = await (
                from f in db.MediaFiles
                join p in db.Playables on f.PlayableId equals p.Id
                where p.Kind == PlayableKind.Movie
                group f by f.PlayableId into g
                where g.All(x => x.Role != MovieFileRole.Primary)
                select g.Key).ToListAsync();

            w.WriteLine($"Movie playables with files but no Primary: {noPrimaryPids.Count}{(Apply ? "" : "   (dry run)")}");
            if (noPrimaryPids.Count == 0) return;

            var files = await db.MediaFiles.Where(f => noPrimaryPids.Contains(f.PlayableId)).ToListAsync();
            var filesByPlayable = files.GroupBy(f => f.PlayableId).ToDictionary(g => g.Key, g => g.ToList());
            var movieByPlayable = await db.Movies
                .Where(m => m.PlayableId != null && noPrimaryPids.Contains(m.PlayableId.Value))
                .ToDictionaryAsync(m => m.PlayableId!.Value);

            int promoted = 0, filePathSet = 0;
            foreach (var pid in noPrimaryPids)
            {
                var pool = filesByPlayable[pid];
                // Prefer a real Part; fall back to any file (covers a lone Variant/Extra-only oddity).
                var first = pool.Where(f => f.Role == MovieFileRole.Part)
                                .OrderBy(f => f.PartNumber ?? int.MaxValue).ThenBy(f => f.Id).FirstOrDefault()
                            ?? pool.OrderBy(f => f.Id).First();

                var movie = movieByPlayable.GetValueOrDefault(pid);
                var label = movie != null ? $"{movie.id} '{movie.Title}'" : $"playable {pid}";
                w.WriteLine($"  {label}: {pool.Count} file(s) → Primary = [{first.Path}]" +
                            (movie != null && string.IsNullOrEmpty(movie.FilePath) ? "  (+ set FilePath)" : ""));

                if (Apply)
                {
                    first.Role = MovieFileRole.Primary;
                    first.PartNumber = null;
                    promoted++;
                    if (movie != null && string.IsNullOrEmpty(movie.FilePath)) { movie.FilePath = first.Path; filePathSet++; }
                }
            }

            if (Apply)
            {
                await db.SaveChangesAsync();
                w.WriteLine($"\nPromoted {promoted} files to Primary; set Movie.FilePath on {filePathSet}. Run sync-jellyfin next to stamp Jellyfin ids.");
            }
            else
            {
                w.WriteLine($"\nDry run — re-run with --apply to write. Then run sync-jellyfin to make them streamable.");
            }
        }
    }
}
