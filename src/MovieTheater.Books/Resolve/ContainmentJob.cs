using System.Globalization;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Parse;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-containment</c> — the per-series collection-containment model (`CollectionNode`) behind the
    /// smart reading list: which edition collects which issues, how the editions nest, and which track is the
    /// one you actually read.
    ///
    /// <para><b>How a series is built.</b> Every book gets a <see cref="CollectionLevels"/> level (Issue &lt;
    /// Volume &lt; Book &lt; Omnibus). The BASE level is the FINEST level that forms a real run — three or more
    /// books — so one stray issue-level special cannot demote a whole shelf of volumes to "containers of a
    /// one-book base". Books at or below the base are the PRIMARY track and get positions 1..N; every level
    /// above is a CONTAINER.</para>
    ///
    /// <para><b>A container's span is never fabricated from page count.</b> The standalone tried that once and
    /// labelled "Saga Vol 7" as collecting #1–6. A span comes ONLY from a `CollectedEditionSpan` row, chosen by
    /// <see cref="SpanSelection"/> (Curated, then Cv matched on the edition title, then a complete LOCG table of
    /// contents, then Gcd-by-title, then a partial LOCG, then issue-keyed Gcd; degenerate "#N-#N" claims about a
    /// collection are discarded); without one
    /// the edition stays a labelled leaf — you own the book, we do not claim to know its contents.</para>
    ///
    /// <para><b>The over-collection guard.</b> A `Series` that conflates runs which restart numbering makes the
    /// same issue number appear many times, so a "#1-6" edition spuriously overlaps every run's #1-6 (a real
    /// case swallowed 695 issues across 46 runs). When the matched SPAN is far wider than the range could hold,
    /// the edition keeps its correct label and claims no children.</para>
    ///
    /// <para>Chunked by `Series.Id` and rewritten per series, exactly like the reading order it reads.</para>
    /// </summary>
    public static class ContainmentJob
    {
        public const string DerivedName = "CollectionNode";

        /// <summary>Three or more books at a level makes it a real run.</summary>
        public const int RunFloor = 3;

        public sealed class Book
        {
            public int ItemId;
            public int SeriesId;
            public CollectionLevel Level;
            public int PageCount;
            public int? VolumeNo;
            public int? ReadIndex;
            public string? ReadDate;
            public double? ReadNumber;
            /// <summary>The parsed `ComicDetail.IssueNo`, when it is a number. The ladder's coordinate.</summary>
            public double? IssueNumber;
            /// <summary>`ReadingOrderEntry.ReadTier` — <see cref="ReadingOrderParser.TierMain"/> unless the file is an
            /// annual or a special / one-shot (by Format OR by the word in its name). An annual is numbered in its OWN
            /// series — "Aquaman Annual #1-5" is not Aquaman #1-5 — so it never takes a place on the run's ladder.</summary>
            public int ReadTier = ReadingOrderParser.TierMain;
            public double? SpanFromStart, SpanFromEnd;
            public EditionSource RangeSource = EditionSource.Cv;
            /// <summary>The winning span's note — the only place a NON-CONTIGUOUS range states its holes.</summary>
            public string? RangeNote;
            /// <summary>The RUNS the range counts in (<c>CollectedEditionSpanRun</c>), each with its own range
            /// in that run's numbering; null when nobody said. Several entries on one leg = a book collecting
            /// several runs (a trade of two minis, an omnibus).</summary>
            public IReadOnlyList<ReadingOrderJob.SpanRun>? Runs;
            /// <summary>The runs THIS FILE is in, from its own matched <c>ItemProviderLink</c> rows (Cv volume,
            /// Gcd series). Null when nothing linked it — which on a ripper-renumbered shelf is most files.</summary>
            public IReadOnlyDictionary<Provider, string>? FileRuns;
            /// <summary>The issue number the ComicVine link gives this file, when it is a number. On a shelf a
            /// ripper renumbered 001-040 this is the coordinate the run's own editions are counted in.</summary>
            public double? ProviderIssueNumber;
            /// <summary>The coordinates this container's own note DENIES; null when its range has no hole.</summary>
            public HashSet<double>? ExcludedCoords;
            /// <summary>Every number this container's own quoted page NAMES; the only way a point issue gets in.</summary>
            public HashSet<double>? QuotedCoords;
            /// <summary>`ComicDetail.IsCollection` — the flag the decision pass's coverage is built on (§14.13).</summary>
            public bool IsCollection;
            public TrackRole TrackRole = TrackRole.Primary;
            public int SpanStart, SpanEnd, ContainsCount = 1;
            public int? ParentItemId;
            public string? SpanLabel;
            public SpanSource SpanSource = SpanSource.Inferred;
        }

        public sealed record BatchResult(int Processed, long Remaining, long? NextCursor, int Rows)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [containment, nodes: {Rows}]";
        }

        public static BatchResult RunBatch(TargetWriter hot, long afterSeriesId, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var seriesIds = hot.Pairs($"SELECT Id, '' FROM Series WHERE Id > {afterSeriesId} ORDER BY Id LIMIT {batchSize}")
                .Select(p => (int)p.Item1).ToList();
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0);

            var written = 0;
            foreach (var seriesId in seriesIds)
            {
                var books = LoadBooks(hot, seriesId);
                if (books.Count == 0) { hot.Exec($"DELETE FROM CollectionNode WHERE SeriesId = {seriesId}"); continue; }
                BuildSeries(books, LoadShelfRuns(hot, seriesId));
                hot.Exec($"DELETE FROM CollectionNode WHERE SeriesId = {seriesId}");
                foreach (var b in books)
                {
                    hot.Upsert("CollectionNode", new
                    {
                        ItemId = b.ItemId,
                        SeriesId = b.SeriesId,
                        Level = b.Level,
                        TrackRole = b.TrackRole,
                        SpanStart = b.SpanStart,
                        SpanEnd = b.SpanEnd,
                        ContainsCount = b.ContainsCount,
                        ParentItemId = b.ParentItemId,
                        SpanSource = b.SpanSource,
                        SpanLabel = b.SpanLabel,
                    });
                    written++;
                }
            }

            var next = seriesIds[^1];
            return new BatchResult(seriesIds.Count, hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}"), next, written);
        }

        /// <summary>The persisted cursor — the SAME key the admin recompute route pages with (see <see cref="JobCursor"/>).</summary>
        public const string CursorKey = "books:recompute:containment";

        /// <summary>Drain every series; the cursor persists per batch and <paramref name="resume"/> continues from it.</summary>
        public static int RunAll(TargetWriter hot, int batchSize, Action<string> log, bool resume = false)
        {
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            var total = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, cursor, batchSize);
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                total += r.Rows;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            Stamp(hot);
            hot.Commit();
            return total;
        }

        /// <summary>
        /// The runs the SHELF itself is, as the identity pass stored them: the ComicVine volume on
        /// <c>Series</c>, and the GCD series on the shelf's own <c>SeriesKeyLink(Provider=Gcd, Status=Manual)</c>
        /// row (its own parsed key first, then any alias of it). A file on a shelf whose identity IS the run a
        /// container names needs no link of its own — it is in that run by living here.
        /// </summary>
        internal static Dictionary<Provider, string> LoadShelfRuns(TargetWriter hot, int seriesId)
        {
            var runs = new Dictionary<Provider, string>();
            foreach (var (_id, payload) in hot.Pairs($@"
SELECT s.Id, coalesce(s.CvVolumeId,'') || char(31)
    || coalesce((SELECT k.ProviderKey FROM SeriesKeyLink k
                 WHERE k.Provider = {(int)Provider.Gcd} AND k.Status = {(int)LinkStatus.Manual}
                   AND k.ProviderKey IS NOT NULL AND k.ParsedKey = s.ParsedKey),
                (SELECT k.ProviderKey FROM SeriesKeyLink k
                 JOIN SeriesAlias a ON a.ParsedKey = k.ParsedKey AND a.SeriesId = s.Id
                 WHERE k.Provider = {(int)Provider.Gcd} AND k.Status = {(int)LinkStatus.Manual}
                   AND k.ProviderKey IS NOT NULL LIMIT 1), '')
FROM Series s WHERE s.Id = {seriesId}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p[0].Length > 0) runs[Provider.Cv] = p[0];
                if (p.Length > 1 && p[1].Length > 0) runs[Provider.Gcd] = p[1];
            }
            return runs;
        }

        private static List<Book> LoadBooks(TargetWriter hot, int seriesId)
        {
            var spans = ReadingOrderJob.LoadSpans(hot, seriesId);

            // Which run each FILE is in by its own matched link, and the issue number that link gives it.
            // SecondaryKey is where this pass's shelf-level rollups read the volume / series from; CvIssue is
            // preferred for ComicVine because it is the record itself rather than a denormalised copy.
            var fileRuns = new Dictionary<int, Dictionary<Provider, string>>();
            var providerNumber = new Dictionary<int, double>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT l.ItemId, l.Provider || char(31)
    || coalesce((SELECT ci.VolumeId FROM CvIssue ci
                 WHERE l.Provider = {(int)Provider.Cv} AND ci.Id = CAST(l.ProviderKey AS INTEGER)),
                coalesce(l.SecondaryKey, '')) || char(31)
    || coalesce((SELECT ci.IssueNumber FROM CvIssue ci
                 WHERE l.Provider = {(int)Provider.Cv} AND ci.Id = CAST(l.ProviderKey AS INTEGER)), '')
FROM ItemProviderLink l JOIN Item i ON i.Id = l.ItemId
WHERE i.SeriesId = {seriesId} AND l.Status IN {LinkStatuses.UsableSql}
  AND l.Provider IN ({(int)Provider.Cv}, {(int)Provider.Gcd})"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p.Length < 2 || p[1].Length == 0) continue;
                var id = (int)itemId;
                if (!fileRuns.TryGetValue(id, out var map)) fileRuns[id] = map = new Dictionary<Provider, string>();
                map[(Provider)int.Parse(p[0])] = p[1];
                if (p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pn))
                    providerNumber[id] = pn;
            }

            var books = new List<Book>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id,
       coalesce(cd.Format, 13) || char(31) || coalesce(cd.FormatRaw,'') || char(31) || coalesce(i.FileName,'') || char(31)
    || coalesce(i.PageCount, 0) || char(31) || coalesce(cd.VolumeNo,'') || char(31)
    || coalesce(ro.ReadIndex,'') || char(31) || coalesce(ro.ReadDate,'') || char(31) || coalesce(ro.ReadNumber,'')
    || char(31) || coalesce(cd.IssueNo,'') || char(31) || coalesce(cd.IsCollection, 0)
    || char(31) || coalesce(ro.ReadTier, 0)
