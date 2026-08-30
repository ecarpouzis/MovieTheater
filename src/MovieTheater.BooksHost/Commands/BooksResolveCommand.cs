using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-resolve</c> — rebuild the DERIVED columns (insight currency, AI tag fold, Series/Item Resolved*,
    /// then the FTS index) from the hot file's inputs. The same code the migration's resolve stage runs; the
    /// runtime jobs (R6) call it after scans and imports. Chunked by item id and safe to re-run.
    /// </summary>
    [Command("books-resolve", Description = "Rebuild the derived columns (insights, folds, Resolved*, FTS) of books.db.")]
    public class BooksResolveCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksResolveCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath) — only --tags reads it.")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per chunk (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("fts", Description = "Also rebuild ItemFts (default true).")] public bool Fts { get; set; } = true;
        [CommandOption("series", Description = "Rebuild the SERIES IDENTITY first (aliases, survivors, Item.SeriesId, merges, counts, spans) — comics THEN books.")] public bool SeriesIdentity { get; set; }
        [CommandOption("book-series", Description = "Rebuild the BOOK series links only (book: rows, Item.SeriesId for books, counts, spans) — no comic identity pass. No comic row is read or written.")] public bool BookSeries { get; set; }
        [CommandOption("tags", Description = "Also rewrite the External/MU/GCD tag folds from the legs file.")] public bool Tags { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var path = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var mapping = MappingContract.Load();
            using var hot = new TargetWriter(path, mapping, dryRun: false);

            if (SeriesIdentity)
            {
                // The identity rebuild drives its own transaction per phase (that is what makes a killed run
                // resumable), so it runs BEFORE the resolve pass opens one.
                var counts = SeriesRebuildJob.RunAll(hot, Math.Max(100, BatchSize), l => console.Output.WriteLine(l));
                await console.Output.WriteLineAsync($"series identity: {counts}");
                var diff = SeriesResolver.Diff(hot);
                await console.Output.WriteLineAsync($"series identity: recompute diff = {diff.Total} (0 = stable)");
            }

            // Books go AFTER the comic identity: that job's finish pass deletes rows and re-points items, and the
            // book counts have to be computed over the settled ids.
            if (SeriesIdentity || BookSeries)
            {
                var bookCounts = BookSeriesLinkJob.RunAll(hot, Math.Max(100, BatchSize), l => console.Output.WriteLine(l));
                await console.Output.WriteLineAsync($"book series: {bookCounts}");
            }

            if (Tags)
            {
                var legs = LegsDbPath ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required for --tags.");
                // The fold drives its own transaction per phase, like the identity rebuild.
                var fold = LegsTagFoldJob.RunAll(hot, legs, l => console.Output.WriteLine(l));
                await console.Output.WriteLineAsync($"legs tag folds: {fold}");
            }

            hot.Begin();
            var items = ResolvePipeline.RunAll(hot, Math.Max(100, BatchSize), l => console.Output.WriteLine(l));
            hot.Commit();
            await console.Output.WriteLineAsync($"resolved {items} items");
            if (!Fts) return;
            hot.Begin();
            hot.Exec(ItemFts.ClearSql);
            long cursor = 0; var total = 0;
            while (true)
            {
                cursor = FtsBuilder.IndexBatch(hot, cursor, Math.Max(100, BatchSize), out var n);
                total += n;
                await console.Output.WriteLineAsync($"{{ processed: {n}, remaining: ?, nextCursor: \"{cursor}\" }}  [fts, indexed: {total}]");
                if (n < Math.Max(100, BatchSize)) break;
            }
            hot.Exec(ItemFts.OptimizeSql);
            hot.Commit();
            await console.Output.WriteLineAsync($"fts indexed {total} items");
        }
    }
}
