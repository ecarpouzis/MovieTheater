using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Books.Db;
using MovieTheater.Books.Parse;
using MovieTheater.Books.Services;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// The reconciliation verbs. Every one of them edits an INPUT — a parsed key, a provider link, a display
    /// override — and then tells the operator to run <c>books-resolve --series</c>, because `Series`,
    /// `SeriesAlias` and `Item.SeriesId` are derived and are never edited directly.
    /// </summary>
    internal static class SeriesOps
    {
        public static async Task<(ServiceProvider Provider, AsyncServiceScope Scope, BooksDb Db)> OpenAsync(BooksHostConfiguration config, string? dbPath)
        {
            var provider = CommandServices.Build(config, dbPath ?? config.DbPath ?? throw new CommandException("--db or Books:DbPath is required."));
            var scope = provider.CreateAsyncScope();
            return await Task.FromResult((provider, scope, scope.ServiceProvider.GetRequiredService<BooksDb>()));
        }

        public const string Next = "next: books-resolve --series (the identity is derived; this verb only edited its inputs)";
    }

    /// <summary><c>books-series-override</c> — set or clear a series' hand-chosen display name.</summary>
    [Command("books-series-override", Description = "Set (or clear) Series.DisplayNameOverride — the top tier of the name chain.")]
    public class BooksSeriesOverrideCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesOverrideCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("series", Description = "The series id.")] public int SeriesId { get; set; }
        [CommandOption("name", Description = "The display name. Omit to CLEAR the override.")] public string? Name { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var (provider, scope, db) = await SeriesOps.OpenAsync(config, DbPath);
            await using (provider) await using (scope)
            {
                var service = provider.GetRequiredService<SeriesNamesService>();
                try { await console.Output.WriteLineAsync((await service.SetOverrideAsync(db, SeriesId, Name)).ToString()); }
                catch (InvalidOperationException ex) { throw new CommandException(ex.Message, 2); }
                await console.Output.WriteLineAsync(SeriesOps.Next);
            }
        }
    }

    /// <summary><c>books-series-clearlink</c> — drop a wrong provider link (it stays as Cleared, never deleted).</summary>
    [Command("books-series-clearlink", Description = "Clear a wrong SeriesKeyLink. The row survives as Cleared so a re-scrape cannot re-make the same wrong match.")]
    public class BooksSeriesClearLinkCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesClearLinkCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("key", Description = "The parsed series key.")] public string ParsedKey { get; set; } = "";
        [CommandOption("provider", Description = "Cv (default) or External.")] public Provider Provider { get; set; } = Provider.Cv;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (string.IsNullOrWhiteSpace(ParsedKey)) throw new CommandException("--key is required.");
            var (provider, scope, db) = await SeriesOps.OpenAsync(config, DbPath);
            await using (provider) await using (scope)
            {
                var service = provider.GetRequiredService<SeriesMismatchService>();
                await console.Output.WriteLineAsync((await service.ClearLinkAsync(db, ParsedKey, Provider, "cli")).ToString());
                await console.Output.WriteLineAsync(SeriesOps.Next);
            }
        }
    }

    /// <summary><c>books-series-namefix</c> — propose (or apply) name repairs the later parse rules would make.</summary>
    [Command("books-series-namefix", Description = "Report series names that still carry parse noise; --apply writes them as display overrides.")]
    public class BooksSeriesNameFixCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesNameFixCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("apply", Description = "Actually write the overrides. Without it this only reports.")] public bool Apply { get; set; }
        [CommandOption("top", Description = "How many proposals to print (default 40).")] public int Top { get; set; } = 40;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var (provider, scope, db) = await SeriesOps.OpenAsync(config, DbPath);
            await using (provider) await using (scope)
            {
                var service = provider.GetRequiredService<SeriesNamesService>();
                var fixes = await service.NameFixAsync(db, Apply);
                foreach (var f in fixes.OrderByDescending(f => f.IssueCount).Take(Math.Max(1, Top)))
                    await console.Output.WriteLineAsync($"  {f.SeriesId,7}  {f.IssueCount,5} issues  '{f.Current}' -> '{f.Proposed}'");
                await console.Output.WriteLineAsync($"{fixes.Count} proposal(s)" + (Apply ? " applied" : " (dry run — re-run with --apply)"));
                if (Apply) await console.Output.WriteLineAsync(SeriesOps.Next);
            }
        }
    }

    /// <summary><c>books-series-prune</c> — remove series rows with no items and no marks.</summary>
    [Command("books-series-prune", Description = "Delete empty series rows (never one a reader has marked). Dry-run by default.")]
    public class BooksSeriesPruneCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesPruneCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("apply", Description = "Actually delete. Without it the verb only counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var (provider, scope, db) = await SeriesOps.OpenAsync(config, DbPath);
            await using (provider) await using (scope)
            {
                var service = provider.GetRequiredService<SeriesNamesService>();
                var (candidates, deleted) = await service.PruneAsync(db, Apply);
                await console.Output.WriteLineAsync($"{{ candidates: {candidates}, deleted: {deleted} }}" + (Apply ? "" : "  (dry run)"));
            }
        }
    }

    /// <summary><c>books-series-split-overmatch</c> — find series that swallowed other runs through a bad match.</summary>
    [Command("books-series-split-overmatch", Description = "Report series holding far more issues than their provider volume claims (an over-eager match).")]
    public class BooksSeriesSplitOvermatchCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesSplitOvermatchCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("ratio", Description = "Held/claimed ratio above which a series is suspect (default 2.0).")] public double Ratio { get; set; } = 2.0;
        [CommandOption("min-issues", Description = "Ignore series smaller than this (default 20).")] public int MinIssues { get; set; } = 20;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var (provider, scope, db) = await SeriesOps.OpenAsync(config, DbPath);
            await using (provider) await using (scope)
            {
                var service = provider.GetRequiredService<SeriesNamesService>();
                var rows = await service.SplitOvermatchAsync(db, Ratio, MinIssues);
                foreach (var r in rows)
                    await console.Output.WriteLineAsync($"  {r.SeriesId,7}  holds {r.Held,5}  claims {r.Claimed,5}  cv:{r.CvVolumeId}  '{r.Name}'");
                await console.Output.WriteLineAsync($"{rows.Count} suspect series. Fix one with books-series-clearlink, then books-resolve --series.");
            }
        }
    }

    /// <summary>
    /// <c>books-fix-issue-numbers</c> — re-run the issue ladder over the stored filenames and report (or fix)
    /// the rows whose `IssueNo` disagrees. This is the verb that corrected 3,328 values when the mini-series
    /// "NN (of MM)" rule landed: the parse rules moved, so the stored answers had to.
    /// </summary>
    [Command("books-fix-issue-numbers", Description = "Re-extract issue numbers from filenames and report (or --apply) the ones that changed.")]
    public class BooksFixIssueNumbersCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksFixIssueNumbersCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("apply", Description = "Actually write. Without it the verb only reports.")] public bool Apply { get; set; }
        [CommandOption("top", Description = "How many changes to print (default 30).")] public int Top { get; set; } = 30;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath);
            long after = 0;
            var changed = 0;
            var printed = 0;
            var batch = Math.Max(100, BatchSize);

            while (true)
            {
                var rows = hot.Pairs($@"
SELECT i.Id, coalesce(i.FileName,'') || char(31) || coalesce(cd.IssueNo,'')
FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
WHERE i.Id > {after} AND i.Kind = 0 ORDER BY i.Id LIMIT {batch}");
                if (rows.Count == 0) break;

                if (Apply) hot.Begin();
                foreach (var (id, payload) in rows)
                {
                    var p = payload!.Split(MovieTheater.Books.Migration.TargetWriter.Sep);
                    var current = p[1].Length == 0 ? null : p[1];
                    var proposed = ComicTitleParser.ExtractIssueNo(Path.GetFileNameWithoutExtension(p[0]));
                    if (proposed == null || string.Equals(proposed, current, StringComparison.Ordinal)) continue;
                    changed++;
                    if (printed++ < Math.Max(1, Top))
                        await console.Output.WriteLineAsync($"  {id,7}  '{current}' -> '{proposed}'   {p[0]}");
                    if (Apply) hot.Update("ComicDetail", "ItemId", id, new { IssueNo = proposed });
                }
                if (Apply) hot.Commit();

                after = rows[^1].Item1;
                await console.Output.WriteLineAsync($"{{ processed: {rows.Count}, remaining: ?, nextCursor: \"{after}\", changed: {changed} }}");
            }

            await console.Output.WriteLineAsync($"done: {changed} issue number(s) differ" + (Apply ? " and were written" : " (dry run — re-run with --apply)"));
            if (Apply) await console.Output.WriteLineAsync("next: books-reading-order (the order reads IssueNo)");
        }
    }
}
