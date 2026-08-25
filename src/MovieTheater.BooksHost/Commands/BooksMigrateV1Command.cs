using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using MovieTheater.Books.Migration;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-migrate-v1</c> — the chunked, resumable copy-transform from the frozen v1 file into books.db +
    /// books-legs.db. Every invocation runs bounded work (<c>--batch-size</c> rows per batch, <c>--max-batches</c>
    /// batches; 0 drains with a no-progress break), prints <c>{ processed, remaining, nextCursor }</c> per batch,
    /// and resumes from MigrationProgress in the target. <c>--dry-run</c> reads everything and writes nothing.
    /// The v1 file is opened read-only; the live standalone site is never touched.
    /// </summary>
    [Command("books-migrate-v1", Description = "Copy-transform the frozen v1 SQLite file into books.db + books-legs.db (chunked, resumable).")]
    public class BooksMigrateV1Command : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksMigrateV1Command(BooksHostConfiguration config) => this.config = config;

        [CommandOption("source", Description = "Frozen v1 file (default Books:V1SourcePath).")] public string? Source { get; set; }
        [CommandOption("target", Description = "books.db (default Books:DbPath).")] public string? Target { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? Legs { get; set; }
        [CommandOption("calibre-link", Description = "calibre_link.json (default Books:CalibreLinkPath).")] public string? CalibreLink { get; set; }
        [CommandOption("cache-dir", Description = "Thumbnail cache root, for Folder.HasIcon (default Books:CacheDir).")] public string? CacheDir { get; set; }
        [CommandOption("report-dir", Description = "Where orphan-insights.json lands (default: beside the target).")] public string? ReportDir { get; set; }
        [CommandOption("stage", Description = "Run one stage (or one 'stage/Unit'); default all.")] public string? Stage { get; set; }
        [CommandOption("batch-size", Description = "Rows per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("max-batches", Description = "Batches this invocation runs; 0 = drain (default 0).")] public int MaxBatches { get; set; }
        [CommandOption("after", Description = "Override the first unit's cursor (a v1 rowid).")] public long? After { get; set; }
        [CommandOption("owner", Description = "The standalone site's owner username — the only user whose activity migrates (default Books:V1OwnerUsername).")] public string? Owner { get; set; }
        [CommandOption("owner-user-id", Description = "The site user id that owner becomes (default Books:OwnerUserId, else 1).")] public int? OwnerUserId { get; set; }
        [CommandOption("dry-run", Description = "Read and count; write nothing.")] public bool DryRun { get; set; }
        [CommandOption("reset", Description = "Forget the progress of the selected stage/unit (or everything) before running.")] public bool Reset { get; set; }
        [CommandOption("status", Description = "Print every unit's progress and exit.")] public bool Status { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var options = new MigrationOptions
            {
                SourcePath = Source ?? config.V1SourcePath ?? throw new CommandException("--source or Books:V1SourcePath is required."),
                TargetPath = Target ?? config.DbPath ?? throw new CommandException("--target or Books:DbPath is required."),
                LegsPath = Legs ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required."),
                CalibreLinkPath = CalibreLink ?? config.CalibreLinkPath,
                CacheDir = CacheDir ?? config.CacheDir,
                ReportDir = ReportDir ?? config.ReportDir,
                BatchSize = Math.Max(1, BatchSize), MaxBatches = Math.Max(0, MaxBatches), DryRun = DryRun, Stage = Stage, After = After,
                OwnerUsername = Owner ?? config.V1OwnerUsername ?? throw new CommandException("--owner or Books:V1OwnerUsername is required (the one account whose activity migrates)."),
                UserIdForOwner = OwnerUserId ?? config.OwnerUserId,
            };
            if (!File.Exists(options.TargetPath)) throw new CommandException($"{options.TargetPath} does not exist — run books-db-migrate first.");
            using var source = new V1Source(options.SourcePath);
            var mapping = MappingContract.Load();
            var ctx = new MigrationContext(source, mapping, options, line => console.Output.WriteLine(line));
            var engine = new MigrationEngine(ctx);

            if (Status)
            {
                foreach (var p in engine.AllProgress())
                    await console.Output.WriteLineAsync($"{(p.FinishedAt != null ? "done " : "     ")} {p.Stage,-45} cursor {p.Cursor,10} processed {p.Processed,9}");
                return;
            }
            if (Reset) { engine.ResetProgress(Stage); await console.Output.WriteLineAsync($"progress reset: {Stage ?? "all"}"); }

            await console.Output.WriteLineAsync($"books-migrate-v1 {(DryRun ? "(DRY RUN) " : "")}{options.SourcePath} -> {options.TargetPath} + {options.LegsPath}; batch {options.BatchSize}, max-batches {options.MaxBatches}, stage {Stage ?? "all"}");
            var summary = engine.Run(console.RegisterCancellationHandler());
            await console.Output.WriteLineAsync($"batches {summary.Batches}; units finished {summary.UnitsFinished}, remaining {summary.UnitsRemaining}" + (summary.Stopped ? $"; STOPPED: {summary.StopReason}" : ""));
            if (summary.Stopped && summary.StopReason != "max batches reached") throw new CommandException("migration stopped without progress", 2);
        }
    }
}
