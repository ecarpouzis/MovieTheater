using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Books;
using MovieTheater.Books.Db;
using MovieTheater.Books.Providers;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-isbn-enrich</c> — fetch Open Library subjects for every NOVEL that carries an ISBN, into the
    /// legs warehouse.
    ///
    /// <para>The verb IS the loop, like every other chunked job here: it repeats
    /// <see cref="IsbnEnrichScraper.RunBatchAsync"/>, prints what each chunk did, and stops when the cursor
    /// stops moving or <c>--max-batches</c> is reached. Nothing in the hot file changes except the cursor —
    /// run <c>books-resolve --tags</c> afterwards to fold the new rows onto items.</para>
    /// </summary>
    [Command("books-isbn-enrich", Description = "Fetch Open Library subjects by ISBN for books into books-legs.db (then run books-resolve --tags).")]
    public class BooksIsbnEnrichCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksIsbnEnrichCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath) — the only file written.")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Books per chunk (default 1000; each chunk is ~10 requests).")] public int BatchSize { get; set; } = 1000;
        [CommandOption("max-batches", Description = "Stop after this many chunks (default 0 = run to the end).")] public int MaxBatches { get; set; }
        [CommandOption("dry-run", Description = "Ask nothing and write nothing; report how much is outstanding.")] public bool DryRun { get; set; }
        [CommandOption("reset", Description = "Rewind the cursor to 0 before starting.")] public bool Reset { get; set; }
        [CommandOption("interval-ms", Description = "Pause between live requests (default 1000).")] public int IntervalMs { get; set; } = 1000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var legsPath = LegsDbPath ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required.");
            if (!File.Exists(legsPath)) throw new CommandException($"Not found: {legsPath}", 2);

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            services.AddDbContext<BooksDb>(o => BooksDbOptions.Configure(o, dbPath));
            services.AddSingleton(new ProviderCacheStore(legsPath));
            services.AddHttpClient<IsbnEnrichScraper>();
            await using var provider = services.BuildServiceProvider();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();
            var scraper = scope.ServiceProvider.GetRequiredService<IsbnEnrichScraper>();
            scraper.MinRequestInterval = TimeSpan.FromMilliseconds(Math.Clamp(IntervalMs, 0, 60_000));

            if (Reset && !DryRun)
            {
                var row = db.SystemStates.FirstOrDefault(s => s.Key == IsbnEnrichScraper.CursorKey);
                if (row != null) { row.Value = "0"; await db.SaveChangesAsync(console.RegisterCancellationHandler()); }
            }

            long processed = 0, fetched = 0, noMatch = 0, held = 0, failed = 0;
            var batches = 0;
            var ct = console.RegisterCancellationHandler();

            while (true)
            {
                var r = await scraper.RunBatchAsync(db, BatchSize, apply: !DryRun, ct);
                await console.Output.WriteLineAsync(r.ToString());
                processed += r.Processed; fetched += r.Fetched; noMatch += r.NoMatch;
                held += r.AlreadyHeld; failed += r.Failed;

                if (r.Done) break;
                if (++batches >= MaxBatches && MaxBatches > 0) break;

                // A dry run must not spin: it never advances the cursor, so one chunk is the whole report.
                if (DryRun) break;
            }

            await console.Output.WriteLineAsync(
                $"done: processed {processed}, fetched {fetched}, noMatch {noMatch}, alreadyHeld {held}, failed {failed}");
            if (!DryRun && fetched > 0)
                await console.Output.WriteLineAsync("next: books-resolve --tags   (folds the new editions onto items)");
        }
    }
}
