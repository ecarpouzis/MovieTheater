using System;
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
    /// <c>photos-import</c> — reads a <c>photos-export</c> directory and reports exactly what it would
    /// create, update or skip against the current database (docs/photos-plan.md §2.11).
    ///
    /// <para><b>Dry run is the default and writing needs <c>--apply</c>.</b> §2.11 wants round-trip
    /// fidelity PROVEN before the restore is ever needed in anger, so the report is the primary product
    /// — and because the configured connection string is the live shared database, a restore lane that
    /// wrote by default would be one typo away from re-applying an old export over current curation.</para>
    ///
    /// <para>Bounded batches with <c>{processed, remaining, nextCursor}</c> per chunk, resumable from
    /// <c>--after</c>. Matching is content-hash first, relative path second, so an export re-applies
    /// after the folder churn this collection is guaranteed to see (§2.5).</para>
    /// </summary>
    [Command("photos-import", Description = "Report (or, with --apply, restore) a photos-export against the current database.")]
    public class PhotoImportCommand : BasicDICommand, ICommand
    {
        [CommandOption("from", 'f', Description = "Export directory to read. Default: the newest under <PhotosReportDir>/exports.")]
        public string? From { get; set; }

        [CommandOption("apply", Description = "Actually write the curation. Omitted = dry run, which is the default and the point.")]
        public bool Apply { get; set; }

        [CommandOption("batch-size", Description = "Items per batch (default 250).")]
        public int BatchSize { get; set; } = 250;

        [CommandOption("max-batches", Description = "Batches this invocation runs; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("after", Description = "Resume cursor (section:index) from a prior run's nextCursor.")]
        public string? After { get; set; }

        [CommandOption("sqlite", Description = "Import into this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly MovieTheaterConfiguration config;
        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoImportCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var directory = !string.IsNullOrWhiteSpace(From) ? Path.GetFullPath(From!) : NewestExport(config);
            if (directory == null || !System.IO.Directory.Exists(directory))
            {
                w.WriteLine("No export to read: pass --from <dir>.");
                return;
            }

            var importer = new PhotoCurationImporter(BuildDbFactory(w), directory, Apply, line => w.WriteLine(line), BatchSize);
            var manifest = importer.Manifest;

            w.WriteLine($"export: {directory}");
            if (manifest == null) w.WriteLine("  ! no manifest — reading whatever sections are present.");
            else w.WriteLine($"  taken {manifest.CreatedUtc:u}, version {manifest.Version}"
                             + (manifest.Complete ? "" : "  ⚠ INCOMPLETE export"));
            w.WriteLine(Apply ? "MODE: APPLY — curation rows will be written." : "MODE: DRY RUN — nothing will be written.");
            w.WriteLine();

            var report = await importer.RunAsync(After, MaxBatches);

            w.WriteLine();
            foreach (var section in PhotoCurationExportFormat.Sections)
            {
                if (!report.Sections.TryGetValue(section, out var s) || s.Examined == 0) continue;
                w.WriteLine($"{section}: {s.Summary()}");
                foreach (var example in s.Examples.Take(5)) w.WriteLine($"    · {example}");
            }

            if (!Apply) w.WriteLine("\nDRY RUN — nothing was written. Re-run with --apply to restore.");
        }

        /// <summary>The newest export directory under the configured report dir — the one a restore
        /// almost always means, and named by timestamp precisely so "newest" is decidable without
        /// opening anything.</summary>
        private static string? NewestExport(MovieTheaterConfiguration config)
        {
            var root = Path.Combine(PhotoExportCommand.ReportDir(config), "exports");
            if (!System.IO.Directory.Exists(root)) return null;
            return System.IO.Directory.EnumerateDirectories(root)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault();
        }

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
