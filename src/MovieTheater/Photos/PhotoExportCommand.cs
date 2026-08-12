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
    /// <c>photos-export</c> — dumps the curation tables as versioned JSON (docs/photos-plan.md §2.11).
    ///
    /// <para>Unlike the movie database, almost nothing here is re-derivable: person tags, hand-set
    /// dates, master picks, albums and captions are years of irreplaceable human labor, and "the DB is
    /// backed up" must not be the only answer. Cheap, safe to run on a schedule, and the closing step
    /// of any heavy curation session or the step before any risky migration.</para>
    ///
    /// <para><b>Never the NAS</b> (§2.11): exports live under the repo's <c>data/</c> convention
    /// (<c>PhotosReportDir</c>), never beside the originals, and there are no XMP sidecars — ever.</para>
    /// </summary>
    [Command("photos-export", Description = "Dump the family-album curation (people, tags, dates, albums, dupes, flags, Google mesh) to versioned JSON.")]
    public class PhotoExportCommand : BasicDICommand, ICommand
    {
        [CommandOption("out", 'o', Description = "Export directory. Default: <PhotosReportDir>/exports/<timestamp>.")]
        public string? Out { get; set; }

        [CommandOption("page-size", Description = "Rows read per page while streaming a section (default 2000).")]
        public int PageSize { get; set; } = 2000;

        [CommandOption("max-sections", Description = "Sections this invocation writes; 0 writes them all (default 0). Re-run to resume.")]
        public int MaxSections { get; set; }

        [CommandOption("sqlite", Description = "Export from this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly MovieTheaterConfiguration config;
        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoExportCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var directory = !string.IsNullOrWhiteSpace(Out)
                ? Path.GetFullPath(Out!)
                : Path.GetFullPath(Path.Combine(ReportDir(config), "exports", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));

            w.WriteLine($"export: {directory}");

            var exporter = new PhotoCurationExporter(BuildDbFactory(w), line => w.WriteLine(line), PageSize);
            var manifest = await exporter.RunAsync(directory, MaxSections);

            w.WriteLine();
            foreach (var kv in manifest.Counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                w.WriteLine($"  {kv.Key}: {kv.Value}");
            w.WriteLine(manifest.Complete
                ? $"Complete. Verify it with: photos-import --from \"{directory}\" (dry run by default)."
                : "Incomplete — re-run with the same --out to resume the remaining sections.");
        }

        internal static string ReportDir(MovieTheaterConfiguration config) =>
            !string.IsNullOrWhiteSpace(config.PhotosReportDir) ? config.PhotosReportDir! : Path.Combine("data", "photos");

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
