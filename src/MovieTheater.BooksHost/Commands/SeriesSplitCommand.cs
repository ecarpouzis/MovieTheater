using System.Globalization;
using System.Text;
using System.Text.Json;
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
    /// <c>books-series-split</c> — separate the publishing runs that a single <c>Series</c> has swallowed.
    ///
    /// <para><b>The defect.</b> PLAN.md §4.4, open since v1. One <c>Series</c> holding several runs that each
    /// number from 1 makes the same issue number name several different comics: Series 6398 is ten manga —
    /// Fairy Tail, 100 Years Quest, Blue Mistral, City Hero, Ice Trail, Zero and four more — sharing one
    /// chapter-number space, so the perfectly good range "Fairy Tail v17 collects chapters 135-143" swallows
    /// the file <c>Fairy Tail - 100 Years Quest 136</c>, which is a chapter of a different work. Measured over
    /// the library: 404 shelves and 36,266 files carry more than one run marker, and splitting by the folder
    /// the files live in accounts for 62% of every duplicate issue number in them. Until a shelf is split,
    /// CORRECT ranges cannot be applied to it — which is why this verb exists.</para>
    ///
    /// <para><b>What it changes, and why that is the durable place.</b> Only
    /// <c>ComicDetail.ParsedSeriesKey</c>. That column is what <c>books-resolve --series</c> groups by, so
    /// giving each run its own key makes the resolver build the separate <c>Series</c> rows itself, exactly as
    /// it would have if the parse had been right the first time. Writing <c>Item.SeriesId</c> directly would be
    /// undone by the next resolve; this is not. It is also why <c>books-reparse</c> refuses to touch
    /// <c>ParsedSeriesKey</c> and calls the job that does "a different (supervised) job" — this is that job.
    /// Nothing is deleted, no <c>Series</c> row is dropped, and the previous key of every row it touches is
    /// written to a CSV so the split can be walked back.</para>
    ///
    /// <para><b>The input is a decision, not a guess.</b> One JSON line per item —
    /// <c>{"itemId": 40024, "key": "Fairy Tail - 100 Years Quest"}</c> — produced by reading the shelf
    /// (<c>tools/propose_split.py</c> groups a series by the folder its files live in and by the title its
    /// files carry, and prints the groups for a person to accept, name or correct). The verb itself proposes
    /// nothing.</para>
    ///
    /// <para>Chunked by input line with a resumable <c>--after</c> cursor, a per-batch progress line, and
    /// dry-run by default. Re-running the same file is a no-op: a row already carrying its target key is
    /// counted as settled and not rewritten.</para>
    ///
    /// <para><b>Afterwards run <c>books-resolve --series</c></b>, then the containment chain
    /// (<c>books-reading-order</c>, <c>books-containment</c>), or the shelves will still read as one run.</para>
    /// </summary>
    [Command("books-series-split", Description = "Give each publishing run in a conflated Series its own ParsedSeriesKey, so books-resolve --series builds them as separate Series (chunked, resumable, reversible, dry-run by default).")]
    public class BooksSeriesSplitCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksSeriesSplitCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("in", IsRequired = true, Description = "JSONL of {itemId, key} decisions.")] public string InPath { get; set; } = "";
        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Lines per batch (default 2000).")] public int BatchSize { get; set; } = 2000;
        [CommandOption("after", Description = "Resume after this many input lines (the cursor a batch prints).")] public int After { get; set; }
        [CommandOption("undo-log", Description = "Where the previous key of every rewritten row is appended (default {ReportDir}/series-split-undo.csv).")] public string? UndoPath { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only counts and samples.")] public bool Apply { get; set; }
        [CommandOption("top", Description = "How many groups to print (default 40).")] public int Top { get; set; } = 40;

        private sealed record Line(int ItemId, string Key);

        public async ValueTask ExecuteAsync(IConsole console)
        {
            if (!File.Exists(InPath)) throw new CommandException($"input not found: {InPath}");
            var raw = File.ReadAllLines(InPath);
            if (After > raw.Length) throw new CommandException($"--after {After} is past the end of {InPath} ({raw.Length} lines).");

            var undoPath = UndoPath ?? Path.Combine(config.ReportDir ?? ".", "series-split-undo.csv");
            var undo = new StringBuilder();

            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var batchSize = Math.Clamp(BatchSize, 50, 50_000);
            var cursor = After;
            int settled = 0, changed = 0, missing = 0, invalid = 0;
            var perKey = new Dictionary<string, int>(StringComparer.Ordinal);
            // EVERY key the decision names, not only the ones that moved a row this run. A re-run must still
            // be able to create a Series row that is missing, or a half-finished split stays half-finished.
            var targets = new HashSet<string>(StringComparer.Ordinal);
            var touched = new List<int>();
            // Which shelves each NEW key is drawing its items out of. The franchise and title a created run
            // inherits have to come from ITS OWN parents: one franchise picked for a whole batch stamps a
            // hundred unrelated runs with one shelf's identity (a 44-run batch put `G.I. Joe v2 (2001)` and
            // `Batman Beyond v6 (2016)` under the title `30 Days of Night`).
            var parentsOfKey = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            var fromKey = new Dictionary<string, int>(StringComparer.Ordinal);

            while (cursor < raw.Length)
            {
                var slice = raw.Skip(cursor).Take(batchSize).ToList();
                var parsed = new List<Line>();
                foreach (var text in slice)
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        var o = doc.RootElement;
                        if (!o.TryGetProperty("itemId", out var idEl) || !idEl.TryGetInt32(out var id)
                            || !o.TryGetProperty("key", out var keyEl) || keyEl.GetString() is not { Length: > 0 } key)
                        { invalid++; continue; }
                        parsed.Add(new Line(id, key.Trim()));
                    }
                    catch (JsonException) { invalid++; }
                }

                if (parsed.Count > 0)
                {
                    var idList = string.Join(',', parsed.Select(p => p.ItemId));
                    var current = hot.Pairs(
                        $@"SELECT cd.ItemId, coalesce(cd.ParsedSeriesKey,'') || char(31) || coalesce(i.SeriesId,'')
                           FROM ComicDetail cd JOIN Item i ON i.Id = cd.ItemId
                           WHERE cd.ItemId IN ({idList})")
                        .ToDictionary(p => (int)p.Item1, p => p.Item2!.Split(TargetWriter.Sep));

                    if (Apply) hot.Begin();
                    foreach (var line in parsed)
                    {
                        if (!current.TryGetValue(line.ItemId, out var now)) { missing++; continue; }
                        targets.Add(line.Key);
                        var was = now[0];
                        if (string.Equals(was, line.Key, StringComparison.Ordinal)) { settled++; continue; }
                        changed++;
                        perKey[line.Key] = perKey.GetValueOrDefault(line.Key) + 1;
                        fromKey[was] = fromKey.GetValueOrDefault(was) + 1;
                        undo.Append(line.ItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append(Csv(was)).Append(',').Append(Csv(line.Key)).Append(',')
                            .Append(Csv(now.Length > 1 ? now[1] : "")).Append('\n');
                        touched.Add(line.ItemId);
                        if (now.Length > 1 && int.TryParse(now[1], out var parentSid))
                        {
                            if (!parentsOfKey.TryGetValue(line.Key, out var votes))
                                parentsOfKey[line.Key] = votes = new Dictionary<int, int>();
                            votes[parentSid] = votes.GetValueOrDefault(parentSid) + 1;
                        }
                        if (Apply) hot.Update("ComicDetail", "ItemId", line.ItemId, new { ParsedSeriesKey = line.Key });
                    }
                    if (Apply) hot.Commit();
                }

                // The undo log is flushed PER BATCH, not once at the end. It used to be written after the
                // whole run, so when the Series-creation step threw (a UNIQUE clash on CanonicalKey) the
                // file was never written and 26,439 already-committed re-keys had no recorded previous key.
                // The rewrite is committed by this point; the record of it has to be too.
                if (Apply && undo.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(undoPath)!);
                    var fresh = !File.Exists(undoPath);
                    await File.AppendAllTextAsync(undoPath,
                        (fresh ? "ItemId,PreviousParsedSeriesKey,NewParsedSeriesKey,SeriesIdAtSplit\n" : "")
                        + undo);
                    undo.Clear();
                }

                cursor += slice.Count;
                await console.Output.WriteLineAsync(
                    $"{{ processed: {slice.Count}, remaining: {raw.Length - cursor}, nextCursor: {cursor}, "
                  + $"changed: {changed}, settled: {settled} }}  [series-split]");
            }

            // A key with no Series row of its own cannot be re-pointed AT: `books-resolve --series` only ever
            // UPDATES existing rows and maps parsed keys onto them, so a brand-new key would leave its items
            // stranded on the shelf they came from. Create the row here, in the same shape ingest uses, and
            // let the resolver do the rest — it will rename and re-key it from the evidence on its next pass.
            var created = 0;
            if (Apply && targets.Count > 0)
            {
                // Existence is decided by CANONICAL key, not by ParsedKey: `Series.CanonicalKey` is the unique
                // index, and two parsed keys that differ only in case or punctuation normalize onto one row.
                // Checking ParsedKey let a batch try to insert a canonical key that was already taken and the
                // whole run died on `UNIQUE constraint failed: Series.CanonicalKey`.
                var known = hot.Pairs($"SELECT Id, coalesce(CanonicalKey,'') FROM Series WHERE {SeriesResolver.NotBookSql}")
                    .Select(p => p.Item2!).ToHashSet(StringComparer.Ordinal);
                var nextId = hot.Scalar<long>("SELECT coalesce(max(Id), 0) FROM Series");
                hot.Begin();
                // The run inherits the FRANCHISE of the shelf it came out of, and joins that shelf's TITLE.
                // Without this a split silently drops both: 213 runs were created earlier today carrying no
                // franchise at all, so `Green Lantern v2 (1960)` and `v3 (1990)` ended up related by nothing
                // but their names happening to start the same way. The title is the stem this verb itself
                // writes — `<Title> v<N> (<Year>)` — so it is exact here, not inferred.
                var parentIds = parentsOfKey.Values.SelectMany(v => v.Keys).Distinct().ToList();
                var facts = parentIds.Count == 0
                    ? new Dictionary<int, string[]>()
                    : hot.Pairs(
                        $@"SELECT s.Id, coalesce(s.Franchise,'') || char(31) || coalesce(s.TitleId,'')
                           FROM Series s WHERE s.Id IN ({string.Join(',', parentIds)})")
                      .ToDictionary(p => (int)p.Item1, p => p.Item2!.Split(TargetWriter.Sep));

                foreach (var key in targets.OrderBy(k => k, StringComparer.Ordinal))
                {
                    var canonical = "parsed:" + SeriesResolver.NormalizeKey(key);
                    if (!known.Add(canonical)) continue;         // already a row, or an earlier key in this batch
                    // The shelf that gave this key the most items is the one it inherits from.
                    var votes = parentsOfKey.GetValueOrDefault(key);
                    string franchise = "", titleId = "";
                    if (votes is { Count: > 0 })
                        foreach (var (sid, _) in votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key))
                        {
                            if (!facts.TryGetValue(sid, out var f)) continue;
                            if (franchise.Length == 0 && f[0].Length > 0) franchise = f[0];
                            if (titleId.Length == 0 && f.Length > 1 && f[1].Length > 0) titleId = f[1];
                            if (franchise.Length > 0 && titleId.Length > 0) break;
                        }
                    hot.Upsert("Series", new
                    {
                        Id = (int)++nextId,
                        ParsedKey = key,
                        CanonicalKey = canonical,
                        Name = key,
                        Franchise = franchise.Length > 0 ? franchise : null,
                        TitleId = titleId.Length > 0 && int.TryParse(titleId, out var tid) ? tid : (int?)null,
                        IssueCount = 0,
                        IsOngoing = false,
                    });
                    created++;
                    await console.Output.WriteLineAsync(
                        $"  created Series {nextId} for '{key}'"
                      + (franchise.Length > 0 ? $"  franchise '{franchise}'" : "")
                      + (titleId.Length > 0 ? $"  title {titleId}" : ""));
                }
                hot.Commit();
            }

            if (Apply && undo.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(undoPath)!);
                var fresh = !File.Exists(undoPath);
                await File.AppendAllTextAsync(undoPath,
                    (fresh ? "ItemId,PreviousParsedSeriesKey,NewParsedSeriesKey,SeriesIdAtSplit\n" : "") + undo);
            }

            await console.Output.WriteLineAsync("");
            await console.Output.WriteLineAsync($"runs after the split ({perKey.Count} keys receiving rows):");
            foreach (var (key, n) in perKey.OrderByDescending(kv => kv.Value).Take(Math.Max(1, Top)))
                await console.Output.WriteLineAsync($"  {n,6}  {key}");
            await console.Output.WriteLineAsync($"keys they came from: {string.Join(", ", fromKey.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ({kv.Value})"))}");
            await console.Output.WriteLineAsync(
                $"done: {changed} row(s) re-keyed, {created} new Series row(s), {settled} already settled, "
              + $"{missing} item(s) not found, {invalid} bad line(s)"
              + (Apply ? $"; previous keys appended to {undoPath}" : " (dry run — re-run with --apply)"));
            if (Apply)
                await console.Output.WriteLineAsync("next: books-resolve --series, then books-reading-order and books-containment");
        }

        private static string Csv(string? s) =>
            s == null ? "" : s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
