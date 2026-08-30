using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-resolve --book-series</c> — give BOOKS real <c>Series</c> rows and a real <c>Item.SeriesId</c>,
    /// so a book series is navigable everywhere a comic series is (the <c>?series=</c> modal,
    /// <c>browse/series/{id}/run</c>, the Explore and shelf rails, the series facet) — without altering ANY
    /// comic outcome.
    ///
    /// <para><b>Why a separate job.</b> Comic identity is derived from <c>ComicDetail.ParsedSeriesKey</c> through
    /// <see cref="SeriesResolver"/>, <c>SeriesKeyLink</c> and <c>SeriesAlias</c>. A book has none of those: its
    /// series is a plain string Calibre wrote into <c>BookDetail.SeriesName</c>, and there is no provider link and
    /// no parsed spelling to alias. Putting book rows through the comic pipeline would park their canonical keys,
    /// fold every one of them into the single empty-parsed-key bucket, rename them, and then delete them for
    /// having no alias row. So books get their own job and their own key space — <c>book:&lt;normalized name&gt;</c>
    /// — and every statement in the comic rebuild is narrowed off those rows
    /// (<see cref="SeriesResolver.NotBookSql"/>).</para>
    ///
    /// <para><b>The identity.</b> One <c>Series</c> row per <see cref="SeriesResolver.NormalizeKey"/> of the
    /// series name, <c>CanonicalKey = "book:" + key</c>, <c>ParsedKey = NULL</c> (a book has no parsed spelling —
    /// leaving it null is also what keeps the alias-driven comic statements from ever seeing these rows), and
    /// <c>Name</c> = the most frequent EXACT spelling among the books that carry it. A name of two characters or
    /// fewer is not a series: <c>SS</c>, <c>UC</c>, <c>V</c>, <c>HR</c> and friends are format tokens Calibre's
    /// own field picked up, and 1,294 books hang off <c>SS</c> alone. They are being cleared at the source, and
    /// this floor is the guard that stands whether that lands or not.</para>
    ///
    /// <para><b>Chunked, resumable, observable</b> like every bulk job here, on the same three-phase cursor shape
    /// the comic rebuild uses: <c>0</c> = the identity pass (bounded by the distinct name count, one transaction),
    /// <c>&gt;= RepointBase</c> = re-point a page of book items at a time (the 126k-row part; the cursor IS the
    /// page's ordering, so a resume is exact), <c>1</c> = the finish pass (drop the emptied rows, recompute the
    /// counts and the spans, stamp the registry). <b>Idempotent</b>: a second run writes the same rows and changes
    /// nothing.</para>
    ///
    /// <para><b>Order matters.</b> It runs AFTER the comic identity rebuild, because that job's finish pass
    /// deletes series rows and re-points items, and this one's counts must be computed over the settled ids.</para>
    /// </summary>
    public static class BookSeriesLinkJob
    {
        /// <summary>Cursor space, deliberately the same shape as <see cref="SeriesRebuildJob"/>'s.</summary>
        public const long IdentityCursor = 0;
        public const long FinishCursor = 1;
        public const long RepointBase = 1_000_000;

        /// <summary>A series name this short is a format token, never a series. See the class remarks.</summary>
        public const int MinNameLength = 3;

        /// <summary>One bounded phase. Returns true when the whole job is done.</summary>
        public static bool RunStep(TargetWriter hot, long cursor, int batchSize, Action<string> log, UnitCounts counts, out long nextCursor) =>
            RunStep(hot, cursor, batchSize, log, counts, out nextCursor, out _);

        /// <summary>One bounded phase, reporting how many rows it actually handled (what the caller prints).</summary>
        public static bool RunStep(TargetWriter hot, long cursor, int batchSize, Action<string> log, UnitCounts counts, out long nextCursor, out int processed)
        {
            batchSize = Math.Clamp(batchSize, 100, 50_000);
            switch (cursor)
            {
                case IdentityCursor:
                {
                    var (added, renamed, total) = Identity(hot);
                    counts.Bump("book-series-added", added);
                    counts.Bump("book-series-renamed", renamed);
                    log($"book series: {total} distinct names ({added} new rows, {renamed} renamed)");
                    processed = total;
                    nextCursor = RepointBase;
                    return false;
                }
                case FinishCursor:
                {
                    Finish(hot, log, counts);
                    processed = 0;
                    nextCursor = FinishCursor;
                    return true;
                }
                default:
                {
                    var after = cursor - RepointBase;
                    var last = Repoint(hot, after, batchSize, out var seen, out var linked, out var unlinked);
                    counts.Bump("books-linked", linked);
                    counts.Bump("books-unlinked", unlinked);
                    processed = seen;
                    if (seen == 0) { nextCursor = FinishCursor; return false; }
                    nextCursor = RepointBase + last;
                    return false;
                }
            }
        }

        /// <summary>
        /// Drain every phase, reporting <c>{ processed, remaining, nextCursor, counts }</c> per chunk the way the
        /// other bulk verbs do. The caller-driven loop lives here; the no-progress break is the safety net.
        /// </summary>
        public static UnitCounts RunAll(TargetWriter hot, int batchSize, Action<string> log)
        {
            var counts = new UnitCounts();
            var cursor = IdentityCursor;
            var guard = 0;
            while (true)
            {
                hot.Begin();
                var done = RunStep(hot, cursor, batchSize, log, counts, out var next, out var processed);
                hot.Commit();

                var remaining = next >= RepointBase
                    ? hot.Scalar<long>("SELECT count(*) FROM Item WHERE Kind = 1 AND Id > $after", ("$after", next - RepointBase))
                    : 0;
                log($"{{ processed: {processed}, remaining: {remaining}, nextCursor: \"{next}\", counts: {counts} }}");

                if (done) break;
                if (next == cursor && ++guard > 2) break;
                if (next != cursor) guard = 0;
                cursor = next;
            }
            return counts;
        }

        // ── phase 1: identity ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create or refresh one <c>book:</c> series row per normalized name. Bounded by the DISTINCT name count
        /// (~21k on the real file), read as one grouped query rather than a row per book.
        ///
        /// <para>Nothing is deleted here. A name that has gone away leaves its row behind for the re-point to
        /// empty and the finish pass to drop — deleting a row items still point at would fail the foreign key,
        /// and doing it in that order is what makes a killed run resumable.</para>
        /// </summary>
        private static (int Added, int Renamed, int Total) Identity(TargetWriter hot)
        {
            var names = NameCounts(hot);
            var existing = ExistingRows(hot);
            var nextId = hot.Scalar<long>("SELECT coalesce(max(Id), 0) FROM Series");

            int added = 0, renamed = 0;
            foreach (var (key, name) in names)
            {
                var canonical = SeriesResolver.BookKeyPrefix + key;
                if (existing.TryGetValue(canonical, out var row))
                {
                    if (string.Equals(row.Name, name, StringComparison.Ordinal)) continue;
                    hot.Update("Series", "Id", row.Id, new { Name = name });
                    renamed++;
                }
                else
                {
                    hot.Upsert("Series", new
                    {
                        Id = (int)++nextId,
                        ParsedKey = (string?)null,
                        CanonicalKey = canonical,
                        Name = name,
                        IssueCount = 0,
                        IsOngoing = false,
                    });
                    added++;
                }
            }
            return (added, renamed, names.Count);
        }

        /// <summary>
        /// Every book series name in the library, grouped to one winner per normalized key: the most frequent
        /// EXACT spelling, ties broken by the spelling itself so the answer is deterministic and a re-run does
        /// not flip between two equally popular casings.
        /// </summary>
        private static Dictionary<string, string> NameCounts(TargetWriter hot)
        {
            var best = new Dictionary<string, (string Name, long Count)>(StringComparer.Ordinal);
            foreach (var (count, name) in hot.Pairs(@"
SELECT count(*), bd.SeriesName FROM BookDetail bd
JOIN Item i ON i.Id = bd.ItemId
WHERE i.Kind = 1 AND bd.SeriesName IS NOT NULL AND trim(bd.SeriesName) <> ''
GROUP BY bd.SeriesName"))
            {
                var spelling = (name ?? "").Trim();
                if (spelling.Length < MinNameLength) continue;
                var key = SeriesResolver.NormalizeKey(spelling);
                if (key.Length == 0) continue;
                if (!best.TryGetValue(key, out var cur)
                    || count > cur.Count
                    || (count == cur.Count && string.CompareOrdinal(spelling, cur.Name) < 0))
                    best[key] = (spelling, count);
            }
            return best.ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.Ordinal);
        }

        /// <summary>The <c>book:</c> series rows that already exist, by canonical key.</summary>
        private static Dictionary<string, (int Id, string? Name)> ExistingRows(TargetWriter hot)
        {
            var map = new Dictionary<string, (int, string?)>(StringComparer.Ordinal);
            foreach (var (id, payload) in hot.Pairs(
                "SELECT Id, CanonicalKey || char(31) || coalesce(Name,'') FROM Series WHERE CanonicalKey LIKE 'book:%'"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                map[p[0]] = ((int)id, p[1].Length == 0 ? null : p[1]);
            }
            return map;
        }

        // ── phase 2: re-point ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Point one page of BOOK items at their series row, or at NULL when they have no usable series name.
        /// The page's ordering is <c>Item.Id</c> ascending, which is exactly the cursor.
        ///
        /// <para>Kind = 1 is the whole guard on the comic side: a comic is never read here and never written, and
        /// the answer is derived from the book's own <c>BookDetail.SeriesName</c> — never from the SeriesId
        /// already stored — so the pass is idempotent and a book whose series was cleared is UNLINKED rather than
        /// left pointing at a row that no longer describes it.</para>
        /// </summary>
        private static long Repoint(TargetWriter hot, long after, int batchSize, out int seen, out int linked, out int unlinked)
        {
            linked = 0; unlinked = 0;
            var upto = hot.Scalar<long>(
                "SELECT coalesce(max(Id), 0) FROM (SELECT Id FROM Item WHERE Kind = 1 AND Id > $after ORDER BY Id LIMIT $n)",
                ("$after", after), ("$n", batchSize));
            var rows = hot.Pairs($@"
SELECT i.Id, coalesce(CAST(i.SeriesId AS TEXT),'') || char(31) || coalesce(bd.SeriesName,'')
FROM Item i LEFT JOIN BookDetail bd ON bd.ItemId = i.Id
WHERE i.Kind = 1 AND i.Id > {after} AND i.Id <= {upto} ORDER BY i.Id");
            seen = rows.Count;
            if (seen == 0) return after;

            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, key) in hot.Pairs("SELECT Id, CanonicalKey FROM Series WHERE CanonicalKey LIKE 'book:%'"))
                byKey[key!] = (int)id;

            foreach (var (itemId, payload) in rows)
            {
                var p = payload!.Split(TargetWriter.Sep);
                var current = p[0].Length == 0 ? (int?)null : int.Parse(p[0], CultureInfo.InvariantCulture);
                var desired = DesiredSeriesId(byKey, p[1]);
                if (current == desired) continue;
                hot.Exec("UPDATE Item SET SeriesId = $sid WHERE Id = $id", ("$sid", desired), ("$id", itemId));
                if (desired == null) unlinked++; else linked++;
            }
            return upto;
        }

        private static int? DesiredSeriesId(IReadOnlyDictionary<string, int> byKey, string seriesName)
        {
            var spelling = seriesName.Trim();
            if (spelling.Length < MinNameLength) return null;
            var key = SeriesResolver.NormalizeKey(spelling);
            if (key.Length == 0) return null;
            return byKey.TryGetValue(SeriesResolver.BookKeyPrefix + key, out var id) ? id : null;
        }

        // ── phase 3: finish ──────────────────────────────────────────────────────────────────────────────

        private static void Finish(TargetWriter hot, Action<string> log, UnitCounts counts)
        {
            // A book series nothing points at any more is gone. Its series-level tag rows go with it (the FK
            // would refuse otherwise), and nothing else in the model can reference one.
            hot.Exec(@"
DELETE FROM SeriesTag WHERE SeriesId IN (
    SELECT Id FROM Series WHERE CanonicalKey LIKE 'book:%'
      AND Id NOT IN (SELECT SeriesId FROM Item WHERE SeriesId IS NOT NULL))");
            var deleted = hot.Exec(@"
DELETE FROM Series WHERE CanonicalKey LIKE 'book:%'
  AND Id NOT IN (SELECT SeriesId FROM Item WHERE SeriesId IS NOT NULL)");
            counts.Bump("book-series-deleted", deleted);

            RecomputeCounts(hot);
            RecomputeYearSpans(hot);

            // The same three registry rows the comic rebuild owns: this job writes two of them (Series and
            // Item.SeriesId) too, so the stamp has to be re-taken AFTER it or the row counts would describe a
            // file that no longer exists.
            SeriesRebuildJob.Stamp(hot);
            log($"book series: {deleted} emptied rows deleted, counts and spans recomputed, registry stamped");
        }

        /// <summary>Book series hold BOOKS: Kind = 1, shadow duplicates excluded, one grouped pass.</summary>
        private static void RecomputeCounts(TargetWriter hot)
        {
            hot.Exec("UPDATE Series SET IssueCount = 0 WHERE CanonicalKey LIKE 'book:%'");
            hot.Exec(@"
UPDATE Series SET IssueCount = t.cnt
FROM (SELECT i.SeriesId AS sid, count(*) AS cnt FROM Item i
      WHERE i.SeriesId IS NOT NULL AND i.Kind = 1 AND coalesce(i.IsExcluded, 0) = 0
      GROUP BY i.SeriesId) AS t
WHERE Series.Id = t.sid AND Series.CanonicalKey LIKE 'book:%'");
        }

        /// <summary>
        /// The run span from the books' own publication dates. <c>BookDetail.PublishedOn</c> is Calibre's
        /// <c>pubdate</c> — an ISO-ish string whose first four characters are the year — and a year outside
        /// 1900–2100 is Calibre's "unknown" placeholder (0101-01-01), never a date. <c>IsOngoing</c> stays false:
        /// it is a comic-run heuristic and a book series has no publication schedule to be current with.
        /// </summary>
        private static void RecomputeYearSpans(TargetWriter hot)
        {
            hot.Exec("UPDATE Series SET YearStart = NULL, YearEnd = NULL, IsOngoing = 0 WHERE CanonicalKey LIKE 'book:%'");
            hot.Exec(@"
UPDATE Series SET YearStart = t.minY, YearEnd = t.maxY
FROM (SELECT sid, min(y) AS minY, max(y) AS maxY FROM (
        SELECT i.SeriesId AS sid,
               CASE WHEN CAST(substr(bd.PublishedOn, 1, 4) AS INTEGER) BETWEEN 1900 AND 2100
                    THEN CAST(substr(bd.PublishedOn, 1, 4) AS INTEGER) END AS y
        FROM Item i JOIN BookDetail bd ON bd.ItemId = i.Id
        WHERE i.SeriesId IS NOT NULL AND i.Kind = 1 AND coalesce(i.IsExcluded, 0) = 0)
      WHERE y IS NOT NULL GROUP BY sid) AS t
WHERE Series.Id = t.sid AND Series.CanonicalKey LIKE 'book:%'");
        }
    }
}
