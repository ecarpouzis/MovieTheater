using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Parse;

namespace MovieTheater.BooksHost.Commands
{
    /// <summary>
    /// <c>books-reparse</c> — re-run <see cref="ComicTitleParser.Parse"/> over what the database ALREADY holds
    /// (the stored file name and path, the raw `ComicEmbedded` ComicInfo, the page count) and rewrite the
    /// FORMAT half of `ComicDetail` where the answer moved. It is the sibling of `books-fix-issue-numbers`:
    /// that one re-runs the issue ladder when the ladder changes, this one re-runs the whole parse when a
    /// FORMAT rule changes — without a scan, because none of its inputs live on the share.
    ///
    /// <para><b>Deliberately narrow.</b> Only `Format`, `FormatRaw`, `IsCollection`, `VolumeNo`, `IssueNo`,
    /// `IssueSource` and `ParseNotes` are written. `ParsedSeriesKey`, `Year` and `Publisher` are NOT: they are
    /// the series identity's inputs, and rewriting them here would silently re-shape `Series` behind
    /// `books-resolve --series`. When a naming rule changes, that is a different (supervised) job.</para>
    ///
    /// <para><b>It never clears or demotes on silence.</b> Many stored values came from v1's free-text format
    /// column and v1's own notes — inputs THIS parse cannot see: 119,045 comics carry a `FormatRaw` while their
    /// `ComicEmbedded.Format` is empty. So a null answer is treated as "no opinion" and leaves the stored value
    /// alone; `FormatRaw`, `VolumeNo` and `ParseNotes` are only ever written to a non-null value; `IssueNo` is
    /// only ever CLEARED by the collected-edition rule that deliberately moves it to `VolumeNo`; and a
    /// collection-grade format is never demoted back to `SingleIssue` (that would undo v1 knowledge). Those
    /// refusals are counted on the summary line.</para>
    ///
    /// <para>Chunked by `Item.Id` — the batch query's own ordering — with a per-batch progress line, a
    /// resumable <c>--after</c> cursor and dry-run by default.</para>
    /// </summary>
    [Command("books-reparse", Description = "Re-run the comic parse over stored filenames + ComicInfo + page count and rewrite ComicDetail's format fields where they differ.")]
    public class BooksReparseCommand : ICommand
    {
        private readonly BooksHostConfiguration config;
        public BooksReparseCommand(BooksHostConfiguration config) => this.config = config;

        [CommandOption("db", Description = "books.db (default Books:DbPath).")] public string? DbPath { get; set; }
        [CommandOption("batch-size", Description = "Items per batch (default 5000).")] public int BatchSize { get; set; } = 5000;
        [CommandOption("after", Description = "Resume from this Item.Id (exclusive).")] public long After { get; set; }
        [CommandOption("max-batches", Description = "Stop after this many batches (0 = drain).")] public int MaxBatches { get; set; }
        [CommandOption("apply", Description = "Actually write. Without it the verb only counts and samples.")] public bool Apply { get; set; }
        [CommandOption("top", Description = "How many changed rows to print (default 30).")] public int Top { get; set; } = 30;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var hot = HotFile.Open(config, DbPath, dryRun: !Apply);
            var roots = hot.Pairs("SELECT Id, Path FROM LibraryRoot ORDER BY Id").Select(p => p.Item2!).ToList();

            var after = After;
            var batch = Math.Clamp(BatchSize, 100, 50_000);
            int scanned = 0, changed = 0, printed = 0, batches = 0;
            int toCollection = 0, issueDropped = 0, volumeSet = 0, formatMoved = 0, refusedDemotion = 0;

