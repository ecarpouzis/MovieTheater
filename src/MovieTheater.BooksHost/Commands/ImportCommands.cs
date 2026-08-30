using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-import-calibre</c> — fill the books' Calibre-native identity from a Calibre <c>metadata.db</c>.
    /// This is the job that finally fills <c>BookDetail.SeriesName</c>: v1 had no column for it, so it is NULL
    /// for all 22,084 migrated books until this runs.
    /// </summary>
    [Command("books-import-calibre", Description = "Fill BookDetail / author credits / subject tags from a Calibre metadata.db (chunked, idempotent).")]
    public class BooksImportCalibreCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksImportCalibreCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("metadata", Description = "The Calibre metadata.db (read-only).")] public string? MetadataPath { get; set; }
        [CommandOption("link", Description = "calibre_link.json (default Books:CalibreLinkPath).")] public string? LinkPath { get; set; }
        [CommandOption("library-root", Description = "The Calibre library's root as the catalog knows it (default: the metadata.db's folder). Pass it when --metadata is a copy.")] public string? LibraryRoot { get; set; }
        [CommandOption("batch-size", Description = "Calibre books per batch (default 500).")] public int BatchSize { get; set; } = CalibreImportService.DefaultBatchSize;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = until done).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only reports what would match.")] public bool Apply { get; set; }
        [CommandOption("reset", Description = "Forget the saved cursor and start from the first book.")] public bool Reset { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var dbPath = DbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required.");
            var metadata = MetadataPath ?? throw new CommandException("--metadata <calibre metadata.db> is required.");
            if (!File.Exists(metadata)) throw new CommandException($"Calibre metadata.db not found at {metadata}", 2);
            var link = LinkPath ?? config.CalibreLinkPath;

            await using var provider = CommandServices.Build(config, dbPath);
            var service = provider.GetRequiredService<CalibreImportService>();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BooksDb>();

            if (Reset) await service.ResetAsync(db);

            long matched = 0, unmatched = 0, filled = 0, repathed = 0, foldersFixed = 0;
            var batches = 0;
            long? after = null; // dry run: the cursor lives here, not in the store
            await console.Output.WriteLineAsync($"metadata: {metadata}" + Environment.NewLine + $"library root: {LibraryRoot ?? Path.GetDirectoryName(Path.GetFullPath(metadata))}  (paths are composed under this and compared with Item.Path)");
            while (MaxBatches <= 0 || batches < MaxBatches)
            {
                var r = await service.RunBatchAsync(db, metadata, link, BatchSize, Apply, LibraryRoot, Apply ? null : after);
                if (!Apply) after = r.NextCursor ?? after;
                batches++;
                // the terminal batch (nothing left to read) re-reports the persisted totals, not new work
                if (!(r.Done && r.Processed == 0))
                {
                    matched += r.Matched; unmatched += r.Unmatched; filled += r.Filled;
                    repathed += r.Repathed; foldersFixed += r.FoldersFixed;
                }
                await console.Output.WriteLineAsync(r.ToString() + $"  [batches: {batches}]");
                if (r.Done) break;
            }

            await console.Output.WriteLineAsync(
                $"done: matched {matched}, unmatched {unmatched}, filled {filled}, repathed {repathed}, folders-fixed {foldersFixed} over {batches} batch(es)"
                + (Apply ? "" : " (dry run — nothing written)"));
        }
    }

    /// <summary>
    /// <c>books-locg-import</c> — take a League of Comic Geeks JSONL export into the warehouse (and the hot
    /// subset the modal reads). The scraper that PRODUCES the export is an offline Node pipeline and is not
    /// ported; this is its consume side.
    /// </summary>
    [Command("books-locg-import", Description = "Import a LOCG JSONL export into LocgComicRaw / LocgCreatorRaw (+ the hot LocgComic subset).")]
    public class BooksLocgImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLocgImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("file", Description = "The .jsonl export.")] public string? FilePath { get; set; }
        [CommandOption("batch-size", Description = "Lines per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("after", Description = "Resume after this line number.")] public long After { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var file = FilePath ?? throw new CommandException("--file <export.jsonl> is required.");
            if (!File.Exists(file)) throw new CommandException($"Not found: {file}", 2);
            using var hot = HotFile.Open(config, DbPath);
            using var legs = OpenLegsWritable(HotFile.Legs(config, LegsDbPath));

            var cursor = After;
            long written = 0, skipped = 0;
            while (true)
            {
                var r = LegImporters.ImportLocgJsonl(hot, legs, file, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r.ToString());
                cursor = long.Parse(r.NextCursor!);
            }
            await console.Output.WriteLineAsync($"done: {written} written, {skipped} skipped");
        }

        internal static SqliteConnection OpenLegsWritable(string path)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            conn.Open();
            return conn;
        }
    }

    /// <summary><c>books-locg-import-map</c> — land offline-decided item↔LOCG matches as Manual links.</summary>
    [Command("books-locg-import-map", Description = "Import an itemId,locgComicId CSV as ItemProviderLink(Locg, Manual).")]
    public class BooksLocgImportMapCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLocgImportMapCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("file", Description = "The CSV (itemId,locgComicId).")] public string? FilePath { get; set; }
        [CommandOption("batch-size", Description = "Lines per batch (default 5000).")] public int BatchSize { get; set; } = 5000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var file = FilePath ?? throw new CommandException("--file <map.csv> is required.");
            if (!File.Exists(file)) throw new CommandException($"Not found: {file}", 2);
            using var hot = HotFile.Open(config, DbPath);

            long cursor = 0, written = 0, skipped = 0;
            while (true)
            {
                var r = LegImporters.ImportLocgMap(hot, file, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r.ToString());
                cursor = long.Parse(r.NextCursor!);
            }
            await console.Output.WriteLineAsync($"done: {written} links, {skipped} skipped");
        }
    }

    /// <summary><c>books-locg-containment</c> — the alias the plan names for the LOCG span reduction.</summary>
    [Command("books-locg-containment", Description = "Rebuild CollectedEditionSpan(Source=Locg) from the LOCG containment edges (alias of books-collected-editions).")]
    public class BooksLocgContainmentCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLocgContainmentCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 2000).")] public int BatchSize { get; set; } = 2000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var (spans, skipped) = MovieTheater.Books.Resolve.CollectedEditionJob.RunAll(
                hot, HotFile.Legs(config, LegsDbPath), BatchSize, l => console.Output.WriteLine(l));
            await console.Output.WriteLineAsync($"collected editions: {spans} spans, {skipped} skipped");
        }
    }

    /// <summary>
    /// <c>books-gcd-match</c> — match items to Grand Comics Database issues out of a READ-ONLY GCD dump, by
    /// ISBN then barcode. Exact identifiers only: GCD's value is that its rows are human-verified, and a fuzzy
    /// match would throw that away.
    /// </summary>
    [Command("books-gcd-match", Description = "Match items to GCD issues by ISBN/barcode from a read-only GCD SQLite dump.")]
    public class BooksGcdMatchCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksGcdMatchCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("gcd", Description = "The GCD SQLite dump (opened read-only).")] public string? GcdPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 5000).")] public int BatchSize { get; set; } = 5000;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var gcdPath = GcdPath ?? throw new CommandException("--gcd <gcd.db> is required.");
            if (!File.Exists(gcdPath)) throw new CommandException($"Not found: {gcdPath}", 2);
            using var hot = HotFile.Open(config, DbPath);
            using var legs = BooksLocgImportCommand.OpenLegsWritable(HotFile.Legs(config, LegsDbPath));
            using var gcd = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = gcdPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            gcd.Open();

            long cursor = 0, written = 0, skipped = 0;
            while (true)
            {
                var r = LegImporters.MatchGcd(hot, gcd, legs, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r.ToString());
                cursor = long.Parse(r.NextCursor!);
            }
            await console.Output.WriteLineAsync($"done: {written} matched, {skipped} unmatched");
        }
    }

    /// <summary><c>books-mu-import</c> — a MangaUpdates JSON export into MuSeries + MuSeriesRaw.</summary>
    [Command("books-mu-import", Description = "Import a MangaUpdates JSON export into MuSeries (hot) and MuSeriesRaw (legs).")]
    public class BooksMuImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksMuImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("file", Description = "The JSON array export.")] public string? FilePath { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 500).")] public int BatchSize { get; set; } = 500;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var file = FilePath ?? throw new CommandException("--file <mangaupdates.json> is required.");
            if (!File.Exists(file)) throw new CommandException($"Not found: {file}", 2);
            using var hot = HotFile.Open(config, DbPath);
            using var legs = BooksLocgImportCommand.OpenLegsWritable(HotFile.Legs(config, LegsDbPath));

            long cursor = 0, written = 0, skipped = 0;
            while (true)
            {
                var r = LegImporters.ImportMangaUpdates(hot, legs, file, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r.ToString());
                cursor = long.Parse(r.NextCursor!);
            }
            await console.Output.WriteLineAsync($"done: {written} series, {skipped} skipped");
        }
    }
}
