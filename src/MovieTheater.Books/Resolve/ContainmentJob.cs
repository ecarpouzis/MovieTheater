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
    /// labelled "Saga Vol 7" as collecting #1–6. A span comes ONLY from a `CollectedEditionSpan` row, whose
    /// precedence is Locg &gt; Gcd &gt; Cv &gt; Curated; without one the edition stays a labelled leaf — you own
    /// the book, we do not claim to know its contents.</para>
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
            public double? SpanFromStart, SpanFromEnd;
            public EditionSource RangeSource = EditionSource.Cv;
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
                BuildSeries(books);
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

        private static List<Book> LoadBooks(TargetWriter hot, int seriesId)
        {
            var spans = ReadingOrderJob.LoadSpans(hot, seriesId);
            var books = new List<Book>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id,
       coalesce(cd.Format, 13) || char(31) || coalesce(cd.FormatRaw,'') || char(31) || coalesce(i.FileName,'') || char(31)
    || coalesce(i.PageCount, 0) || char(31) || coalesce(cd.VolumeNo,'') || char(31)
    || coalesce(ro.ReadIndex,'') || char(31) || coalesce(ro.ReadDate,'') || char(31) || coalesce(ro.ReadNumber,'')
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
                };
                if (spans.TryGetValue(book.ItemId, out var sp))
                {
                    book.SpanFromStart = sp.Start;
                    book.SpanFromEnd = sp.End;
                    book.RangeSource = sp.Source;
                }
                books.Add(book);
            }
            return books;
        }

        /// <summary>The pure decision — one series' books in, their nodes' fields set in place.</summary>
        public static void BuildSeries(List<Book> books)
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
                num[i] = b.ReadNumber ?? b.VolumeNo ?? i + 1;
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
            var baseRange = new (double Lo, double Hi)[n];
            for (var i = 0; i < n; i++)
            {
                var bb = baseBooks[i];
                if (bb.SpanFromStart.HasValue && bb.SpanFromEnd.HasValue) baseRange[i] = (bb.SpanFromStart.Value, bb.SpanFromEnd.Value);
                else if (bb.Level == CollectionLevel.Issue) baseRange[i] = (num[i] ?? i + 1, num[i] ?? i + 1);
                else baseRange[i] = (double.NaN, double.NaN);
            }

            if (n > 0)
                foreach (var b in books.Where(b => b.Level > baseLevel && b.SpanFromStart.HasValue && b.SpanFromEnd.HasValue))
                {
                    double es = b.SpanFromStart!.Value, ee = b.SpanFromEnd!.Value;
                    int lo = int.MaxValue, hi = int.MinValue;
                    for (var i = 0; i < n; i++)
                        if (!double.IsNaN(baseRange[i].Lo) && baseRange[i].Lo <= ee && baseRange[i].Hi >= es)
                        { if (i < lo) lo = i; if (i > hi) hi = i; }

                    // The authoritative range is ALWAYS surfaced as the label, even when nothing it collects is
                    // owned — it still "collects #1-20"; there is just nothing to drill into.
                    b.SpanSource = SpanSourceFor(b.RangeSource);
                    b.SpanLabel = es == ee ? $"#{Fmt(es)}" : $"#{Fmt(es)}-{Fmt(ee)}";

                    var rangeSize = ee - es + 1;
                    var span = lo == int.MaxValue ? 0 : hi - lo + 1;
                    // Guard on the SPAN, not the match count: a clean collection's issues are CONTIGUOUS in the
                    // base sequence so span ≈ rangeSize; a conflated-run collision matches a handful scattered
                    // far apart, giving a small count yet an enormous span.
                    if (lo == int.MaxValue || span > rangeSize * 1.3 + 3) { b.SpanStart = b.SpanEnd = 0; b.ContainsCount = 0; }
                    else { b.SpanStart = lo + 1; b.SpanEnd = hi + 1; b.ContainsCount = span; }
                }

            var containers = books.Where(b => b.Level > baseLevel).ToList();
            if (containers.Count == 0) return;
            foreach (var b in books)
            {
                // A container that collects nothing we own has no position to be contained BY, so it stays a
                // top-level leaf — otherwise every "empty" container nests under any other empty one.
                if (b.SpanEnd <= 0) { b.ParentItemId = null; continue; }
                Book? parent = null;
                foreach (var p in containers)
                    if (p.Level > b.Level && p.ItemId != b.ItemId
                        && p.SpanStart <= b.SpanStart && p.SpanEnd >= b.SpanEnd
                        && (parent == null || p.SpanEnd - p.SpanStart < parent.SpanEnd - parent.SpanStart))
                        parent = p;
                b.ParentItemId = parent?.ItemId;
            }
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
WHERE l.Provider = {(int)Provider.Locg} AND l.Status = {(int)LinkStatus.Matched} AND l.ProviderKey IS NOT NULL
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
