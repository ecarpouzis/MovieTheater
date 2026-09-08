using System.Globalization;
using System.Text;
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
    /// <c>books-curated-spans-import</c> — land the model pass's judged containment as
    /// <c>CollectedEditionSpan(Source = Curated)</c>.
    ///
    /// <para>The input is the JSONL the pass appends as it walks the series packets, one line per collected
    /// edition: <c>{itemId, start, end, editionTitle, confidence, rationale}</c>, or
    /// <c>{itemId, unknown: true, why}</c> when the evidence did not decide. Unknown is NOT empty — it is a
    /// deliberate refusal, so it is counted and never written; the book stays a labelled leaf rather than
    /// acquiring a guessed table of contents that a file de-duplication would then act on.</para>
    ///
    /// <para><b>Gold is not overwritten.</b> A Curated row this pass did not write (ProviderRef without the
    /// <c>model:</c> prefix) is v1 quoting an edition's own indicia. A model answer replaces a Curated row only at
    /// equal or greater confidence, and never displaces a disagreeing gold row — the disagreement is written to
    /// the flags CSV instead. Re-running the same file is a no-op beyond rewriting the pass's own rows, so the
    /// import is idempotent on <c>(ItemId, Source)</c>.</para>
    ///
    /// <para>Chunked by INPUT LINE — the file's own ordering — with a resumable <c>--after</c> line cursor, a
    /// per-batch progress line and dry-run by default.</para>
    /// </summary>
    [Command("books-curated-spans-import", Description = "Import the model pass's judged collected-edition ranges as CollectedEditionSpan(Source=Curated) (chunked, resumable, idempotent).")]
    public class BooksCuratedSpansImportCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksCuratedSpansImportCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("in", IsRequired = true, Description = "The model pass's curated_spans.jsonl.")] public string InPath { get; set; } = "";
        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Lines per batch (default 2000).")] public int BatchSize { get; set; } = 2000;
        [CommandOption("after", Description = "Resume after this many input lines (the cursor a batch prints).")] public int After { get; set; }
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("batch", Description = "Label recorded as ProviderRef 'model:<label>' when a line carries no batch of its own.")] public string BatchLabel { get; set; } = "pass1";
        [CommandOption("flags", Description = "Where refusals and disagreements are appended (default {ReportDir}/curated-span-import-flags.csv).")] public string? FlagsPath { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only counts and samples.")] public bool Apply { get; set; }
        [CommandOption("top", Description = "How many written spans to print (default 20).")] public int Top { get; set; } = 20;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(InPath)) throw new CommandException($"input not found: {InPath}");
            var lines = File.ReadAllLines(InPath);
            var total = lines.Length;
            if (After > total) throw new CommandException($"--after {After} is past the end of {InPath} ({total} lines).");

            var flagsPath = FlagsPath ?? Path.Combine(config.ReportDir ?? ".", "curated-span-import-flags.csv");
            var flags = new StringBuilder();

            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var batchSize = Math.Clamp(BatchSize, 50, 50_000);
            var cursor = After;
            int written = 0, kept = 0, unknown = 0, invalid = 0, missingItem = 0, printed = 0, batches = 0, blank = 0;
            var retracted = 0;

            while (cursor < total)
            {
                var take = Math.Min(batchSize, total - cursor);
                var parsed = new List<CuratedSpanImport.Line>(take);
                for (var i = 0; i < take; i++)
                {
                    var raw = lines[cursor + i];
                    if (raw.Trim().Length == 0) { blank++; continue; }
                    parsed.Add(CuratedSpanImport.Parse(raw, cursor + i + 1));
                }

                // One round trip per batch for the two things the decision needs: the item's series, and the
                // Curated row it may already carry.
                var ids = parsed.Where(p => p.Error == null).Select(p => (long)p.ItemId).Distinct().ToList();
                var idList = ids.Count == 0 ? "-1" : string.Join(",", ids);
                var series = new Dictionary<int, int?>();
                foreach (var (id, payload) in hot.Pairs(
                    $"SELECT Id, coalesce(SeriesId, '') FROM Item WHERE Id IN ({idList})"))
                    series[(int)id] = payload is { Length: > 0 } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

                var existing = new Dictionary<int, CuratedSpanImport.Existing>();
                foreach (var (id, payload) in hot.Pairs(
                    $@"SELECT ItemId, coalesce(IssueStart,'') || char(31) || coalesce(IssueEnd,'') || char(31)
                           || coalesce(Confidence,'') || char(31) || coalesce(ProviderRef,'')
                       FROM CollectedEditionSpan WHERE Source = {(int)EditionSource.Curated} AND ItemId IN ({idList})"))
                {
                    var p = payload!.Split(TargetWriter.Sep);
                    existing[(int)id] = new CuratedSpanImport.Existing(Dbl(p[0]), Dbl(p[1]), Dbl(p[2]), p[3].Length == 0 ? null : p[3]);
                }

                if (Apply) hot.Begin();
                foreach (var line in parsed)
                {
                    existing.TryGetValue(line.ItemId, out var prior);
                    var verdict = CuratedSpanImport.Decide(line, prior, out var flag, out var detail);
                    var seriesId = series.TryGetValue(line.ItemId, out var sid) ? sid : null;
                    if (verdict == CuratedSpanImport.Verdict.Write && !series.ContainsKey(line.ItemId))
                    {
                        // An item id the model invented, or a row deleted since the packets were exported.
                        verdict = CuratedSpanImport.Verdict.Invalid;
                        flag = "invalid-span";
                        detail = "no such Item";
                        missingItem++;
                    }

                    if (flag != null)
                        flags.Append(line.ItemId).Append(',').Append(seriesId?.ToString(CultureInfo.InvariantCulture) ?? "")
                             .Append(',').Append(flag).Append(',').Append(Csv(detail)).Append(',')
                             .Append(Csv($"line {line.LineNo}")).Append('\n');

                    switch (verdict)
                    {
                        case CuratedSpanImport.Verdict.Unknown: unknown++; continue;
                        case CuratedSpanImport.Verdict.Kept: kept++; continue;
                        case CuratedSpanImport.Verdict.Invalid: invalid++; continue;
                        case CuratedSpanImport.Verdict.Retract:
                            unknown++;
                            retracted++;
                            hot.Exec(
                                "DELETE FROM CollectedEditionSpan WHERE ItemId = $id AND Source = $src"
                                + " AND ProviderRef LIKE 'model:%'",
                                ("$id", line.ItemId), ("$src", (int)EditionSource.Curated));
                            continue;
                    }

                    written++;
                    if (printed++ < Math.Max(1, Top))
                        await console.Output.WriteLineAsync(
                            $"  {line.ItemId,7}  #{CuratedSpanImport.Fmt(line.Start)}-{CuratedSpanImport.Fmt(line.End)}"
                            + $"  conf {line.Confidence:0.##}  {line.EditionTitle}"
                            + (prior == null ? "" : $"  (replaces #{CuratedSpanImport.Fmt(prior.Start)}-{CuratedSpanImport.Fmt(prior.End)})"));

                    hot.Upsert("CollectedEditionSpan", new
                    {
                        ItemId = line.ItemId,
                        Source = EditionSource.Curated,
                        SeriesId = seriesId,
                        IssueStart = line.Start,
                        IssueEnd = line.End,
                        EditionTitle = line.EditionTitle,
                        ProviderRef = "model:" + (line.Batch ?? BatchLabel),
                        Contiguous = true,
                        Confidence = line.Confidence,
                        Note = line.Rationale,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
                if (Apply) hot.Commit();

                cursor += take;
                batches++;
                await console.Output.WriteLineAsync(
                    $"{{ processed: {take}, remaining: {total - cursor}, nextCursor: \"{cursor}\", "
                    + $"counts: {{ written: {written}, retracted: {retracted}, kept: {kept}, unknown: {unknown}, invalid: {invalid} }} }}  [curated-spans]");
                if (MaxBatches > 0 && batches >= MaxBatches) break;
            }

            if (flags.Length > 0)
            {
                if (Apply)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(flagsPath))!);
                    if (!File.Exists(flagsPath)) File.WriteAllText(flagsPath, "itemId,seriesId,flag,detail,source\n");
                    File.AppendAllText(flagsPath, flags.ToString());
                    await console.Output.WriteLineAsync($"flags appended to {flagsPath}");
                }
                else
                {
                    await console.Output.WriteLineAsync($"{flags.ToString().TrimEnd('\n').Split('\n').Length} flag rows would be appended to {flagsPath}");
                }
            }

            await console.Output.WriteLineAsync(
                $"done: {total - After} lines read{(blank > 0 ? $" ({blank} blank)" : "")}, "
                + $"{{ written: {written}, retracted: {retracted}, kept: {kept}, unknown: {unknown}, invalid: {invalid}, missingItem: {missingItem} }}"
                + (Apply ? " and were written" : " (dry run — re-run with --apply)"));
            if (Apply)
                await console.Output.WriteLineAsync(
                    "next: books-collected-editions -> books-reading-order -> books-containment -> books-resolve");
        }

        private static double? Dbl(string s) =>
            s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);

        private static string Csv(string? s) =>
            s == null ? "" : s.IndexOfAny([',', '"', '\n']) < 0 ? s : '"' + s.Replace("\"", "\"\"") + '"';
    }
}
