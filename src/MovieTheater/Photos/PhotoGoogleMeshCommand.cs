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
    /// <c>photos-google-mesh</c> — mesh a Google Takeout archive against the family library
    /// (docs/photos-plan.md §2.10).
    ///
    /// <para><b>Why an archive and not an API.</b> The Google Photos Library API lost third-party read
    /// access on 2025-03-31 and the replacement Picker API is a manual, per-session selection. Takeout is
    /// the only lane left; Google can be scheduled to produce one every two months, which is the cadence
    /// this command is built around.</para>
    ///
    /// <para><b>Report-only by default, always.</b> Without <c>--download</c> this command reads the
    /// archive, writes rows and derivatives, and tells you what Google has that the library does not.
    /// The download lane is opt-in per run, is the ONE additive write to the collection host in the whole
    /// vertical (§6), refuses to run without <c>PhotosGoogleSyncDir</c>, refuses to run before the match
    /// pass has fully drained, and never overwrites an existing path.</para>
    ///
    /// <para>Chunked and resumable like every pass here: bounded work per batch,
    /// <c>{processed, remaining, nextCursor}</c> per chunk, <c>--after</c> to resume, <c>--max-batches</c>
    /// to bound one invocation.</para>
    /// </summary>
    [Command("photos-google-mesh", Description = "Mesh a Google Takeout archive against the family library; report-only unless --download.")]
    public class PhotoGoogleMeshCommand : BasicDICommand, ICommand
    {
        [CommandOption("pass", 'p', Description = "scan | match | thumbs | all (default all). The download lane is --download, never a pass name.")]
        public string Pass { get; set; } = "all";

        [CommandOption("takeout-dir", Description = "Extracted Takeout archive root. Default: PhotosGoogleTakeoutDir from config.")]
        public string? TakeoutDir { get; set; }

        [CommandOption("thumb-cache", Description = "Derivative cache directory. Default: PhotosThumbCacheDir from config.")]
        public string? ThumbCache { get; set; }

        [CommandOption("batch-size", Description = "Directories per scan batch, or rows per queue batch (default 50).")]
        public int BatchSize { get; set; } = 50;

        [CommandOption("max-batches", Description = "Batches this invocation runs per pass; 0 drains (default 0).")]
        public int MaxBatches { get; set; }

        [CommandOption("max-batch-mb", Description = "Byte bound per batch for the passes that read archive bytes (default 2048).")]
        public int MaxBatchMb { get; set; } = 2048;

        [CommandOption("after", Description = "Resume cursor from a prior run's nextCursor (applies to the FIRST pass of a chained run).")]
        public string? After { get; set; }

        [CommandOption("phash-distance", Description = "pHash Hamming threshold for the third matching rung, 0-32 (default 8 — the near lane's).")]
        public int PHashDistance { get; set; } = 8;

        [CommandOption("conflict-minutes", Description = "How far a sidecar date may sit from the local one before it counts as a disagreement (default 60).")]
        public int ConflictMinutes { get; set; } = 60;

        [CommandOption("dry-run", Description = "Report what would change and write nothing at all.")]
        public bool DryRun { get; set; }

        [CommandOption("download", Description = "OPT-IN: copy Google-only items into PhotosGoogleSyncDir. The one additive write in this vertical (§2.10).")]
        public bool Download { get; set; }

        [CommandOption("sync-dir", Description = "Download destination. Default: PhotosGoogleSyncDir from config. No built-in default exists.")]
        public string? SyncDir { get; set; }

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        private readonly MovieTheaterConfiguration config;
        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoGoogleMeshCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var takeoutSetting = !string.IsNullOrWhiteSpace(TakeoutDir) ? TakeoutDir : config.PhotosGoogleTakeoutDir;
            if (string.IsNullOrWhiteSpace(takeoutSetting))
            {
                w.WriteLine("No Takeout archive: pass --takeout-dir or set PhotosGoogleTakeoutDir in config.");
                return;
            }
            var takeout = Path.GetFullPath(takeoutSetting!);
            if (!Directory.Exists(takeout)) { w.WriteLine($"Takeout archive not found: {takeout}"); return; }

            var passes = ParsePasses(Pass);
            if (passes.Count == 0)
            {
                w.WriteLine($"Unknown --pass '{Pass}'. Use scan, match, thumbs or all.");
                return;
            }

            var options = new PhotoGoogleMeshOptions
            {
                TakeoutDir = takeout,
                SyncDir = !string.IsNullOrWhiteSpace(SyncDir) ? SyncDir : config.PhotosGoogleSyncDir,
                ThumbCacheDir = !string.IsNullOrWhiteSpace(ThumbCache) ? Path.GetFullPath(ThumbCache!)
                    : (!string.IsNullOrWhiteSpace(config.PhotosThumbCacheDir) ? Path.GetFullPath(config.PhotosThumbCacheDir!) : null),
                HomeTimeZone = config.PhotosHomeTimeZone,
                BatchSize = BatchSize,
                MaxBatchBytes = Math.Max(1, MaxBatchMb) * 1024L * 1024L,
                PHashDistance = Math.Clamp(PHashDistance, 0, 32),
                ConflictToleranceMinutes = Math.Max(0, ConflictMinutes),
                DryRun = DryRun,
            };

            if (passes.Contains(PhotoGoogleMeshPass.Thumbs) && string.IsNullOrWhiteSpace(options.ThumbCacheDir))
            {
                w.WriteLine("Google-only thumbs need a cache directory: pass --thumb-cache or set PhotosThumbCacheDir.");
                w.WriteLine("Running scan and match only.");
                passes.Remove(PhotoGoogleMeshPass.Thumbs);
            }

            w.WriteLine($"takeout: {takeout}  (READ ONLY)");
            w.WriteLine(Download
                ? "DOWNLOAD LANE ARMED — the one additive write in this vertical (§2.10). Nothing is overwritten, ever."
                : "Report-only: nothing outside the database and the derivative cache will be written.");
            if (DryRun) w.WriteLine("DRY RUN — no rows, no derivatives, no copies.");

            var factory = BuildDbFactory(w);
            var mesh = new PhotoGoogleMesh(factory, options, line => w.WriteLine(line));

            var first = passes.Count > 0 ? passes[0] : (PhotoGoogleMeshPass?)null;
            foreach (var pass in passes)
            {
                w.WriteLine();
                w.WriteLine($"── {pass} ──");
                // A cursor belongs to the FIRST pass of a chained run: a directory path means nothing to
                // a row queue, and a row id means nothing to the archive walk.
                var cursor = pass == first ? After : null;
                var total = await mesh.RunAsync(pass, cursor, MaxBatches);
                var counts = total.CountsText();
                w.WriteLine($"{pass}: {total.Processed} processed, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
                if (total.Remaining > 0)
                    w.WriteLine($"More to do: re-run --pass {pass.ToString().ToLowerInvariant()} --after \"{total.NextCursor}\"");
            }

            if (Download)
            {
                w.WriteLine();
                w.WriteLine("── download ──");
                w.WriteLine($"destination: {(string.IsNullOrWhiteSpace(options.SyncDir) ? "(unset)" : Path.GetFullPath(options.SyncDir!))}");
                var total = await mesh.RunAsync(PhotoGoogleMeshPass.Download, null, MaxBatches);
                var counts = total.CountsText();
                w.WriteLine($"download: {total.Processed} examined, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
            }

            await ReportAsync(factory, w);
        }

        /// <summary>The state of the mesh after the run — counted from the database rather than
        /// accumulated by the passes, so a resumed run reports the whole picture and not its own slice.</summary>
        private static async Task ReportAsync(Func<MovieDb> factory, ConsoleWriter w)
        {
            using var db = factory();
            var byStatus = await db.PhotoGoogleItems
                .GroupBy(i => i.Status)
                .Select(g => new { Status = g.Key, count = g.Count() })
                .ToListAsync();

            w.WriteLine();
            foreach (var row in byStatus.OrderBy(r => r.Status))
                w.WriteLine($"  {row.Status}: {row.count}");

            var byMethod = await db.PhotoGoogleItems
                .Where(i => i.MatchMethod != null)
                .GroupBy(i => i.MatchMethod!)
                .Select(g => new { method = g.Key, count = g.Count() })
                .ToListAsync();
            foreach (var row in byMethod.OrderBy(r => r.method, StringComparer.Ordinal))
                w.WriteLine($"  matched by {row.method}: {row.count}");

            var disagreements = await db.PhotoGoogleItems.CountAsync(i => i.Disagreements != null);
            if (disagreements > 0)
                w.WriteLine($"  {disagreements} item(s) disagree with the local metadata — see /photos → Review.");

            var googleOnly = byStatus.FirstOrDefault(r => r.Status == PhotoGoogleItemStatus.Unmatched)?.count ?? 0;
            var pending = byStatus.FirstOrDefault(r => r.Status == PhotoGoogleItemStatus.Pending)?.count ?? 0;
            w.WriteLine(googleOnly > 0
                ? $"{googleOnly} Google-only item(s) waiting for review at /photos → Review."
                : "Nothing is Google-only: every archive item was found in the library.");
            if (pending > 0)
                w.WriteLine($"⚠ {pending} item(s) have not been matched yet — the download lane will refuse until they are.");
        }

        private static List<PhotoGoogleMeshPass> ParsePasses(string value)
        {
            // Download is deliberately NOT reachable by name: it is a separate flag so it can never be
            // typed by accident in a list of read-only passes.
            var all = new[] { PhotoGoogleMeshPass.Scan, PhotoGoogleMeshPass.Match, PhotoGoogleMeshPass.Thumbs };
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return all.ToList();

            var result = new List<PhotoGoogleMeshPass>();
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<PhotoGoogleMeshPass>(part.Trim(), ignoreCase: true, out var pass)
                    || pass == PhotoGoogleMeshPass.Download
                    || !Enum.IsDefined(typeof(PhotoGoogleMeshPass), pass))
                    return new List<PhotoGoogleMeshPass>();
                result.Add(pass);
            }
            return result;
        }

        /// <summary>Same explicit local lane as the other photo commands: the configured connection
        /// string is the live shared database, so exercising a pass end to end has to be possible
        /// without pointing it there.</summary>
        private Func<MovieDb> BuildDbFactory(ConsoleWriter w)
        {
            if (string.IsNullOrWhiteSpace(Sqlite)) return () => dbFactory.CreateDbContext();

            var file = Path.GetFullPath(Sqlite!);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sqliteOptions = new DbContextOptionsBuilder<MovieDb>().UseSqlite("Data Source=" + file).Options;
            using (var seed = new MovieDb(sqliteOptions)) seed.Database.EnsureCreated();
            w.WriteLine($"sqlite: {file}");
            return () => new MovieDb(sqliteOptions);
        }
    }
}
