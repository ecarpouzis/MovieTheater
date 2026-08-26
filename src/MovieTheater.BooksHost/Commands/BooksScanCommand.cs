using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-scan</c> — walk the library share and reconcile the catalog with it.
    ///
    /// <para><b>Dry run is the default.</b> Without <c>--apply</c> the verb counts what a scan WOULD add, change
    /// and remove and writes nothing. A pass that can delete rows says how many first — every time, not just the
    /// first time.</para>
    ///
    /// <para>This VERB is the driver: it loops the scanner's bounded batches and stops on one that moves no
    /// cursor. The job commits its cursor with its writes, so killing this process costs at most one batch and
    /// re-running continues from there. <c>--max-batches</c> bounds one invocation for exactly that reason.</para>
    /// </summary>
    [Command("books-scan", Description = "Scan the library roots and reconcile the catalog (chunked, resumable; dry-run by default).")]
    public class BooksScanCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksScanCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("root", Description = "Scan only this LibraryRoot id (default: every enabled root).")] public int? RootId { get; set; }
        [CommandOption("batch-size", Description = "Folders (or files/items) per batch (default 200).")] public int BatchSize { get; set; } = LibraryScanner.DefaultBatchSize;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = until done).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only reports what would change.")] public bool Apply { get; set; }
        [CommandOption("resume", Description = "Continue the run already in progress instead of starting a new one.")] public bool Resume { get; set; }
        [CommandOption("status", Description = "Print the saved progress and exit without doing work.")] public bool Status { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            await using var provider = CommandServices.Build(config, dbPath);
            var scanner = provider.GetRequiredService<LibraryScanner>();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();

            if (Status)
            {
                var s = await scanner.StatusAsync(db);
                await console.Output.WriteLineAsync(s.ToString());
                return;
            }

            if (!Apply)
            {
                try
                {
                    var preview = await scanner.PreviewAsync(db, RootId);
                    await console.Output.WriteLineAsync(preview.ToString());
                    await console.Output.WriteLineAsync("dry run: nothing written. Re-run with --apply.");
                    return;
                }
                catch (InvalidOperationException ex) { throw new CommandException(ex.Message, 2); }
            }

            if (!Resume)
            {
                try { await scanner.StartAsync(db, RootId); }
                catch (InvalidOperationException ex) { throw new CommandException(ex.Message, 2); }
            }

            long added = 0, changed = 0, removed = 0, failed = 0;
            var batches = 0;
            while (MaxBatches <= 0 || batches < MaxBatches)
            {
                ScanBatchResult result;
                try { result = await scanner.RunBatchAsync(db, BatchSize, apply: true); }
                catch (InvalidOperationException ex) { throw new CommandException(ex.Message, 2); }

                batches++;
                added += result.Added; changed += result.Changed; removed += result.Removed; failed += result.Failed;
                await console.Output.WriteLineAsync(result.ToString() + $"  [batches: {batches}]");
                if (result.Done) break;
            }

            await console.Output.WriteLineAsync(
                $"done: added {added}, changed {changed}, removed {removed}, failed {failed} over {batches} batch(es)");
            await console.Output.WriteLineAsync("next: books-resolve --series, then books-resolve");
        }
    }
}
