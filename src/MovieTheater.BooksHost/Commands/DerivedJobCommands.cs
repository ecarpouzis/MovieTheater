using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>Shared plumbing for the verbs that drive a <see cref="TargetWriter"/>-based derived job.</summary>
    internal static class HotFile
    {
        public static TargetWriter Open(BooksHostConfiguration config, string? dbPath) =>
            new(dbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required."), MappingContract.Load(), dryRun: false);

        public static string Legs(BooksHostConfiguration config, string? legsPath) =>
            legsPath ?? config.LegsDbPath ?? throw new CommandException("--legs or Books:LegsDbPath is required.");
    }

    /// <summary><c>books-reading-order</c> — rebuild the derived per-issue reading position, per series.</summary>
    [Command("books-reading-order", Description = "Rebuild ReadingOrderEntry from the best available signal (chunked by series).")]
    public class BooksReadingOrderCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksReadingOrderCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("series", Description = "Rebuild only this series id.")] public int? SeriesId { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 200).")] public int BatchSize { get; set; } = 200;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var rows = ReadingOrderJob.RunAll(hot, BatchSize, l => console.Output.WriteLine(l), SeriesId);
            await console.Output.WriteLineAsync($"reading order: {rows} rows written");
        }
    }

    /// <summary><c>books-reading-order-audit</c> — the per-series CSV of how the order was decided.</summary>
    [Command("books-reading-order-audit", Description = "Write a per-series CSV of reading-order coverage and which signal won.")]
    public class BooksReadingOrderAuditCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksReadingOrderAuditCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("out", Description = "CSV path (default {ReportDir}/reading-order-audit.csv).")] public string? OutPath { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var path = OutPath ?? Path.Combine(config.ReportDir ?? ".", "reading-order-audit.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllLinesAsync(path, ReadingOrderJob.AuditCsv(hot));
            await console.Output.WriteLineAsync($"wrote {path}");
        }
    }

    /// <summary><c>books-containment</c> — rebuild the per-series collection-containment model.</summary>
    [Command("books-containment", Description = "Rebuild CollectionNode (levels, spans, nesting) per series.")]
    public class BooksContainmentCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksContainmentCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 200).")] public int BatchSize { get; set; } = 200;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var rows = ContainmentJob.RunAll(hot, BatchSize, l => console.Output.WriteLine(l));
            await console.Output.WriteLineAsync($"containment: {rows} nodes written");
        }
    }

    /// <summary><c>books-collected-editions</c> — the LOCG containment reduction into CollectedEditionSpan.</summary>
    [Command("books-collected-editions", Description = "Rebuild CollectedEditionSpan(Source=Locg) from the warehouse's containment edges.")]
    public class BooksCollectedEditionsCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksCollectedEditionsCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 2000).")] public int BatchSize { get; set; } = 2000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var (spans, skipped) = CollectedEditionJob.RunAll(hot, HotFile.Legs(config, LegsDbPath), BatchSize, l => console.Output.WriteLine(l));
            await console.Output.WriteLineAsync($"collected editions: {spans} spans, {skipped} skipped");
        }
    }

    /// <summary><c>books-library-ratings</c> — the blend, then the resolver re-materializes ResolvedRating.</summary>
    [Command("books-library-ratings", Description = "Rebuild Rating(Source=Library) for every series and item, then re-resolve the browse scalars.")]
    public class BooksLibraryRatingsCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLibraryRatingsCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("resolve", Description = "Re-materialize Item/Series.ResolvedRating afterwards (default true).")] public bool Resolve { get; set; } = true;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var counts = LibraryRatingJob.RunAll(hot, BatchSize, l => console.Output.WriteLine(l));
            await console.Output.WriteLineAsync($"library ratings: {counts}");
            if (!Resolve) return;

            // The blend writes ROWS; the browse reads the materialized scalar, so the resolver runs after it.
            hot.Begin();
            ItemResolver.ResolveSeries(hot);
            long after = 0;
            var batch = Math.Max(100, BatchSize);
            while (true)
            {
                after = ItemResolver.ResolveItems(hot, after, batch, out var n);
                if (n < batch) break;
            }
            hot.Commit();
            await console.Output.WriteLineAsync("resolved scalars re-materialized");
        }
    }

    /// <summary><c>books-dedup</c> — group duplicate copies and suggest a keeper (never deletes; never hides).</summary>
    [Command("books-dedup", Description = "Detect duplicate copies into DuplicateGroup/Member (chunked; --csv writes the review sheet).")]
    public class BooksDedupCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksDedupCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("csv", Description = "Write a review sheet here (default {ReportDir}/dedup.csv when set).")] public string? CsvPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 5000).")] public int BatchSize { get; set; } = DuplicateDetectionService.DefaultBatchSize;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = until done).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Write the groups. Without it the verb only counts them.")] public bool Apply { get; set; }
        [CommandOption("reset", Description = "Forget the saved cursor and start from the first item.")] public bool Reset { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            await using var provider = CommandServices.Build(config, dbPath);
            var service = provider.GetRequiredService<DuplicateDetectionService>();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();

            if (Reset) await service.ResetAsync(db);

            StreamWriter? csv = null;
            if (CsvPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CsvPath))!);
                csv = new StreamWriter(CsvPath, false, new System.Text.UTF8Encoding(true));
                await csv.WriteLineAsync(DuplicateDetectionService.CsvHeader);
            }

            long groups = 0, duplicates = 0;
            var batches = 0;
            try
            {
                while (MaxBatches <= 0 || batches < MaxBatches)
                {
                    var r = await service.RunBatchAsync(db, BatchSize, Apply, csv);
                    batches++;
                    groups += r.Groups;
                    duplicates += r.Duplicates;
                    await console.Output.WriteLineAsync(r.ToString() + $"  [batches: {batches}]");
                    if (r.Done) break;
                }
            }
            finally { if (csv != null) { await csv.FlushAsync(); csv.Dispose(); } }

            await console.Output.WriteLineAsync(
                $"done: {groups} group(s), {duplicates} duplicate member(s) over {batches} batch(es)" + (Apply ? "" : " (dry run — nothing written)"));
        }
    }

    /// <summary><c>books-parse-audit</c> — the parse pipeline's own CSV, one row per comic.</summary>
    [Command("books-parse-audit", Description = "Write the parse-pipeline audit CSV (series/issue/year/publisher with the source of each).")]
    public class BooksParseAuditCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksParseAuditCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("out", Description = "CSV path (default {ReportDir}/parse-audit.csv).")] public string? OutPath { get; set; }
        [CommandOption("batch-size", Description = "Items per page (default 5000).")] public int BatchSize { get; set; } = 5000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var path = OutPath ?? Path.Combine(config.ReportDir ?? ".", "parse-audit.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            await using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            await writer.WriteLineAsync("itemId,fileName,parsedSeriesKey,issueNo,year,volumeNo,publisher,format,confidence,seriesSource,issueSource,yearSource,publisherSource,seriesId,parseNotes");

            long after = 0;
            var total = 0;
            while (true)
            {
                var rows = hot.Pairs($@"
SELECT i.Id,
       replace(coalesce(i.FileName,''), ',', ' ') || char(31) || replace(coalesce(cd.ParsedSeriesKey,''), ',', ' ') || char(31)
    || coalesce(cd.IssueNo,'') || char(31) || coalesce(cd.Year,'') || char(31) || coalesce(cd.VolumeNo,'') || char(31)
    || replace(coalesce(cd.Publisher,''), ',', ' ') || char(31) || coalesce(cd.Format,'') || char(31) || coalesce(cd.Confidence,'') || char(31)
    || coalesce(cd.SeriesSource,'') || char(31) || coalesce(cd.IssueSource,'') || char(31) || coalesce(cd.YearSource,'') || char(31)
    || coalesce(cd.PublisherSource,'') || char(31) || coalesce(i.SeriesId,'') || char(31) || replace(coalesce(cd.ParseNotes,''), ',', ';')
FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
WHERE i.Id > {after} ORDER BY i.Id LIMIT {Math.Max(100, BatchSize)}");
                if (rows.Count == 0) break;
                foreach (var (id, payload) in rows)
                    await writer.WriteLineAsync(id + "," + string.Join(",", payload!.Split(TargetWriter.Sep)));
                total += rows.Count;
                after = rows[^1].Item1;
                await console.Output.WriteLineAsync($"{{ processed: {rows.Count}, remaining: ?, nextCursor: \"{after}\" }}  [parse-audit, total: {total}]");
            }
            await console.Output.WriteLineAsync($"wrote {path} ({total} rows)");
        }
    }
}
