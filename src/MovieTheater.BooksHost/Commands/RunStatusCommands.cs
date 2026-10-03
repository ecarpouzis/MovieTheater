using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Resolve;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-gcd-series-import</c> — refresh legs <c>GcdSeries</c> from the local GCD dump for every GCD series the
    /// identity pass linked (SeriesKeyLink, Provider = Gcd), including GCD's <c>is_current</c>. Reads the dump only.
    /// </summary>
    [Command("books-gcd-series-import", Description = "Refresh legs GcdSeries (incl. is_current) from the GCD dump for every linked GCD series (chunked, resumable).")]
    public class BooksGcdSeriesImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksGcdSeriesImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("gcd", IsRequired = true, Description = "The GCD sqlite dump (read-only).")] public string GcdPath { get; set; } = "";
        [CommandOption("db", Description = "books.db (default Books:DbPath) — where the GCD links live.")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("after", Description = "Resume after this GCD series id.")] public long After { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 500, max 900).")] public int BatchSize { get; set; } = 500;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb counts only.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(GcdPath)) throw new CommandException($"GCD dump not found: {GcdPath}");
            var ids = new List<int>();
            using (var hot = HotFile.Open(config, DbPath))
            using (var cmd = hot.CreateCommand($"SELECT DISTINCT ProviderKey FROM SeriesKeyLink WHERE Provider = {(int)Provider.Gcd} AND Status IN {LinkStatuses.UsableSql}"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) if (int.TryParse(rd.GetString(0), out var id)) ids.Add(id);
            ids.Sort();
            if (!Apply)
            {
                await console.Output.WriteLineAsync($"{{ linkedGcdSeries: {ids.Count}, after: {After}, wouldImport: {ids.Count(x => x > After)} }}  (dry run — re-run with --apply)");
                return;
            }
            using var gcd = GcdSpanJob.OpenReadOnly(GcdPath);
            using var legs = BooksLocgImportCommand.OpenLegsWritable(HotFile.Legs(config, LegsDbPath));
            long cursor = After, written = 0; var batches = 0;
            while (MaxBatches <= 0 || batches < MaxBatches)
            {
                var r = RipImporters.ImportGcdSeries(gcd, legs, ids, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; batches++;
                await console.Output.WriteLineAsync(r + "  [gcd-series]");
                if (r.NextCursor is not long n || n <= cursor) break;   // no-progress break
                cursor = n;
            }
            await console.Output.WriteLineAsync($"done: written {written} over {batches} batch(es); last id {cursor}. next: books-run-status --apply");
        }
    }

    /// <summary>
    /// <c>books-run-status</c> — derive every comic run's publication status (Ongoing / Completed / Ended / Cancelled)
    /// and how much of it we hold. See <see cref="RunStatusJob"/>. Dry run by default (prints the counts).
    /// </summary>
    [Command("books-run-status", Description = "Derive Series.RunStatus / RunPlanned / RunPublished / RunHeld from GCD, ComicVine and the files (dry run by default).")]
    public class BooksRunStatusCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksRunStatusCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Series per committed chunk (default 2000).")] public int BatchSize { get; set; } = 2000;
        [CommandOption("apply", Description = "Actually write.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            var legsPath = HotFile.Legs(config, LegsDbPath);
            using var legs = File.Exists(legsPath) ? GcdSpanJob.OpenReadOnly(legsPath) : null;
            var counts = RunStatusJob.Run(hot, legs, BatchSize, Apply, l => console.Output.WriteLine(l));
            await console.Output.WriteLineAsync($"run status: {counts}" + (Apply ? "" : "  (dry run — re-run with --apply)"));
        }
    }
}
