using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Books;
using MovieTheater.Books.Archives;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-thumbs</c> — generate the MISSING cover thumbnails, a bounded batch at a time.
    ///
    /// <para>There is one mode and it is <c>--missing</c> (the default and only behaviour; the flag exists so the
    /// intent is spelled at the call site). "Regenerate all" is not a mode: a rebuild is a delete pass followed
    /// by this one, so the work stays countable and resumable. Ids were preserved by the migration, so the
    /// thumbnails the standalone site already generated are valid and this verb has nothing to do for them.</para>
    ///
    /// <para>This VERB is the driver: it loops batches and accumulates totals, stopping on a batch that moves no
    /// cursor. The job itself does a bounded amount per call and commits its cursor with its writes, so killing
    /// this process anywhere costs at most one batch — re-run it and it continues. <c>--max-batches</c> bounds a
    /// single invocation for exactly that reason.</para>
    /// </summary>
    [Command("books-thumbs", Description = "Generate missing cover thumbnails (chunked, resumable).")]
    public class BooksThumbsCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksThumbsCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("cache-dir", Description = "Thumbnail cache root (default Books:CacheDir).")] public string? CacheDir { get; set; }
        [CommandOption("missing", Description = "Generate the missing thumbnails. The only mode; default true.")] public bool Missing { get; set; } = true;
        [CommandOption("batch-size", Description = "Items per batch (default 200).")] public int BatchSize { get; set; } = ThumbnailJob.DefaultBatchSize;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = until done).")] public int MaxBatches { get; set; }
        [CommandOption("reset", Description = "Forget the saved cursor and start from the first item.")] public bool Reset { get; set; }
        [CommandOption("status", Description = "Print the saved progress and exit without doing work.")] public bool Status { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!Missing) throw new CommandException("--missing is the only mode; \"regenerate all\" is delete-then-generate-missing.");
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var cacheDir = CacheDir ?? config.CacheDir ?? throw new CommandException("--cache-dir or Books:CacheDir is required.");

            await using var provider = BuildProvider(dbPath, cacheDir);
            var job = provider.GetRequiredService<ThumbnailJob>();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();

            if (Reset)
            {
                await job.ResetAsync(db);
                await console.Output.WriteLineAsync("cursor reset");
            }

            if (Status)
            {
                var s = await job.StatusAsync(db);
                await console.Output.WriteLineAsync(
                    $"{{ cursor: {s.Cursor}, processed: {s.Processed}, generated: {s.Generated}, skipped: {s.Skipped}, failed: {s.Failed}, remaining: {s.Remaining} }}");
                return;
            }

            long totalProcessed = 0, totalGenerated = 0, totalSkipped = 0, totalFailed = 0;
            var batches = 0;
            while (MaxBatches <= 0 || batches < MaxBatches)
            {
                var result = await job.RunBatchAsync(db, BatchSize);
                batches++;
                totalProcessed += result.Processed;
                totalGenerated += result.Generated;
                totalSkipped += result.Skipped;
                totalFailed += result.Failed;

                await console.Output.WriteLineAsync(
                    $"{{ processed: {result.Processed}, remaining: {result.Remaining}, nextCursor: \"{result.NextCursor}\", failed: {result.Failed} }}" +
                    $"  [generated: {totalGenerated}, skipped: {totalSkipped}, batches: {batches}]");

                // The no-progress safety break: a batch that moved nothing is the end of the run, or a defect —
                // either way, looping again would spin.
                if (result.Done) break;
            }

            await console.Output.WriteLineAsync(
                $"done: processed {totalProcessed}, generated {totalGenerated}, skipped {totalSkipped}, failed {totalFailed} over {batches} batch(es)");
        }

        /// <summary>
        /// The minimum service graph the job needs — the readers, the thumbnail service and one
        /// <see cref="BooksDb"/> — built here rather than by <c>AddBooks</c> so a CLI run brings up no web stack,
        /// no controllers and no cache warmer.
        /// </summary>
        private ServiceProvider BuildProvider(string dbPath, string cacheDir)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(new BooksOptions
            {
                DbPath = dbPath,
                CacheDir = cacheDir,
                ThumbnailQuality = config.ThumbnailQuality,
                SevenZipPath = config.SevenZipPath,
                ArchiveCacheGb = 0,   // a one-shot batch job gains nothing from warming the whole-archive cache
            });
            services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, dbPath));
            services.AddSingleton<SevenZipCliExtractor>();
            services.AddSingleton<IArchiveReader, CbzArchiveReader>();
            services.AddSingleton<IArchiveReader, CbrArchiveReader>();
            services.AddSingleton<IArchiveReader, PdfArchiveReader>();
            services.AddSingleton<IArchiveReader, MobiArchiveReader>();
            // The EPUB reader needs the shared memory cache for its extracted-image lists.
            services.AddMemoryCache(o => o.SizeLimit = 512);
            services.AddSingleton<IArchiveReader, EpubArchiveReader>();
            services.AddSingleton<ThumbnailService>();
            services.AddSingleton<ThumbnailJob>();
            return services.BuildServiceProvider();
        }
    }
}