FROM Item i
LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
LEFT JOIN ReadingOrderEntry ro ON ro.ItemId = i.Id
WHERE i.SeriesId = {seriesId} AND i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0
ORDER BY i.Id"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                var pageCount = int.Parse(p[3]);
                var book = new Book
                {
                    ItemId = (int)itemId,
                    SeriesId = seriesId,
                    Level = CollectionLevels.Resolve((ComicFormat)int.Parse(p[0]), Blank(p[1]), p[2], pageCount),
                    PageCount = pageCount,
                    VolumeNo = p[4].Length == 0 ? null : int.Parse(p[4]),
                    ReadIndex = p[5].Length == 0 ? null : int.Parse(p[5]),
                    ReadDate = Blank(p[6]),
                    ReadNumber = p[7].Length == 0 ? null : double.Parse(p[7], CultureInfo.InvariantCulture),
                    IssueNumber = p.Length > 8 && double.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var ino)
                        ? ino : null,
                    IsCollection = p.Length > 9 && p[9] == "1",
                    ReadTier = p.Length > 10 && int.TryParse(p[10], out var tier) ? tier : ReadingOrderParser.TierMain,
                    FileRuns = fileRuns.TryGetValue((int)itemId, out var fr) ? fr : null,
                    ProviderIssueNumber = providerNumber.TryGetValue((int)itemId, out var pn) ? pn : null,
                };
                if (spans.TryGetValue(book.ItemId, out var sp))
                {
                    book.SpanFromStart = sp.Start;
                    book.SpanFromEnd = sp.End;
                    book.RangeSource = sp.Source;
                    book.RangeNote = sp.Note;
                    book.Runs = sp.Runs;
                }
                books.Add(book);
            }
            return books;
        }

        /// <summary>
        /// The pure decision — one series' books in, their nodes' fields set in place.
        /// <paramref name="shelfRuns"/> is the shelf's OWN identity per leg (<see cref="LoadShelfRuns"/>): it
        /// is what lets an unlinked file be in the run a container names, because the shelf is that run.
        /// </summary>
        public static void BuildSeries(List<Book> books, IReadOnlyDictionary<Provider, string>? shelfRuns = null)
        {
            var levelCounts = books.GroupBy(b => b.Level).ToDictionary(g => g.Key, g => g.Count());
            var baseLevel = levelCounts.Where(kv => kv.Value >= RunFloor).Select(kv => (CollectionLevel?)kv.Key).Min()
                            ?? books.Min(b => b.Level);

            var baseBooks = books.Where(b => b.Level <= baseLevel)
                .OrderBy(b => b.ReadIndex ?? int.MaxValue)
                .ThenBy(b => b.ReadNumber ?? double.MaxValue)
                .ThenBy(b => b.ReadDate ?? "9999", StringComparer.Ordinal)
                .ThenBy(b => b.ItemId)
                .ToList();

            var n = baseBooks.Count;
            var num = new double?[n];
            for (var i = 0; i < n; i++)
            {
                var b = baseBooks[i];
                b.TrackRole = TrackRole.Primary;
                b.SpanStart = b.SpanEnd = i + 1;
                b.ContainsCount = 1;
                b.SpanLabel = null;
                // The ISSUE number, not the reading-order number. They are the same on 87,069 of the 87,125
                // single issues that have both — and the 56 that differ are all one shape: ComicVine numbering
                // by ARC where the shelf numbers continuously. "Baltimore 006 - The Curse Bells 01 (of 05)" is
                // the sixth Baltimore comic and the first Curse Bells comic, ComicVine says 1, and every
                // collected edition on that shelf claims a range in the continuous numbering (#6-10, #11-15,
                // ... #36-40). Ordering a shelf by the arc is defensible; nesting it by the arc is not, because
                // the range it is being nested INTO is not in that coordinate. Reading order keeps its own
                // answer — this changes nothing but the containment ladder.
                num[i] = b.IssueNumber ?? b.ReadNumber ?? b.VolumeNo ?? i + 1;
            }

            foreach (var b in books.Where(b => b.Level > baseLevel))
            {
                b.TrackRole = TrackRole.Container;
                b.SpanStart = b.SpanEnd = 0;
                b.ContainsCount = 0;
                b.SpanLabel = null;
                b.SpanSource = SpanSource.None;
            }

            // A container CONTAINS a base book when their issue ranges overlap. A base book's range is its own
            // span when it has one (a TPB volume), its issue number when it is a single issue, and NOTHING when
            // it is a volume with no known range — a volume number is not an issue number and cannot be nested.
            //
            // A SINGLE ISSUE MAY CARRY A SPAN ANYWAY, and it is always a bad provider link. LOCG matched
            // `Lobster Johnson 001 - The Iron Prometheus 01 (of 05)` — a 27-page floppy — to the TRADE's record
            // and wrote it a span of #2-4 "5 contained"; fifteen of that shelf's thirty-one issues carry one.
            // Believing them puts the floppy in the ladder at someone else's coordinates, and the whole shelf
            // shifts: Vol. 01 collected nothing and Vol. 02 collected #8-10. The file settles it before any
            // provider does — twenty-seven pages is not five issues — so the span is believed only when the
            // page arithmetic can hold it, and <see cref="PageArithmetic"/> already owns that judgement.
            var baseRange = new (double Lo, double Hi)[n];
            for (var i = 0; i < n; i++)
            {
                var bb = baseBooks[i];
                var hasSpan = bb.SpanFromStart.HasValue && bb.SpanFromEnd.HasValue;
                // A base COLLECTION's own span defines the ladder only when it was JUDGED. A provider's
                // title match on "Hellboy Vol. 03 - The Chained Coffin and Others" produced #88-91, and the
                // twelve Hellboy volumes between them claim 1-4, 1-5, 88-91, 14-19, 1-4, 1-2, 1-2, 1-6, 1-8,
                // 1-3, 1-2 and 1-3 — no ladder at all. The omnibuses above them are ranges over VOLUMES, and
                // the coordinate has to be the same on both sides or nothing can nest (§4.2). Where the
                // shelf's own judgement supplies the span it is used, which is what keeps Saga's volumes
                // reading in issue numbers under their books.
                // A single issue's span must be a RANGE to be a ladder coordinate. A degenerate "#26-26" on a
                // 22-page floppy is the provider echoing its own match key — the shape SpanSelection.
                // IsDiscardable already refuses — and believing it put `100 Years Quest 136` into the ladder at
                // 26, where its volume claiming chapters 19-27 duly swallowed it. Page arithmetic cannot catch
                // that one: 22 pages for one issue is perfectly plausible. Only the width tells them apart.
                var spanDefinesTheLadder = hasSpan && (bb.Level == CollectionLevel.Issue
                    ? bb.SpanFromEnd!.Value > bb.SpanFromStart!.Value
                      && PageArithmetic.Flag(bb.PageCount, bb.SpanFromStart!.Value, bb.SpanFromEnd!.Value) != "thin"
                    : bb.RangeSource == EditionSource.Curated);
                if (spanDefinesTheLadder) baseRange[i] = (bb.SpanFromStart!.Value, bb.SpanFromEnd!.Value);
                // An annual or a special is numbered in its OWN series, so its number is not a coordinate on
                // this run's ladder at all: "Aquaman Annual 001-005" sat under Book 01 (#0-8), "Iron Man
                // Annual 001" under Vol. 01 - Big Iron, four Deathstroke annuals under Assassins. Seventy files
                // nested this way and not one of them was in the book. The tier is the reading order's — set
                // from Format, or from the word in the file name when the Format is wrong — so it is the one
                // judgement of "is this an annual" the library already makes. Like a volume with no known
                // range, such a file stays on the primary track and simply cannot be nested.
                else if (bb.Level == CollectionLevel.Issue && OffTheLadder(bb)) baseRange[i] = (double.NaN, double.NaN);
                else if (bb.Level == CollectionLevel.Issue) baseRange[i] = (num[i] ?? i + 1, num[i] ?? i + 1);
                else if (bb.VolumeNo is int volumeOrdinal) baseRange[i] = (volumeOrdinal, volumeOrdinal);
                else baseRange[i] = (double.NaN, double.NaN);
            }

            if (n > 0)
                foreach (var b in books.Where(b => b.Level > baseLevel && b.SpanFromStart.HasValue && b.SpanFromEnd.HasValue))
                {
                    double es = b.SpanFromStart!.Value, ee = b.SpanFromEnd!.Value;
                    // A range with a hole in it collects only what it holds. The note is where the hole is
                    // stated — "Hellboy Omnibus Vol. 03 collects 8, 9, 12" over a bounding 8-12 — and without
                    // this the two short-story volumes between them nest inside a book that never printed
                    // them. Same evidence the de-duplication reads, so the shelf and the file agree.
                    var excluded = SpanEvidence.ExcludedIssues(b.RangeNote, es, ee);
                    b.ExcludedCoords = excluded;
                    var quoted = SpanEvidence.QuotedIssues(b.RangeNote);
                    b.QuotedCoords = quoted;
                    int lo = int.MaxValue, hi = int.MinValue;
                    for (var i = 0; i < n; i++)
                    {
                        if (double.IsNaN(baseRange[i].Lo)) continue;
                        // …and the coordinate has to be in the RUN this range is counted in, or the file is
                        // not measured against it at all (see CoordAgainst). When that run's row carries its
                        // OWN range — a trade collecting two minis states each mini's #1-5 — that range is
                        // what this file is measured against, not the span's bounding one.
                        if (!CoordAgainst(baseBooks[i], baseRange[i], b, shelfRuns, out var mine, out var vs)) continue;
                        var (clo, chi) = vs ?? (es, ee);
                        if (mine.Lo > chi || mine.Hi < clo) continue;
                        if (excluded != null && WhollyExcluded(mine, excluded)) continue;
                        if (!PointIssueNamed(baseBooks[i], mine, quoted)) continue;
                        if (i < lo) lo = i;
                        if (i > hi) hi = i;
                    }

                    // The authoritative range is ALWAYS surfaced as the label, even when nothing it collects is
                    // owned — it still "collects #1-20"; there is just nothing to drill into.
                    b.SpanSource = SpanSourceFor(b.RangeSource);
                    b.SpanLabel = es == ee ? $"#{Fmt(es)}" : $"#{Fmt(es)}-{Fmt(ee)}";

                    var rangeSize = ee - es + 1;
                    var span = lo == int.MaxValue ? 0 : hi - lo + 1;
                    // Guard on the SPAN, not the match count: a clean collection's issues are CONTIGUOUS in the
                    // base sequence so span ≈ rangeSize; a conflated-run collision matches a handful scattered
                    // far apart, giving a small count yet an enormous span.
                    //
                    // The span is measured in COORDINATES, not in ladder positions. The library holds many
                    // issues two and three times over — a chronology-tree rip beside a run rip — and counting
                    // positions makes every one of those duplicates look like scatter. `Green Lantern Vol. 02`
                    // collects #7-13, seven issues held as twenty-one files: twenty-one positions against an
                    // allowance of twelve, so a range read straight off the book's own copyright page was
                    // thrown away. Measured in coordinates it is seven against seven. Library-wide this was
                    // discarding 269 judged ranges over 3,518 files.
                    //
                    // The guard's intent is untouched, because scatter is a property of the COORDINATES: a
                    // claim of #1-5 whose matches land at #1 and #300 still covers hundreds of distinct
                    // coordinates between them and is still rejected.
                    //
                    // A book that collects SEVERAL runs is allowed all of them: the coordinates are counted
                    // per run (a #1 of one mini and a #1 of another are two different issues, and each run's
                    // own width is its own allowance), which is what keeps an omnibus of two five-issue minis
                    // from reading as ten coordinates scattered over a claim of five.
                    if (lo != int.MaxValue)
                    {
                        var seen = new HashSet<(double, double, double)>();
                        var runsUsed = new HashSet<(double Lo, double Hi)>();
                        for (var i = lo; i <= hi; i++)
                        {
                            if (double.IsNaN(baseRange[i].Lo)) continue;
                            // the same coordinate — and the same run's range — the matching used, so a file
                            // this range cannot measure does not widen the scatter it is judged by
                            if (!CoordAgainst(baseBooks[i], baseRange[i], b, shelfRuns, out var mine, out var vs))
                                continue;
                            if (!PointIssueNamed(baseBooks[i], mine, quoted)) continue;
                            var bucket = vs ?? (es, ee);
                            runsUsed.Add(bucket);
                            seen.Add((bucket.Lo, bucket.Hi, mine.Lo));
                        }
                        span = seen.Count;
                        if (runsUsed.Count > 0) rangeSize = runsUsed.Sum(r => r.Hi - r.Lo + 1);
                    }
                    if (lo == int.MaxValue || span > rangeSize * 1.3 + 3) { b.SpanStart = b.SpanEnd = 0; b.ContainsCount = 0; }
                    else { b.SpanStart = lo + 1; b.SpanEnd = hi + 1; b.ContainsCount = span; }
                }

            var containers = books.Where(b => b.Level > baseLevel).ToList();
            if (containers.Count == 0) return;
            var coordOf = new Dictionary<int, (double, double)>();
            for (var i = 0; i < n; i++) coordOf[baseBooks[i].ItemId] = (baseRange[i].Lo, baseRange[i].Hi);
            foreach (var b in books)
            {
                // A container that collects nothing we own has no position to be contained BY, so it stays a
                // top-level leaf — otherwise every "empty" container nests under any other empty one.
                //
                // Unless BOTH sides state a JUDGED range, in which case the ranges alone settle it and the
                // "every empty one nests under every other" failure cannot happen. `Saga Book 03` collects
                // #37-54 and we own only #49-54 as files, so `Vol. 07` (#37-42) and `Vol. 08` (#43-48) have
                // no position of their own and sat flat under "Editions without a known range" while `Vol.
                // 09` — the one holding the files — nested correctly. The range IS known; there is simply
                // nothing of it on disk. 419 editions on 75 shelves are in that shape. A provider's span is
                // never enough here: an unjudged claim must not nest anything, and equal ranges are two
                // editions of the same material, never one inside the other.
                if (b.SpanEnd <= 0)
                {
                    b.ParentItemId = null;
                    if (b.TrackRole != TrackRole.Container || b.RangeSource != EditionSource.Curated
                        || !b.SpanFromStart.HasValue || !b.SpanFromEnd.HasValue) continue;
                    double bs = b.SpanFromStart.Value, be = b.SpanFromEnd.Value;
                    Book? byRange = null;
                    foreach (var p in containers)
                    {
                        if (p.Level <= b.Level || p.ItemId == b.ItemId) continue;
                        if (p.RangeSource != EditionSource.Curated || !p.SpanFromStart.HasValue || !p.SpanFromEnd.HasValue) continue;
                        // "A container that is not flagged as a collection is a container nobody judged"
                        // (PLAN §14.13) — the decision pass's coverage is built on `IsCollection = 1`, so a
                        // book without the flag was never read, and a range on it is unreviewed. The
                        // positional pass is kept clear of them by the data; this pass reaches editions the
                        // positional one never could, so it has to ask. `Iron Man Epic Collection` (S9575) is
                        // the shelf that bought it: a mixed shelf of unrelated trades, each judged in ITS OWN
                        // coordinates, where item 82200 (`Iron Man 2020 (2018)`, unflagged, whose #1-6 is
                        // really Machine Man #1-4) took `Ultimate Iron Man` #1-5 inside it.
                        if (!p.IsCollection) continue;
                        // Two judged ranges counted in DIFFERENT runs are not measured against each other at
                        // all (see <see cref="ComparableRuns"/>): Hellboy Vol. 01's #1-4 is Seed of Destruction
                        // and Vol. 02's #1-5 is Wake the Devil, and neither the "#1-4 is inside #1-5" reading
                        // nor the "equal ranges are one comic twice" reading is true of them.
                        // …and when both name the SAME run and that run's rows carry ranges, THOSE are the
                        // ranges compared: the shelf-numbered span of an omnibus says nothing about where a
                        // mini's own #1-5 sits inside it.
                        if (!RunOverlay(p, b, out var pr, out var br)) continue;
                        double ps = pr?.Lo ?? p.SpanFromStart.Value, pe = pr?.Hi ?? p.SpanFromEnd.Value;
                        double cs = br?.Lo ?? bs, ce = br?.Hi ?? be;
                        if (ps > cs || pe < ce) continue;
                        // …the same material, twice — not a nesting. But an omnibus that names a run the trade
                        // does not is not the same material even where their ranges for the run they share are
                        // equal: it collects that whole mini AND another one.
                        if (ps == cs && pe == ce && SameMaterial(p, b)) continue;
                        // The same page and hole tests the positional pass makes, for the same reasons.
                        if (p.PageCount > 0 && b.PageCount > 0 && p.PageCount < b.PageCount) continue;
                        if (p.ExcludedCoords != null && WhollyExcluded((bs, be), p.ExcludedCoords)) continue;
                        if (byRange == null || pe - ps < byRange.SpanFromEnd!.Value - byRange.SpanFromStart!.Value) byRange = p;
                    }
                    b.ParentItemId = byRange?.ItemId;
                    continue;
                }
                Book? parent = null;
                var own = coordOf.TryGetValue(b.ItemId, out var c) ? c : (double.NaN, double.NaN);
                // An annual or a special has no coordinate on this ladder (see OffTheLadder), and a position
                // window is not a range: it must not be swept in by sitting between two issues that are.
                if (b.Level == CollectionLevel.Issue && b.TrackRole == TrackRole.Primary && OffTheLadder(b))
                {
                    b.ParentItemId = null;
                    continue;
                }
                foreach (var p in containers)
                {
                    if (p.Level <= b.Level || p.ItemId == b.ItemId) continue;
                    // The same run test the range-based pass makes below: a container whose judged range is
                    // counted in a different run than the child's cannot hold it, whatever the positions say.
                    if (!ComparableRuns(p, b)) continue;
                    // …and for a loose issue file the question is the other one: is this FILE in the run that
                    // range is counted in? The answer also chooses the coordinate it is measured by.
                    if (!CoordAgainst(b, own, p, shelfRuns, out var mine, out var vs)) continue;
                    if (p.SpanStart > b.SpanStart || p.SpanEnd < b.SpanEnd) continue;
                    // A book cannot be inside a book with fewer pages than itself. The positional tests
                    // above are about coordinates and say nothing about size, so where two editions of the
                    // same material both became containers the smaller one could swallow the larger:
                    // `Sex Criminals - The Complete Edition` (715pp) sat inside `Big Hard Sex Criminals
                    // Book 02` (252pp), and `The Flash Vol. 14` (#750-755, 191pp) inside the 103pp deluxe
                    // edition of #750 alone, because both ranges open at the same coordinate. Five edges
                    // were impossible in this way. Unknown page counts are not evidence, so a zero on
                    // either side skips the test rather than rejecting the parent.
                    if (p.PageCount > 0 && b.PageCount > 0 && p.PageCount < b.PageCount) continue;
                    // A container's node range is a CONTIGUOUS run of base positions, so a hole in its real
                    // contents survives into the parent test unless it is asked again here. Hellboy Omnibus
                    // Vol. 03 spans positions 8..12 and holds 8, 9 and 12; without this, the two short-story
                    // volumes between them nest inside a book that never printed them.
                    if (p.ExcludedCoords != null && !double.IsNaN(mine.Item1)
                        && WhollyExcluded(mine, p.ExcludedCoords)) continue;
                    // …and the same question for a point issue the window happens to cover: #23.1 sits
                    // between #23 and #24 in the reading order, and only the page can put it in the book.
                    if (!double.IsNaN(mine.Item1) && !PointIssueNamed(b, mine, p.QuotedCoords)) continue;
                    // And the child's own coordinate has to be INSIDE the range the container claims. The
                    // test above is positional, and a position window is not a range: `Aquaman Vol. 01`
                    // collects #1-8, but the window that holds those eight also holds files numbered outside
                    // them, and without this they nest inside a book that never printed them. Thirteen
                    // containers were doing that, one of them nesting thirty-five coordinates its own range
                    // excludes. Over-claiming is the direction that loses files, so the range is asked again
                    // here — exactly as the gap is on the line above.
                    // …in the numbering the file is counted in: `vs` is the container's range for THIS file's
                    // run when that run's row carries one (a two-mini trade), else the span's own.
                    if ((vs is not null || (p.SpanFromStart.HasValue && p.SpanFromEnd.HasValue))
                        && !double.IsNaN(mine.Item1))
                    {
                        var (plo, phi) = vs ?? (p.SpanFromStart!.Value, p.SpanFromEnd!.Value);
                        if (mine.Item2 < plo || mine.Item1 > phi) continue;
                    }
                    if (parent == null || IsInnerThan(p, parent)) parent = p;
                }
                b.ParentItemId = parent?.ItemId;
            }
        }

        /// <summary>
        /// Which of two eligible containers is the TIGHTER parent. The positional span is the first word — the
        /// smallest window that still covers the child — but positions are measured in the base books we OWN,
        /// so two containers of different sizes routinely tie there.
        ///
        /// <para>`Saga Book 03` collects #37-54 and `Vol. 09` collects #49-54; we own only #49-54 as files, so
        /// both measure 8-13 and the tie went to whichever came first by item id — the BOOK — putting issues
        /// #49-54 beside the Volume that holds them instead of inside it. On an equal window the INNERMOST
        /// container wins: the lower <see cref="CollectionLevel"/> first (a Volume sits inside a Book), then
        /// the narrower JUDGED range, then the smaller page count. Unknown page counts are not evidence, so a
        /// zero on either side leaves the incumbent alone — the same rule the eligibility tests use.</para>
        ///
        /// <para><b>Ahead of all of it: a book nobody flagged as a collection never displaces one that is</b>
        /// (PLAN §14.13, the same guard the judged-range pass carries). The positional pass has no
        /// `IsCollection` test — it was kept clear of unflagged containers by the item order alone, and
        /// preferring the inner level took that accident away: `Green Hornet - Sky Lights Collection` (Level
        /// Volume, unflagged, S7899) took five files off `Green Hornet Omnibus v01`, and
        /// `The Amazing Spider-Man (2023) (DCP Webrips)` (S34339) took three off the Spider-Man omnibus. Both
        /// tripped `audit_containment`'s "a container that is not flagged as a collection". It costs exactly
        /// those two edges; eligibility is untouched.</para>
        /// </summary>
        private static bool IsInnerThan(Book candidate, Book incumbent)
        {
            int cw = candidate.SpanEnd - candidate.SpanStart, iw = incumbent.SpanEnd - incumbent.SpanStart;
            if (cw != iw) return cw < iw;
            if (candidate.IsCollection != incumbent.IsCollection) return candidate.IsCollection;
            if (candidate.Level != incumbent.Level) return candidate.Level < incumbent.Level;
            double cr = JudgedWidth(candidate), ir = JudgedWidth(incumbent);
            if (cr != ir) return cr < ir;
            if (candidate.PageCount > 0 && incumbent.PageCount > 0 && candidate.PageCount != incumbent.PageCount)
                return candidate.PageCount < incumbent.PageCount;
            return false;
        }

        /// <summary>
        /// Whether a loose ISSUE file may be measured against one container's judged range at all, and in
        /// which coordinate.
        ///
        /// <para><b>The defect.</b> Baltimore (S1810) and Lobster Johnson (S10839) are chains of miniseries a
        /// ripper renumbered continuously — 001-040, 001-031 — with no provider link on a single file. Once a
        /// `C` line moves a trade's range out of the ripper's numbering into its own mini's (`Vol. 02 - The
        /// Burning Hand` collects #1-5 OF the Burning Hand, not #6-10 of the folder), the positional pass
        /// would hand it files 001-005 — which are The Iron Prometheus. The numbers match; they are numbers
        /// about a different comic.</para>
        ///
        /// <para><b>The rule.</b> A range that names a run counts a file only when the file is IN that run:
        /// by its own matched link (<see cref="Book.FileRuns"/> — the ComicVine volume of its issue, the GCD
        /// series of its issue), or because the shelf's own identity IS that run, which is the ordinary case
        /// and changes nothing. A file that is in the run by its own link but was renumbered by the ripper is
        /// then measured by the PROVIDER's issue number, because that is the numbering the range is written
        /// in. A range with no run refs is measured exactly as it is today.</para>
        ///
        /// <para>On a refused shelf whose files carry no links, such a range therefore collects nothing, and
        /// the books sit flat. That is the honest answer until the split pass gives each mini its own shelf —
        /// and it is the direction that does not lose files.</para>
        /// </summary>
        internal static bool CoordAgainst(Book file, (double Lo, double Hi) own, Book container,
            IReadOnlyDictionary<Provider, string>? shelfRuns, out (double Lo, double Hi) coord) =>
            CoordAgainst(file, own, container, shelfRuns, out coord, out _);

        /// <inheritdoc cref="CoordAgainst(Book, ValueTuple{double, double}, Book, IReadOnlyDictionary{Provider, string}, out ValueTuple{double, double})"/>
        /// <param name="against">The container's range IN THE RUN THE FILE IS IN, when that run's row carries
        /// one — a book collecting two minis states each mini's own #1-5, and a file of the second mini must be
        /// measured against THAT range, not against the span's own (which is only ever one of them). Null = the
        /// span's own range, unchanged.</param>
        internal static bool CoordAgainst(Book file, (double Lo, double Hi) own, Book container,
            IReadOnlyDictionary<Provider, string>? shelfRuns, out (double Lo, double Hi) coord,
            out (double Lo, double Hi)? against)
        {
            coord = own;
            against = null;
            if (container.Runs is not { Count: > 0 } runs) return true;       // nobody named a run: today
            if (file.Level != CollectionLevel.Issue) return true;             // judged ranges: ComparableRuns
            // Contradiction is judged PER LEG, not per row: a container may now name two runs on one leg (an
            // omnibus of two minis), and a file that is in the second of them has not contradicted anything by
            // failing to be in the first. A leg contradicts when the file has a key there and NOT ONE of the
            // container's runs on that leg is it.
            var matchedOn = new HashSet<Provider>();
            var claimedOn = new HashSet<Provider>();
            foreach (var (provider, key, start, end) in runs)
                if (file.FileRuns is { } fr && fr.TryGetValue(provider, out var mine))
                {
                    claimedOn.Add(provider);
                    if (!string.Equals(mine, key, StringComparison.OrdinalIgnoreCase)) continue;
                    matchedOn.Add(provider);
                    if (start is { } lo && end is { } hi) against ??= (lo, hi);
                }
            var byOwnLink = matchedOn.Count > 0;
            var contradicted = claimedOn.Count > matchedOn.Count;
            // A file's OWN link is more specific than the shelf it happens to sit on, and it wins. S100573
            // holds Flash (1959) and Flash v2 (2007-2009) after a merge on a wrong stored link: the shelf's
            // identity is CV 1995 (1959), so the shelf test alone let the 1975 "The Flash 232" (100pp, gold
            // #232-232) swallow the 2007 "The Flash 232" — a 22pp file whose own GCD link names series 26125,
            // Wally West's run. A file that says it is in a different run is not in this one, whatever the
            // shelf says.
            if (contradicted) { against = null; return false; }
            if (!byOwnLink)
            {
                if (shelfRuns is not { } sr) return false;
                var byShelf = false;
                foreach (var (provider, key, start, end) in runs)
                    if (sr.TryGetValue(provider, out var shelf)
                        && string.Equals(shelf, key, StringComparison.OrdinalIgnoreCase))
                    {
                        byShelf = true;
                        if (start is { } lo && end is { } hi) against = (lo, hi);
                        break;
                    }
                if (!byShelf) { against = null; return false; }
                // …and the shelf may only speak for a file that has not contradicted it on some OTHER leg.
                // On S100573 the span names the ComicVine volume, which the 2007 file's links say nothing
                // about; what it does say is GCD series 26125, where the shelf says the 1959 run. A file
                // that disagrees with the shelf is not in the shelf's run, so the shelf cannot lend it one.
                foreach (var (provider, shelf) in sr)
                    if (file.FileRuns is { } fr2 && fr2.TryGetValue(provider, out var mine2)
                        && !string.Equals(mine2, shelf, StringComparison.OrdinalIgnoreCase)) { against = null; return false; }
                return true;
            }
            if (file.ProviderIssueNumber is double pn) coord = (pn, pn);
            return true;
        }

        /// <summary>
        /// Whether two books' ranges are in the same coordinate system at all.
        ///
        /// <para>A <c>CollectedEditionSpan</c> says "#a-b"; it does not say #a-b OF WHAT, and on a shelf whose
        /// books each collect a different mini (Hellboy, B.P.R.D., Ether, Pathfinder) the answer differs per
        /// book. <c>CollectedEditionSpanRun</c> is where a reader records it, per leg. When both sides are
        /// JUDGED ranges that name the same leg with DIFFERENT keys, they are not comparable: neither nests in
        /// the other, and equal ranges are not "two editions of the same material" — they are two different
        /// comics that both number from #1.</para>
        ///
        /// <para>Unknown on either side — no refs, or no leg in common — is today's behaviour, unchanged. This
        /// is a veto a reader has to arm; it never fires on its own.</para>
        /// </summary>
        internal static bool ComparableRuns(Book a, Book b) => RunOverlay(a, b, out _, out _);

        /// <summary>
        /// <see cref="ComparableRuns"/>, and — when the two books name the SAME run and both carry a range for
        /// it — the two ranges IN THAT RUN'S NUMBERING, which are then the ones to compare.
        ///
        /// <para>Since TOOLS_TODO 17 a book may name several runs, so "comparable" is per (leg, key): sharing
        /// ANY (leg, key) makes them comparable, and it is that shared run's coordinates both ranges are read
        /// in. A leg in common on which the keys never match is the veto — two different comics that both
        /// number from #1. Either side carrying no range for the shared run falls back to the span's own,
        /// which is what a NULL start/end means.</para>
        /// </summary>
        internal static bool RunOverlay(Book a, Book b,
            out (double Lo, double Hi)? forA, out (double Lo, double Hi)? forB)
        {
            forA = forB = null;
            if (a.RangeSource != EditionSource.Curated || b.RangeSource != EditionSource.Curated) return true;
            if (a.Runs is not { Count: > 0 } x || b.Runs is not { Count: > 0 } y) return true;
            var legInCommon = false;
            foreach (var p in x)
                foreach (var q in y)
                {
                    if (p.Provider != q.Provider) continue;
                    legInCommon = true;
                    if (!string.Equals(p.Key, q.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.Start is { } ps && p.End is { } pe) forA = (ps, pe);
                    if (q.Start is { } qs && q.End is { } qe) forB = (qs, qe);
                    return true;
                }
            return !legInCommon;
        }

        /// <summary>
        /// Whether two books with an equal range describe the SAME material. Two editions of one mini do; an
        /// omnibus that also names a second run does not, and must be allowed to hold the trade whose whole
        /// mini it collects. Unknown on either side is today's answer — equal ranges are one comic twice.
        /// </summary>
        private static bool SameMaterial(Book a, Book b)
        {
            if (a.Runs is not { Count: > 0 } x || b.Runs is not { Count: > 0 } y) return true;
            static HashSet<(Provider, string)> Keys(IReadOnlyList<ReadingOrderJob.SpanRun> r) =>
                r.Select(v => (v.Provider, v.Key.ToLowerInvariant())).ToHashSet();
            return Keys(x).SetEquals(Keys(y));
        }

        /// <summary>The width of a container's judged range, or +∞ when it has none — no range is not narrow.</summary>
        private static double JudgedWidth(Book b) =>
            b.SpanFromStart.HasValue && b.SpanFromEnd.HasValue
                ? b.SpanFromEnd.Value - b.SpanFromStart.Value
                : double.PositiveInfinity;

        /// <summary>An annual or a special / one-shot: numbered in its own series, never a coordinate on the run's ladder.</summary>
        internal static bool OffTheLadder(Book b) =>
            b.ReadTier is ReadingOrderParser.TierAnnual or ReadingOrderParser.TierSpecial;

        /// <summary>
        /// A POINT issue (#23.1, #7.5) sits inside a whole-number range arithmetically and inside the book only
        /// when the book says so. DC's Villains Month #23.1-23.4 were never in "Superman Vol. 04 - Psi War"
        /// (#18-24), yet all three of ours nested there; The Flash's #23.2 IS in Vol. 04 (#20-25) and #23.1 /
        /// #23.3 are not. So a fractional coordinate on an ISSUE file is measured against a container only when
        /// the container's own quoted page names that exact number. A whole-number coordinate is unaffected.
        /// </summary>
        internal static bool PointIssueNamed(Book file, (double Lo, double Hi) coord, HashSet<double>? quoted)
        {
            if (file.Level != CollectionLevel.Issue) return true;
            if (coord.Lo % 1 == 0 && coord.Hi % 1 == 0) return true;
            return quoted != null && quoted.Contains(coord.Lo);
        }

        /// <summary>Every whole number this base book occupies is one the container's own note denies.</summary>
        private static bool WhollyExcluded((double Lo, double Hi) range, HashSet<double> excluded)
        {
            for (var v = Math.Ceiling(range.Lo); v <= Math.Floor(range.Hi); v++)
                if (!excluded.Contains(v)) return false;
            return Math.Ceiling(range.Lo) <= Math.Floor(range.Hi);
        }

        private static SpanSource SpanSourceFor(EditionSource source) => source switch
        {
            EditionSource.Locg => SpanSource.Locg,
            EditionSource.Gcd => SpanSource.Gcd,
            EditionSource.Cv => SpanSource.ComicVine,
            EditionSource.Curated => SpanSource.Curated,
            _ => SpanSource.Inferred,
        };

        private static string Fmt(double d) =>
            d % 1 == 0 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.#", CultureInfo.InvariantCulture);

        private static string? Blank(string s) => s.Length == 0 ? null : s;

        internal static void Stamp(TargetWriter hot)
        {
            var entry = DerivedTables.All.First(e => e.Name == DerivedName);
            hot.Upsert("DerivedTable", new
            {
                Name = entry.Name,
                RebuildJob = entry.RebuildJob,
                InputFingerprint = ResolvePipeline.Fingerprint(hot, entry.FingerprintSql),
                LastRebuiltAt = DateTime.UtcNow,
                RowCount = (int)hot.Scalar<long>("SELECT count(*) FROM CollectionNode"),
            });
        }
    }

    /// <summary>
    /// <c>books-collected-editions</c> — rebuild `CollectedEditionSpan(Source=Locg)` from the warehouse's own
    /// containment edges. This is the reduction the standalone ran OFFLINE in a script: LOCG publishes one edge
    /// per contained issue, and a span is the min and max issue number over the edges of one container.
    ///
    /// <para>The other three sources are IMPORTED, never derived: ComicVine's and GCD's spans arrive from their
    /// own scrapes, and the curated rows are hand knowledge. Only LOCG's is a reduction, and only LOCG's is
    /// rebuilt here — which is why the verb touches nothing else.</para>
    ///
    /// <para>Chunked by container item id. The legs file is opened READ-ONLY; a containment edge naming a comic
    /// the hot file does not hold is skipped and counted, never thrown on (no FK crosses the file boundary).</para>
    /// </summary>
    public static class CollectedEditionJob
    {
        public sealed record BatchResult(int Processed, long Remaining, long? NextCursor, int Spans, int Skipped)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", skipped: {Skipped} }}  [collected-editions, spans: {Spans}]";
        }

        /// <summary>
        /// One page of containers, by `Item.Id`. A container is an item with a matched LOCG link whose LOCG
        /// comic id appears on the CONTAINER side of at least one containment edge.
        /// </summary>
        public static BatchResult RunBatch(TargetWriter hot, SqliteConnection legs, long after, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);

            var links = new List<(int ItemId, int SeriesId, long LocgId)>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id, coalesce(i.SeriesId,'') || char(31) || l.ProviderKey
