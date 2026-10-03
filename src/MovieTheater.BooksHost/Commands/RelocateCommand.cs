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
    /// <c>books-relocate</c> — re-point existing items at the files that now hold them (moves, renames, new rips of
    /// the same book), keeping every row keyed by the item id. See <see cref="RelocationService"/>. Run it BEFORE
    /// <c>books-scan</c>, which would otherwise index each moved file as a new item and mark the old one missing.
    /// Dry run by default; the verb is the driver; <c>--after</c> continues from a line.
    /// </summary>
    [Command("books-relocate", Description = "Re-point items at their moved/replaced files from a reviewed TSV (ItemId, NewPath). Dry run by default.")]
    public class BooksRelocateCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksRelocateCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("in", Description = "The reviewed TSV (header must name ItemId and NewPath).")] public string In { get; set; } = "";
        [CommandOption("cache-dir", Description = "Thumbnail cache (default Books:CacheDir).")] public string? CacheDir { get; set; }
        [CommandOption("batch-size", Description = "Lines per batch (default 200).")] public int BatchSize { get; set; } = RelocationService.DefaultBatchSize;
        [CommandOption("after", Description = "Resume after this 1-based line number (the last nextCursor).")] public long After { get; set; }
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = until done).")] public int MaxBatches { get; set; }
        [CommandOption("report", Description = "Write refused / would-do lines here (TSV).")] public string? Report { get; set; }
        [CommandOption("journal", Description = "Append applied moves here (TSV, the undo record). Required with --apply.")] public string? Journal { get; set; }
        [CommandOption("refresh", Description = "Re-read each listed file in place even when its size and mtime are unchanged (page count, ComicInfo facts, thumbnail error) — for a reader fix; NewPath = the current path. Curated identity is kept.")] public bool Refresh { get; set; }
        [CommandOption("apply", Description = "Actually write.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            if (!File.Exists(In)) throw new CommandException($"--in {In} does not exist.");
            if (Apply && string.IsNullOrWhiteSpace(Journal)) throw new CommandException("--journal is required with --apply.");

            await using var provider = CommandServices.Build(config, dbPath, CacheDir);
            var svc = provider.GetRequiredService<RelocationService>();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();

            await using var report = Report == null ? null : new StreamWriter(Report, append: After > 0);
            StreamWriter? journal = null;
            if (Apply)
            {
                var fresh = !File.Exists(Journal!);
                journal = new StreamWriter(Journal!, append: true);
                if (fresh) await journal.WriteLineAsync(RelocationService.JournalHeader);
            }

            long after = After;
            int batches = 0, moved = 0, refreshed = 0, unchanged = 0, refused = 0, folders = 0;
            try
            {
                while (MaxBatches <= 0 || batches < MaxBatches)
                {
                    var r = await svc.RunBatchAsync(db, In, BatchSize, Apply, after, report, journal, default, Refresh);
                    if (r.Done) break;
                    batches++;
                    moved += r.Moved; refreshed += r.Refreshed; unchanged += r.Unchanged; refused += r.Refused; folders += r.FoldersCreated;
                    await console.Output.WriteLineAsync(r + $"  [batches: {batches}]");
                    if (r.NextCursor is not long n || n <= after) break;   // no-progress break
                    after = n;
                    if (Apply) db.ChangeTracker.Clear();
                }
            }
            finally
            {
                if (journal != null) await journal.DisposeAsync();
            }

            await console.Output.WriteLineAsync(
                $"done: moved {moved}, refreshed {refreshed}, unchanged {unchanged}, refused {refused}, folders created {folders} — last line {after}"
                + (Apply ? "" : " (dry run — re-run with --apply)"));
            if (Apply) await console.Output.WriteLineAsync("next: books-scan --root <id> --apply, books-thumbs, books-signatures, then the resolve chain");
        }
    }
}
