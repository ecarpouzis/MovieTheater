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

namespace MovieTheater.Photos
{
    /// <summary>
    /// <c>photos-shelf</c> — files a subtree onto the Gallery shelf (or back onto the timeline) by
    /// root-relative path prefix, optionally gathering it into an album (docs/photos-plan.md §2.12).
    ///
    /// <para>The art and meme piles §1 catalogued are identified by WHERE THEY ARE and by nothing on the
    /// row — no heuristic distinguishes a painting from a photograph of a wall. So the operator names
    /// the folder and this files it: one rule per collection, each expressible as one command line.</para>
    ///
    /// <para>Chunked, resumable and idempotent like every pass in this vertical; see
    /// <see cref="PhotoShelfPass"/> for the contract. Nothing under the collection root is read, opened,
    /// written, renamed or moved (§6) — this is an int on a row.</para>
    /// </summary>
    [Command("photos-shelf", Description = "File a subtree onto the Gallery shelf (or back), optionally as an album. Chunked, resumable, idempotent.")]
    public class PhotoShelfCommand : BasicDICommand, ICommand
    {
        [CommandOption("path-prefix", Description = "Root-relative folder prefix to file, forward slashes. Required.")]
        public string? PathPrefix { get; set; }

        [CommandOption("exclude-prefix", Description = "Subtree to carve back out. Repeatable.")]
        public IReadOnlyList<string> ExcludePrefixes { get; set; } = Array.Empty<string>();

        [CommandOption("shelf", Description = "Target shelf: archive (the Gallery) or timeline (the family record). Default archive.")]
        public string Shelf { get; set; } = "archive";

        [CommandOption("album", Description = "Create-or-find an album with this title and add every match to it.")]
        public string? Album { get; set; }

        [CommandOption("artist", Description = "Makes --album an artist collection under this name. Needs --album.")]
        public string? Artist { get; set; }

        [CommandOption("hide", Description = "Also set Hidden on the matches (admin-only visibility). One-directional.")]
        public bool Hide { get; set; }

        [CommandOption("dry-run", Description = "Report the real counts and write nothing.")]
        public bool DryRun { get; set; }

        [CommandOption("batch-size", Description = "Rows per batch (default 500).")]
        public int BatchSize { get; set; } = 500;

        [CommandOption("max-batches", Description = "Batches this invocation runs; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor (an asset id) from a prior run's nextCursor.")]
        public string? After { get; set; }

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoShelfCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            if (string.IsNullOrWhiteSpace(PathPrefix))
            {
                w.WriteLine("--path-prefix is required. It is the whole of the rule, and defaulting it to the");
                w.WriteLine("collection root would file the entire family album onto one shelf.");
                return;
            }
            if (!Enum.TryParse<PhotoShelf>(Shelf, ignoreCase: true, out var shelf))
            {
                w.WriteLine($"Unknown --shelf \"{Shelf}\". Known shelves: archive, timeline.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(Artist) && string.IsNullOrWhiteSpace(Album))
            {
                w.WriteLine("--artist needs --album: an artist names a COLLECTION, and there is nowhere else to put it.");
                return;
            }

            var options = new PhotoShelfPass.Options
            {
                PathPrefix = PathPrefix!,
                ExcludePrefixes = ExcludePrefixes.ToList(),
                Shelf = shelf,
                AlbumTitle = Album,
                ArtistName = Artist,
                Hide = Hide,
                DryRun = DryRun,
            };

            w.WriteLine($"prefix: {PhotoShelfPass.NormalizePrefix(options.PathPrefix)}");
            foreach (var exclude in options.ExcludePrefixes)
                w.WriteLine($"exclude: {PhotoShelfPass.NormalizePrefix(exclude)}");
            w.WriteLine($"shelf: {shelf}");
            if (Hide) w.WriteLine("hide: on — matches become admin-only everywhere.");
            if (DryRun) w.WriteLine("DRY RUN — counts are real, nothing is written.");
            w.WriteLine("Nothing under the collection root is read or written; this pass is columns only.");
            w.WriteLine();

            var pass = new PhotoShelfPass(BuildDbFactory(w), options, BatchSize, line => w.WriteLine(line));
            var total = await pass.RunAsync(After, MaxBatches);

            var counts = total.CountsText();
            w.WriteLine();
            w.WriteLine($"matched {total.Processed}, {total.Remaining} remaining" + (counts.Length > 0 ? $"  [{counts}]" : ""));
            if (total.Remaining > 0)
                w.WriteLine($"More to do: re-run the same command with --after {total.NextCursor}");
            else if (DryRun)
                w.WriteLine("Dry run complete. Re-run without --dry-run to apply it.");
            else
                w.WriteLine("Done. Re-running this exact command changes nothing.");
        }

        /// <summary>Same explicit local lane as every other pass here: the configured connection string
        /// is the live shared database, so exercising this end to end has to be possible without
        /// pointing it there.</summary>
        private Func<MovieDb> BuildDbFactory(ConsoleWriter w)
        {
            if (string.IsNullOrWhiteSpace(Sqlite)) return () => dbFactory.CreateDbContext();

            var file = Path.GetFullPath(Sqlite!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sqliteOptions = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + file).Options;
            using (var seed = new MovieDb(sqliteOptions)) seed.Database.EnsureCreated();
            w.WriteLine($"sqlite: {file}");
            return () => new MovieDb(sqliteOptions);
        }
    }
}
