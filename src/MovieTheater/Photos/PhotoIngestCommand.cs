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
    /// <c>photos-ingest</c> — the family collection's read-only, chunked, resumable pipeline
    /// (docs/photos-plan.md §2.5). Four independent queues, runnable one at a time or chained:
    /// <c>walk</c> (inventory), <c>metadata</c> (EXIF + §2.7 dates), <c>hash</c> (SHA-256 + perceptual)
    /// and <c>thumb</c> (the §2.2 WebP derivatives).
    ///
    /// <para><b>Bulk-job rules.</b> Every batch is bounded (<c>--batch-size</c>, and for the byte-heavy
    /// passes <c>--max-batch-mb</c>), prints <c>{processed, remaining, nextCursor}</c>, and is safe to
    /// kill at any point: re-run with <c>--after &lt;nextCursor&gt;</c>, or with no cursor at all for
    /// the row queues, which are self-draining. <c>--max-batches</c> bounds how many chunks one
    /// invocation runs (0 = drain, with a no-progress safety break).</para>
    ///
    /// <para><b>Nothing under the collection root is ever written</b> (§6). The only writes this
    /// command performs are database rows, the derivative cache, and the ambiguous-pairing review
    /// artifact — none of them on the NAS.</para>
    ///
    /// <para><b>--sqlite</b> points the pipeline at a throwaway local file database instead of the
    /// configured connection. That option exists because the configured connection string IS the live
    /// shared production database: exercising a bulk pass end to end against real files has to be
    /// possible without pointing it at production first.</para>
    /// </summary>
    [Command("photos-ingest", Description = "Walk / read / hash / thumbnail the family photo collection in bounded, resumable batches.")]
    public class PhotoIngestCommand : BasicDICommand, ICommand
    {
        [CommandOption("pass", 'p', Description = "walk | metadata | hash | thumb | video | all (default walk).")]
        public string Pass { get; set; } = "walk";

        [CommandOption("root", 'r', Description = "Collection root. Default: PhotosLibraryDir from config.")]
        public string? Root { get; set; }

        [CommandOption("thumb-cache", Description = "Derivative cache directory. Default: PhotosThumbCacheDir from config.")]
        public string? ThumbCache { get; set; }

        [CommandOption("batch-size", Description = "Directories per walk batch, or rows per queue batch (default 50).")]
        public int BatchSize { get; set; } = 50;

        [CommandOption("max-batches", Description = "Batches this invocation runs; 0 drains the queue (default 1).")]
        public int MaxBatches { get; set; } = 1;

        [CommandOption("max-batch-mb", Description = "Byte bound per batch for the hash/thumb passes (default 2048).")]
        public int MaxBatchMb { get; set; } = 2048;

        [CommandOption("after", Description = "Resume cursor from a prior run's nextCursor (walk: a directory path; queues: an id).")]
        public string? After { get; set; }

        [CommandOption("dry-run", Description = "Walk only: report what would change and write nothing.")]
        public bool DryRun { get; set; }

        [CommandOption("retry-errors", Description = "Re-queue rows a previous run stamped with an ingest error.")]
        public bool RetryErrors { get; set; }

        [CommandOption("batch-id", Description = "IngestBatch marker stamped on rows born in this run (default: photos-<timestamp>).")]
        public string? BatchId { get; set; }

        [CommandOption("sqlite", Description = "Run against this SQLite file instead of the configured database (local exercise only).")]
        public string? Sqlite { get; set; }

        [CommandOption("ffprobe", Description = "ffprobe binary for the video pass. Default: FfprobePath from config.")]
        public string? Ffprobe { get; set; }

        [CommandOption("ffmpeg", Description = "ffmpeg binary for video poster frames. Default: FfmpegPath from config.")]
        public string? Ffmpeg { get; set; }

        [CommandOption("video-timeout-seconds", Description = "Hard ceiling per ffprobe/ffmpeg invocation; it is KILLED past it (default 60).")]
        public int VideoTimeoutSeconds { get; set; } = 60;

        private readonly MovieTheaterConfiguration config;
        private readonly IDbContextFactory<MovieDb> dbFactory;

        public PhotoIngestCommand(MovieTheaterConfiguration config) : base(config)
        {
            this.config = config;
            // Resolved eagerly like every other command here; unused when --sqlite is given, and
            // deliberately never opened in that case.
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;

            var rootSetting = !string.IsNullOrWhiteSpace(Root) ? Root : config.PhotosLibraryDir;
            if (string.IsNullOrWhiteSpace(rootSetting))
            {
                w.WriteLine("No photo root: pass --root or set PhotosLibraryDir in config.");
                return;
            }
            var root = Path.GetFullPath(rootSetting!);
            if (!System.IO.Directory.Exists(root)) { w.WriteLine($"Photo root not found: {root}"); return; }

            var passes = ParsePasses(Pass);
            if (passes.Count == 0)
            {
                w.WriteLine($"Unknown --pass '{Pass}'. Use walk, metadata, hash, thumb, video or all.");
                return;
            }

            var options = new PhotoIngestOptions
            {
                Root = root,
                ThumbCacheDir = !string.IsNullOrWhiteSpace(ThumbCache) ? Path.GetFullPath(ThumbCache!)
                    : (!string.IsNullOrWhiteSpace(config.PhotosThumbCacheDir) ? Path.GetFullPath(config.PhotosThumbCacheDir!) : null),
                ReportDir = !string.IsNullOrWhiteSpace(config.PhotosReportDir)
                    ? config.PhotosReportDir
                    : Path.Combine("data", "photos"),
                HomeTimeZone = config.PhotosHomeTimeZone,
                BatchSize = BatchSize,
                MaxBatchBytes = Math.Max(1, MaxBatchMb) * 1024L * 1024L,
                DryRun = DryRun,
                RetryErrors = RetryErrors,
                IngestBatch = !string.IsNullOrWhiteSpace(BatchId)
                    ? BatchId!
                    : "photos-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
            };

            if (passes.Contains(PhotoIngestPass.Thumb) && string.IsNullOrWhiteSpace(options.ThumbCacheDir))
            {
                w.WriteLine("The thumb pass needs a cache directory: pass --thumb-cache or set PhotosThumbCacheDir.");
                return;
            }

            if (passes.Contains(PhotoIngestPass.Video))
            {
                // Read-only external binaries, bounded and killed on timeout (§2.3). A host without them
                // is a normal host: the pass says so and leaves the videos deferred, which is exactly
                // the state Phase 1 put them in.
                var tools = new FfmpegVideoTools(
                    !string.IsNullOrWhiteSpace(Ffprobe) ? Ffprobe : config.FfprobePath,
                    !string.IsNullOrWhiteSpace(Ffmpeg) ? Ffmpeg : config.FfmpegPath,
                    TimeSpan.FromSeconds(Math.Clamp(VideoTimeoutSeconds, 5, 900)),
                    line => w.WriteLine(line));
                options.VideoTools = tools;
                if (!tools.Available)
                    w.WriteLine("No ffprobe configured (FfprobePath / --ffprobe) — the video pass will report and change nothing.");
                else if (!tools.CanGrabFrames)
                    w.WriteLine("No ffmpeg configured (FfmpegPath / --ffmpeg) — durations and dimensions only, no poster frames.");
            }

            var pipeline = new PhotoIngestPipeline(BuildDbFactory(w), options, line => w.WriteLine(line));

            w.WriteLine($"root: {root}");
            w.WriteLine($"batch: {options.IngestBatch}" + (DryRun ? "  (DRY RUN — nothing will be written)" : ""));

            foreach (var pass in passes)
            {
                w.WriteLine();
                w.WriteLine($"── {pass} ──");
                // The cursor only belongs to the FIRST pass of a chained run: --after "3400" means
                // nothing to the walk, and a directory path means nothing to a row queue.
                var cursor = pass == passes[0] ? After : null;
                var total = await pipeline.RunAsync(pass, cursor, MaxBatches);
                var counts = total.CountsText();
                w.WriteLine($"{pass}: {total.Processed} processed, {total.Remaining} remaining"
                            + (counts.Length > 0 ? $"  [{counts}]" : ""));
                if (total.Remaining > 0)
                    w.WriteLine($"More to do: re-run --pass {pass.ToString().ToLowerInvariant()}"
                                + (pass == PhotoIngestPass.Walk ? $" --after \"{total.NextCursor}\"" : ""));
            }

            if (DryRun) w.WriteLine("\nDRY RUN — nothing was written.");
        }

        private static List<PhotoIngestPass> ParsePasses(string value)
        {
            // Video LAST: it is the only pass that needs external binaries, so a host without them
            // still drains everything before it rather than stopping at a missing dependency.
            var all = new[]
            {
                PhotoIngestPass.Walk, PhotoIngestPass.Metadata, PhotoIngestPass.Hash,
                PhotoIngestPass.Thumb, PhotoIngestPass.Video,
            };
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return all.ToList();

            var result = new List<PhotoIngestPass>();
            foreach (var part in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse<PhotoIngestPass>(part.Trim(), ignoreCase: true, out var pass)
                    || !Enum.IsDefined(typeof(PhotoIngestPass), pass))
                    return new List<PhotoIngestPass>();
                result.Add(pass);
            }
            return result;
        }

        /// <summary>
        /// Where rows go. <c>--sqlite</c> builds a self-contained file database (created on first use);
        /// otherwise the configured pooled factory, which on this repo's dev machine is the LIVE SHARED
        /// database — hence the explicit opt-in for the local lane rather than a silent fallback.
        /// </summary>
        private Func<MovieDb> BuildDbFactory(ConsoleWriter w)
        {
            if (string.IsNullOrWhiteSpace(Sqlite)) return () => dbFactory.CreateDbContext();

            var file = Path.GetFullPath(Sqlite!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var sqliteOptions = new DbContextOptionsBuilder<MovieDb>()
                .UseSqlite("Data Source=" + file)
                .Options;
            using (var seed = new MovieDb(sqliteOptions)) seed.Database.EnsureCreated();
            w.WriteLine($"sqlite: {file}");
            return () => new MovieDb(sqliteOptions);
        }
    }
}
