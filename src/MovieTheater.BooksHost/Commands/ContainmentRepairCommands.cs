using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Providers;
using MovieTheater.Books.Resolve;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-cv-descriptions-import</c> — land the offline ComicVine rip's volume DESCRIPTIONS in the legs
    /// warehouse. The description is the only place ComicVine publishes its "Collected Editions" list, and the
    /// v2 port kept only the 14k volumes it had itself fetched; the rip has 153,805.
    /// </summary>
    [Command("books-cv-descriptions-import", Description = "Import ComicVine volume descriptions from the offline rip into legs CvVolumeDescription (chunked, resumable).")]
    public class BooksCvDescriptionsImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksCvDescriptionsImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("rip", IsRequired = true, Description = "comicdb_comicvine_*.db (read-only).")] public string RipPath { get; set; } = "";
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("after", Description = "Resume from this cv_volume id (exclusive).")] public long After { get; set; }
        [CommandOption("batch-size", Description = "Volumes per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb counts only.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(RipPath)) throw new CommandException($"rip not found: {RipPath}");
            var legsPath = HotFile.Legs(config, LegsDbPath);
            using var rip = GcdSpanJob.OpenReadOnly(RipPath);

            if (!Apply)
            {
                using var probe = GcdSpanJob.OpenReadOnly(RipPath);
                using var cmd = probe.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM cv_volume WHERE id > $a";
                cmd.Parameters.AddWithValue("$a", After);
                var total = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                await console.Output.WriteLineAsync(
                    $"{{ wouldImport: {total}, from: cv_volume, into: CvVolumeDescription }}  (dry run — re-run with --apply)");
                return;
            }

            using var legs = BooksLocgImportCommand.OpenLegsWritable(legsPath);
            var cursor = After;
            long written = 0, skipped = 0;
            var batches = 0;
            while (true)
            {
                var r = RipImporters.ImportCvDescriptions(rip, legs, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r + "  [cv-descriptions]");
                cursor = r.NextCursor!.Value;
                if (MaxBatches > 0 && ++batches >= MaxBatches) break;
            }
            await console.Output.WriteLineAsync($"done: {written} descriptions written, {skipped} volumes had none");
        }
    }

    /// <summary>
    /// <c>books-locg-reprints-import</c> — load the LOCG scraper's cached REVERSE reprint edges
    /// (<c>locg_cache/reprints/&lt;comicId&gt;.json</c>, "which editions reprint this comic") into legs
    /// `LocgContainment`. An edition whose own detail page was a shell still appears in the reverse list of
    /// every issue it collects, so this is how its table of contents comes back.
    /// </summary>
    [Command("books-locg-reprints-import", Description = "Import cached LOCG reverse reprint edges into legs LocgContainment (chunked, resumable, idempotent).")]
    public class BooksLocgReprintsImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLocgReprintsImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("dir", IsRequired = true, Description = "The locg_cache/reprints directory.")] public string Dir { get; set; } = "";
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("after", Description = "Resume from this file name (a LOCG comic id, exclusive).")] public long After { get; set; }
        [CommandOption("batch-size", Description = "Files per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb counts only.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!Directory.Exists(Dir)) throw new CommandException($"directory not found: {Dir}");
            var files = RipImporters.ReprintFiles(Dir);
            await console.Output.WriteLineAsync($"{files.Count} cached reprint files under {Dir}");
            if (!Apply)
            {
                var pending = files.Count(f => RipImporters.KeyOf(f) > After);
                await console.Output.WriteLineAsync(
                    $"{{ wouldRead: {pending}, into: LocgContainment }}  (dry run — re-run with --apply)");
                return;
            }

            using var legs = BooksLocgImportCommand.OpenLegsWritable(HotFile.Legs(config, LegsDbPath));
            var cursor = After;
            long written = 0, skipped = 0;
            var batches = 0;
            while (true)
            {
                var r = RipImporters.ImportLocgReprints(legs, files, cursor, BatchSize);
                if (r.Done) break;
                written += r.Written; skipped += r.Skipped;
                await console.Output.WriteLineAsync(r + "  [locg-reprints]");
                cursor = r.NextCursor!.Value;
                if (MaxBatches > 0 && ++batches >= MaxBatches) break;
            }
            await console.Output.WriteLineAsync($"done: {written} new edges, {skipped} already present or unusable");
            await console.Output.WriteLineAsync("next: books-locg-editions --apply -> books-collected-editions");
        }
    }

    /// <summary><c>books-cv-spans</c> — CollectedEditionSpan(Source=Cv) from ComicVine's own edition list.</summary>
    [Command("books-cv-spans", Description = "Rebuild CollectedEditionSpan(Source=Cv) by matching collected editions to ComicVine's 'Collected Editions' list by TITLE.")]
    public class BooksCvSpansCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksCvSpansCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("batch-size", Description = "Series per batch (default 500).")] public int BatchSize { get; set; } = 500;
        [CommandOption("resume", Description = "Continue from books:recompute:cv-spans.")] public bool Resume { get; set; }
        [CommandOption("top", Description = "How many matches to print (default 25).")] public int Top { get; set; } = 25;
        [CommandOption("apply", Description = "Actually write. Without it the job walks read-only and counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var (spans, withBlock, flagged) = CvSpanJob.RunAll(
                hot, HotFile.Legs(config, LegsDbPath), BatchSize, l => console.Output.WriteLine(l), Resume, Top);
            await console.Output.WriteLineAsync(
                $"cv spans: {spans} across {withBlock} series with a Collected-Editions block, {flagged} flagged by the page-count audit"
                + (Apply ? " and written" : " (dry run — nothing written)"));
            if (Apply) await console.Output.WriteLineAsync("next: books-reading-order -> books-containment -> books-resolve");
        }
    }

    /// <summary><c>books-gcd-spans</c> — CollectedEditionSpan(Source=Gcd) from the GCD reprint graph, title-matched.</summary>
    [Command("books-gcd-spans", Description = "Rebuild CollectedEditionSpan(Source=Gcd) from the GCD reprint graph by TITLE; deletes every issue-keyed GCD span.")]
    public class BooksGcdSpansCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksGcdSpansCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("gcd", IsRequired = true, Description = "The GCD SQLite dump (read-only).")] public string GcdPath { get; set; } = "";
        [CommandOption("batch-size", Description = "Series per batch (default 500).")] public int BatchSize { get; set; } = 500;
        [CommandOption("resume", Description = "Continue from books:recompute:gcd-spans.")] public bool Resume { get; set; }
        [CommandOption("top", Description = "How many matches to print (default 25).")] public int Top { get; set; } = 25;
        [CommandOption("apply", Description = "Actually write. Without it the job walks read-only and counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(GcdPath)) throw new CommandException($"GCD dump not found: {GcdPath}");
            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var (spans, deleted, flagged) = GcdSpanJob.RunAll(
                hot, GcdPath, BatchSize, l => console.Output.WriteLine(l), Resume, Top);
            await console.Output.WriteLineAsync(
                $"gcd spans: {deleted} existing spans deleted, {spans} rewritten from a title match, {flagged} flagged by the page-count audit"
                + (Apply ? " and written" : " (dry run — nothing written)"));
            if (Apply) await console.Output.WriteLineAsync("next: books-reading-order -> books-containment -> books-resolve");
        }
    }

    /// <summary><c>books-locg-editions</c> — point a collected edition's LOCG link at the LOCG EDITION record.</summary>
    [Command("books-locg-editions", Description = "Re-point ItemProviderLink(Locg) for collected editions at the LOCG record that IS the edition (a container with containment edges).")]
    public class BooksLocgEditionsCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksLocgEditionsCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("legs", Description = "books-legs.db (default Books:LegsDbPath).")] public string? LegsDbPath { get; set; }
        [CommandOption("rich", IsRequired = true, Description = "The locg_cache/rich directory (<locgSeriesId>.json).")] public string RichDir { get; set; } = "";
        [CommandOption("batch-size", Description = "Series per batch (default 500).")] public int BatchSize { get; set; } = 500;
        [CommandOption("resume", Description = "Continue from books:recompute:locg-editions.")] public bool Resume { get; set; }
        [CommandOption("top", Description = "How many re-links to print (default 25).")] public int Top { get; set; } = 25;
        [CommandOption("apply", Description = "Actually write. Without it the job walks read-only and counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!Directory.Exists(RichDir)) throw new CommandException($"directory not found: {RichDir}");
            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var (relinked, already, noCandidates, noMatch) = LocgEditionRelinkJob.RunAll(
                hot, HotFile.Legs(config, LegsDbPath), RichDir, BatchSize, l => console.Output.WriteLine(l), Resume, Top);
            await console.Output.WriteLineAsync(
                $"locg editions: {relinked} links re-pointed, {already} already on a container, "
                + $"{noCandidates} with no container candidate at all, {noMatch} without a confident edition title"
                + (Apply ? " and written" : " (dry run — nothing written)"));
            if (Apply) await console.Output.WriteLineAsync("next: books-collected-editions -> books-reading-order -> books-containment -> books-resolve");
        }
    }
}
