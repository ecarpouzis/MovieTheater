using System.Globalization;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-containment-flags-import</c> — load the containment pass's review sheet into
    /// <c>ContainmentFlag</c> so it can be worked through in the admin UI instead of a spreadsheet.
    ///
    /// <para>Chunked by input line with an <c>--after</c> cursor and dry-run by default, like every other bulk
    /// verb here. Keyed on <c>(ItemId, Flag)</c>: a second import of an edited sheet refreshes the evidence and
    /// leaves any verdict a person has already recorded alone.</para>
    /// </summary>
    [Command("books-containment-flags-import", Description = "Import the containment pass's flags CSV into ContainmentFlag for admin review (chunked, resumable, idempotent).")]
    public class BooksContainmentFlagsImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksContainmentFlagsImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("in", IsRequired = true, Description = "The pass's flags.csv (itemId,seriesId,series,flag,detail,file).")] public string InPath { get; set; } = "";
        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("source", Description = "Recorded as ContainmentFlag.Source (default 'model-pass').")] public string Source { get; set; } = "model-pass";
        [CommandOption("batch-size", Description = "Lines per batch (default 500).")] public int BatchSize { get; set; } = 500;
        [CommandOption("after", Description = "Resume after this many input lines (the cursor a batch prints).")] public int After { get; set; }
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("prune", Description = "Delete Pending flags of this Source that the sheet no longer carries. Decided flags are never pruned.")] public bool Prune { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only counts.")] public bool Apply { get; set; }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(InPath)) throw new CommandException($"input not found: {InPath}");
            var lines = File.ReadAllLines(InPath);
            var total = lines.Length;
            if (After > total) throw new CommandException($"--after {After} is past the end of {InPath} ({total} lines).");

            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var batchSize = Math.Clamp(BatchSize, 50, 20_000);
            var cursor = After;
            int inserted = 0, updated = 0, skipped = 0, bad = 0, missingItem = 0, batches = 0;
            var seen = new HashSet<(int, string)>();

            while (cursor < total)
            {
                var take = Math.Min(batchSize, total - cursor);
                var rows = new List<ContainmentFlagImport.Row>(take);
                for (var i = 0; i < take; i++)
                {
                    var raw = lines[cursor + i];
                    if (raw.Trim().Length == 0) { skipped++; continue; }
                    if (ContainmentFlagImport.IsHeader(raw)) { skipped++; continue; }
                    rows.Add(ContainmentFlagImport.Parse(raw, cursor + i + 1));
                }

                var ids = rows.Where(r => r.Error == null).Select(r => (long)r.ItemId).Distinct().ToList();
                var idList = ids.Count == 0 ? "-1" : string.Join(",", ids);
                var known = new HashSet<int>();
                foreach (var (id, _) in hot.Pairs($"SELECT Id, '' FROM Item WHERE Id IN ({idList})")) known.Add((int)id);

                if (Apply) hot.Begin();
                foreach (var r in rows)
                {
                    if (r.Error != null)
                    {
                        bad++;
                        await console.Output.WriteLineAsync($"  line {r.LineNo}: {r.Error}");
                        continue;
                    }
                    if (!known.Contains(r.ItemId)) { missingItem++; continue; }
                    seen.Add((r.ItemId, r.Flag));

                    // The verdict is the human's; only the evidence is the sheet's.
                    var affected = hot.Exec(
                        @"UPDATE ContainmentFlag SET SeriesId = $sid, Detail = $detail, Source = $src
                          WHERE ItemId = $id AND Flag = $flag",
                        ("$sid", r.SeriesId), ("$detail", r.Detail), ("$src", Source),
                        ("$id", r.ItemId), ("$flag", r.Flag));
                    if (affected > 0) { updated++; continue; }

                    hot.Exec(
                        @"INSERT INTO ContainmentFlag (ItemId, SeriesId, Flag, Detail, Source, ReviewState, CreatedAt)
                          VALUES ($id, $sid, $flag, $detail, $src, $state, $now)
                          ON CONFLICT (ItemId, Flag) DO UPDATE SET Detail = excluded.Detail, SeriesId = excluded.SeriesId",
                        ("$id", r.ItemId), ("$sid", r.SeriesId), ("$flag", r.Flag), ("$detail", r.Detail),
                        ("$src", Source), ("$state", ContainmentFlagImport.Pending),
                        ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                    inserted++;
                }
                if (Apply) hot.Commit();

                cursor += take;
                batches++;
                await console.Output.WriteLineAsync(
                    $"{{ processed: {take}, remaining: {total - cursor}, nextCursor: \"{cursor}\", "
                    + $"counts: {{ inserted: {inserted}, updated: {updated}, missingItem: {missingItem}, bad: {bad} }} }}  [containment-flags]");
                if (MaxBatches > 0 && batches >= MaxBatches) break;
            }

            var pruned = 0;
            if (Prune && cursor >= total)
            {
                // Only ever Pending rows of this Source: a flag someone has ruled on is a decision, not a cache.
                var live = hot.Pairs(
                    $"SELECT ItemId, coalesce(Flag,'') FROM ContainmentFlag WHERE ReviewState = '{ContainmentFlagImport.Pending}' AND Source = '{Source.Replace("'", "''")}'");
                if (Apply) hot.Begin();
                foreach (var (id, flag) in live)
                {
                    if (seen.Contains(((int)id, flag ?? ""))) continue;
                    hot.Exec("DELETE FROM ContainmentFlag WHERE ItemId = $id AND Flag = $flag AND ReviewState = $state",
                             ("$id", (int)id), ("$flag", flag), ("$state", ContainmentFlagImport.Pending));
                    pruned++;
                }
                if (Apply) hot.Commit();
            }

            await console.Output.WriteLineAsync(
                $"done: {total - After} lines read, {{ inserted: {inserted}, updated: {updated}, pruned: {pruned}, "
                + $"missingItem: {missingItem}, bad: {bad}, skipped: {skipped} }}"
                + (Apply ? " and were written" : " (dry run — re-run with --apply)"));
        }
    }
}
