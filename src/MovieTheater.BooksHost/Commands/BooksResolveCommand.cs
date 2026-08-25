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
        [CommandOption("batch-size", Description = "Items per chunk (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("fts", Description = "Also rebuild ItemFts (default true).")] public bool Fts { get; set; } = true;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var path = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var mapping = MappingContract.Load();
            using var hot = new TargetWriter(path, mapping, dryRun: false);
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