FROM Item i JOIN ItemProviderLink l ON l.ItemId = i.Id
WHERE l.Provider = {(int)Provider.Locg} AND l.Status IN {LinkStatuses.UsableSql} AND l.ProviderKey IS NOT NULL
  AND i.SeriesId IS NOT NULL AND i.Id > {after}
ORDER BY i.Id LIMIT {batchSize}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p[0].Length == 0 || !long.TryParse(p[1], out var locgId)) continue;
                links.Add(((int)itemId, int.Parse(p[0]), locgId));
            }
            if (links.Count == 0) return new BatchResult(0, 0, null, 0, 0);

            var edges = ReadEdges(legs, links.Select(l => l.LocgId).Distinct().ToList());
            var numbers = ReadIssueNumbers(legs, edges.Values.SelectMany(v => v).Distinct().ToList());

            int spans = 0, skipped = 0;
            var upto = links[^1].ItemId;
            hot.Exec($"DELETE FROM CollectedEditionSpan WHERE Source = {(int)EditionSource.Locg} AND ItemId > {after} AND ItemId <= {upto}");

            foreach (var (itemId, seriesId, locgId) in links)
            {
                if (!edges.TryGetValue(locgId, out var contained) || contained.Count == 0) { skipped++; continue; }
                var values = contained.Select(id => numbers.GetValueOrDefault(id)).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (values.Count == 0) { skipped++; continue; }

                var start = values.Min();
                var end = values.Max();
                // Contiguous when every integer between the ends is actually collected — the difference between
                // "collects #1-12" and "collects #1 and #12".
                var contiguous = values.Distinct().Count() == (int)(end - start) + 1;
                hot.Upsert("CollectedEditionSpan", new
                {
                    ItemId = itemId,
                    Source = EditionSource.Locg,
                    SeriesId = seriesId,
                    IssueStart = start,
                    IssueEnd = end,
                    ProviderRef = locgId.ToString(CultureInfo.InvariantCulture),
                    Contiguous = contiguous,
                    Note = $"{contained.Count} contained",
                    CreatedAt = DateTime.UtcNow,
                });
                spans++;
            }

            return new BatchResult(links.Count, hot.Scalar<long>($"SELECT count(*) FROM Item WHERE Id > {upto}"), upto, spans, skipped);
        }

        public const string DerivedName = "CollectedEditionSpan(Source=Locg)";
        /// <summary>The persisted cursor (an <c>Item.Id</c>); see <see cref="JobCursor"/>.</summary>
        public const string CursorKey = "books:recompute:collected-editions";

        /// <summary>Drain every LOCG-linked container; the cursor persists per batch and <paramref name="resume"/> continues from it.</summary>
        public static (int Spans, int Skipped) RunAll(TargetWriter hot, string legsPath, int batchSize, Action<string> log, bool resume = false)
        {
            using var legs = LegsTagFoldJob.OpenLegs(legsPath);
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            int spans = 0, skipped = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, legs, cursor, batchSize);
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                spans += r.Spans;
                skipped += r.Skipped;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            Stamp(hot);
            hot.Commit();
            return (spans, skipped);
        }

        internal static void Stamp(TargetWriter hot)
        {
            var entry = DerivedTables.All.First(e => e.Name == DerivedName);
            hot.Upsert("DerivedTable", new
            {
                Name = entry.Name,
                RebuildJob = entry.RebuildJob,
                InputFingerprint = ResolvePipeline.Fingerprint(hot, entry.FingerprintSql),
                LastRebuiltAt = DateTime.UtcNow,
                RowCount = (int)hot.Scalar<long>($"SELECT count(*) FROM CollectedEditionSpan WHERE Source = {(int)EditionSource.Locg}"),
            });
        }

        private static Dictionary<long, List<long>> ReadEdges(SqliteConnection legs, List<long> containerIds)
        {
            var edges = new Dictionary<long, List<long>>();
            foreach (var chunk in Chunk(containerIds, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT ContainerLocgComicId, ContainedLocgComicId FROM LocgContainment WHERE ContainerLocgComicId IN (" + Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var container = rd.GetInt64(0);
                    if (!edges.TryGetValue(container, out var list)) edges[container] = list = new List<long>();
                    list.Add(rd.GetInt64(1));
                }
            }
            return edges;
        }

        private static Dictionary<long, double?> ReadIssueNumbers(SqliteConnection legs, List<long> comicIds)
        {
            var numbers = new Dictionary<long, double?>();
            foreach (var chunk in Chunk(comicIds, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT LocgComicId, IssueNumber FROM LocgComicRaw WHERE LocgComicId IN (" + Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var raw = rd.IsDBNull(1) ? null : rd.GetString(1);
                    numbers[rd.GetInt64(0)] = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                }
            }
            return numbers;
        }

        private static string Placeholders(SqliteCommand cmd, IReadOnlyList<long> values)
        {
            var names = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                names[i] = "$p" + i;
                cmd.Parameters.AddWithValue(names[i], values[i]);
            }
            return string.Join(",", names);
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }
}