            while (true)
            {
                var rows = hot.Pairs($@"
SELECT i.Id,
       coalesce(i.FileName,'') || char(31) || coalesce(i.Path,'') || char(31) || coalesce(i.PageCount, 0) || char(31)
    || coalesce(ce.Series,'') || char(31) || coalesce(ce.Number,'') || char(31) || coalesce(ce.AltSeries,'') || char(31)
    || coalesce(ce.AltNumber,'') || char(31) || coalesce(ce.Volume,'') || char(31) || coalesce(ce.PublicationDate,'') || char(31)
    || coalesce(ce.Publisher,'') || char(31) || coalesce(ce.Format,'') || char(31)
    || cd.Format || char(31) || coalesce(cd.FormatRaw,'') || char(31) || cd.IsCollection || char(31)
    || coalesce(cd.VolumeNo,'') || char(31) || coalesce(cd.IssueNo,'') || char(31) || cd.IssueSource || char(31)
    || coalesce(cd.ParseNotes,'')
FROM Item i
JOIN ComicDetail cd ON cd.ItemId = i.Id
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
WHERE i.Id > {after} AND i.Kind = {(int)ItemKind.Comic}
ORDER BY i.Id LIMIT {batch}");
                if (rows.Count == 0) break;

                if (Apply) hot.Begin();
                foreach (var (id, payload) in rows)
                {
                    var p = payload!.Split(TargetWriter.Sep);
                    scanned++;
                    var fileName = p[0];
                    var path = p[1];
                    var pageCount = int.TryParse(p[2], out var pc) ? pc : 0;
                    var hasEmbedded = p[3].Length + p[4].Length + p[5].Length + p[6].Length + p[7].Length
                                    + p[8].Length + p[9].Length + p[10].Length > 0;
                    var meta = hasEmbedded
                        ? new ComicTitleParser.Embedded(
                            Blank(p[3]), Blank(p[4]), Blank(p[5]), Blank(p[6]),
                            int.TryParse(p[7], out var vol) ? vol : null, Blank(p[8]), Blank(p[9]), Blank(p[10]))
                        : null;

                    var parsed = ComicTitleParser.Parse(fileName, path, meta, roots, pageCount);

                    var curFormat = int.Parse(p[11]);
                    var curFormatRaw = Blank(p[12]);
                    var curIsCollection = p[13] == "1";
                    var curVolume = p[14].Length == 0 ? (int?)null : int.Parse(p[14]);
                    var curIssue = Blank(p[15]);
                    var curIssueSource = int.Parse(p[16]);
                    var curNotes = Blank(p[17]);

                    // The collected-edition rule is the only thing licensed to CLEAR an issue number.
                    var promoted = ComicTitleParser.IsCollectedEdition(
                        Path.GetFileNameWithoutExtension(fileName), pageCount);
                    // A collection-grade format never falls back to SingleIssue on a silent parse.
                    var demotion = curIsCollection && !parsed.IsCollection;
                    if (demotion) refusedDemotion++;

                    var newFormat = demotion ? (ComicFormat)curFormat : parsed.Format;
                    var newIsCollection = demotion || parsed.IsCollection;
                    var newFormatRaw = parsed.FormatRaw ?? curFormatRaw;
                    var newVolume = parsed.VolumeNo ?? curVolume;
                    // The ISSUE LADDER is not this verb's business — `books-fix-issue-numbers` owns it, and a
                    // stored number may be a hand-checked or v1-migrated answer the ladder would not reproduce
                    // ("…Case Files v14.5" is stored as 14.5; the ladder reads 5). The one issue-number move
                    // made here is the collected-edition CLEAR, which is the fault being fixed.
                    var newIssue = promoted ? null : curIssue;
                    var newIssueSource = promoted ? ParseSource.None : (ParseSource)curIssueSource;
                    var newNotes = parsed.ParseNotes ?? curNotes;

                    // `ParseNotes` alone is NOT a reason to write: the stored notes carry v1 phrasings this
                    // parser never emits, so comparing them would rewrite the whole library for nothing. The
                    // note follows the fields — it is refreshed only when a field actually moved.
                    var same = curFormat == (int)newFormat
                        && string.Equals(curFormatRaw, newFormatRaw, StringComparison.Ordinal)
                        && curIsCollection == newIsCollection
                        && curVolume == newVolume
                        && string.Equals(curIssue, newIssue, StringComparison.Ordinal)
                        && curIssueSource == (int)newIssueSource;
                    if (same) continue;

                    changed++;
                    if (!curIsCollection && newIsCollection) toCollection++;
                    if (curIssue != null && newIssue == null) issueDropped++;
                    if (curVolume != newVolume && newVolume != null) volumeSet++;
                    if (curFormat != (int)newFormat) formatMoved++;

                    if (printed++ < Math.Max(1, Top))
                        await console.Output.WriteLineAsync(
                            $"  {id,7}  fmt {(ComicFormat)curFormat}->{newFormat}  coll {(curIsCollection ? 1 : 0)}->{(newIsCollection ? 1 : 0)}"
                            + $"  issue '{curIssue}'->'{newIssue}'  vol '{curVolume}'->'{newVolume}'  {pageCount}p  {fileName}");

                    hot.Update("ComicDetail", "ItemId", id, new
                    {
                        Format = newFormat,
                        FormatRaw = newFormatRaw,
                        IsCollection = newIsCollection,
                        VolumeNo = newVolume,
                        IssueNo = newIssue,
                        IssueSource = newIssueSource,
                        ParseNotes = newNotes,
                    });
                }
                if (Apply) hot.Commit();

                after = rows[^1].Item1;
                batches++;
                var remaining = hot.Scalar<long>($"SELECT count(*) FROM Item WHERE Id > {after} AND Kind = {(int)ItemKind.Comic}");
                await console.Output.WriteLineAsync(
                    $"{{ processed: {rows.Count}, remaining: {remaining}, nextCursor: \"{after}\", changed: {changed} }}  [reparse]");
                if (MaxBatches > 0 && batches >= MaxBatches) break;
            }

            await console.Output.WriteLineAsync(
                $"done: scanned {scanned}, changed {changed} "
                + $"{{ toCollection: {toCollection}, issueNoDropped: {issueDropped}, volumeNoSet: {volumeSet}, "
                + $"formatMoved: {formatMoved}, refusedDemotion: {refusedDemotion} }}"
                + (Apply ? " and were written" : " (dry run — re-run with --apply)"));
            if (Apply)
                await console.Output.WriteLineAsync(
                    "next: books-resolve --series -> books-collected-editions -> books-reading-order -> books-containment -> books-resolve");
        }

        private static string? Blank(string s) => s.Length == 0 ? null : s;
    }
}
