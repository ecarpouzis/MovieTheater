using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Parse;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The shared half of the three collected-edition producers: which items are collection-shaped, what the
    /// series is called, and how a span row is written.
    ///
    /// <para><b>The rule all three obey.</b> A collected edition may be linked only to a provider record that is
    /// itself a CONTAINER — an edition entry in ComicVine's own "Collected Editions" list, a GCD issue that
    /// appears as a reprint TARGET, a LOCG comic with containment edges. The number-matching path is gone: the
    /// fault this repairs is that "Saga Book 1" (505 pages) was matched to "Saga #1" (28 pages, zero containment
    /// edges) on the volume ordinal parsed off its filename. When a provider does not have the edition, the
    /// producer emits nothing and the book stays a labelled leaf.</para>
    /// </summary>
    public static class ProducerSupport
    {
        /// <summary>Every collection-shaped comic of the given series, with what the matchers need.</summary>
        public static Dictionary<int, List<CollectionBook>> LoadCollectionBooks(TargetWriter hot, string seriesIdList)
        {
            var bySeries = new Dictionary<int, List<CollectionBook>>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id,
       i.SeriesId || char(31) || coalesce(i.FileName,'') || char(31) || coalesce(i.PageCount,0) || char(31)
    || coalesce(cd.Format, 13) || char(31) || coalesce(cd.FormatRaw,'') || char(31) || coalesce(cd.VolumeNo,'') || char(31)
    || coalesce(cd.IssueNo,'') || char(31) || coalesce(cd.IsCollection,0) || char(31) || coalesce(cd.Year,'')
FROM Item i JOIN ComicDetail cd ON cd.ItemId = i.Id
WHERE i.SeriesId IN ({seriesIdList}) AND i.Kind = {(int)ItemKind.Comic} AND coalesce(i.IsExcluded,0) = 0
ORDER BY i.Id"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                var seriesId = int.Parse(p[0], CultureInfo.InvariantCulture);
                var fileName = p[1];
                var pageCount = int.Parse(p[2], CultureInfo.InvariantCulture);
                var format = (ComicFormat)int.Parse(p[3], CultureInfo.InvariantCulture);
                var formatRaw = p[4].Length == 0 ? null : p[4];
                var level = CollectionLevels.Resolve(format, formatRaw, fileName, pageCount);
                var isCollection = p[7] == "1";
                if (!EditionMatcher.IsCollectionShaped(level, isCollection, pageCount)) continue;
                if (level == CollectionLevel.Issue) level = CollectionLevel.Volume;

                if (!bySeries.TryGetValue(seriesId, out var list)) bySeries[seriesId] = list = [];
                list.Add(new CollectionBook
                {
                    ItemId = (int)itemId,
                    SeriesId = seriesId,
                    FileName = fileName,
                    PageCount = pageCount,
                    VolumeNo = p[5].Length == 0 ? null : int.Parse(p[5], CultureInfo.InvariantCulture),
                    IssueNo = p[6].Length == 0 ? null : p[6],
                    Level = level,
                    Year = p[8].Length == 0 ? null : int.Parse(p[8], CultureInfo.InvariantCulture),
                });
            }
            return bySeries;
        }

        public static Dictionary<int, string> LoadSeriesNames(TargetWriter hot, string seriesIdList)
        {
            var names = new Dictionary<int, string>();
            foreach (var (id, name) in hot.Pairs(
                $"SELECT Id, coalesce(DisplayNameOverride, Name, '') FROM Series WHERE Id IN ({seriesIdList})"))
                names[(int)id] = name ?? "";
            return names;
        }

        /// <summary>The series ids of one page, the batch query's own ordering (the cursor).</summary>
        public static List<int> SeriesPage(TargetWriter hot, long afterSeriesId, int batchSize) =>
            hot.Pairs($"SELECT Id, '' FROM Series WHERE Id > {afterSeriesId} ORDER BY Id LIMIT {batchSize}")
               .Select(p => (int)p.Item1).ToList();

        public static void WriteSpan(TargetWriter hot, EditionSource source, CollectionBook book,
            double start, double end, string editionTitle, string? providerRef, double confidence, bool contiguous, string note)
        {
            hot.Upsert("CollectedEditionSpan", new
            {
                ItemId = book.ItemId,
                Source = source,
                SeriesId = book.SeriesId,
                IssueStart = start,
                IssueEnd = end,
                EditionTitle = editionTitle,
                ProviderRef = providerRef,
                Contiguous = contiguous,
                Confidence = confidence,
                Note = note,
                CreatedAt = DateTime.UtcNow,
            });
        }

        /// <summary>Delete one source's spans for every item of one series — the delete-then-rewrite window.</summary>
        public static void ClearSpans(TargetWriter hot, EditionSource source, int seriesId) =>
            hot.Exec($"DELETE FROM CollectedEditionSpan WHERE Source = {(int)source} "
                   + $"AND ItemId IN (SELECT Id FROM Item WHERE SeriesId = {seriesId})");

        /// <summary>
        /// One provider record is ONE physical edition, so at most one book in a series may claim it. When a
        /// whole shelf scores identically against the same record — "20th_Century_Boys_v01..v11", whose
        /// filenames carry no ordinal the matcher can read, all landing on "Perfect Edition Vol. 1" — the tie is
        /// not evidence about any of them, and every claimant is dropped. A strict winner keeps its match.
        /// </summary>
        public static List<T> ResolveContention<T, TKey>(List<T> matches, Func<T, TKey> keyOf, Func<T, double> confOf)
            where TKey : notnull
        {
            var kept = new List<T>();
            foreach (var group in matches.GroupBy(keyOf))
            {
                var ordered = group.OrderByDescending(confOf).ToList();
                if (ordered.Count == 1 || confOf(ordered[0]) > confOf(ordered[1])) kept.Add(ordered[0]);
            }
            return kept;
        }

        internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
                yield return source.Skip(i).Take(Math.Min(size, source.Count - i)).ToList();
        }

        internal static string Placeholders(SqliteCommand cmd, IReadOnlyList<long> values)
        {
            var names = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                names[i] = "$p" + i;
                cmd.Parameters.AddWithValue(names[i], values[i]);
            }
            return string.Join(",", names);
        }

        internal static string Fmt(double d) =>
            d % 1 == 0 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <c>books-cv-spans</c> — rebuild `CollectedEditionSpan(Source = Cv)` from the ComicVine volume
    /// description's own "Collected Editions" list.
    ///
    /// <para>ComicVine publishes, in each volume's prose, the publisher's list of that run's collected editions
    /// with the issues each one collects ("Volume 7 (#37-42)", "Saga Deluxe Edition Book One (#1-18)"). Those
    /// entries are CONTAINER records. The parse is the standalone's, restored; the match is on the edition
    /// TITLE against the book's filename, never on an issue number.</para>
    ///
    /// <para>The description itself lives in the legs table `CvVolumeDescription`, filled by
    /// <c>books-cv-descriptions-import</c> from the offline ComicVine rip (the hot `CvVolume.Description` holds
    /// only the 14k volumes v2 ever fetched and is used as a fallback).</para>
    ///
    /// <para><b>Delete-then-rewrite, but only where we can produce.</b> A series whose volume has no parseable
    /// block keeps whatever Cv spans it already had — clearing them would throw away migration-carried
    /// knowledge for nothing. A series WITH a block has its Cv spans rewritten wholesale.</para>
    ///
    /// <para>Chunked by `Series.Id` — the batch query's own ordering — dry-run by default.</para>
    /// </summary>
    public static class CvSpanJob
    {
        public const string DerivedName = "CollectedEditionSpan(Source=Cv)";
        public const string CursorKey = "books:recompute:cv-spans";

        /// <summary>The note that marks a span as produced HERE, by an edition-title match. `SpanSelection`
        /// promotes only Cv spans carrying it; a migration-carried Cv row without it ranks with GCD.</summary>
        public const string TitleMatchNote = "match-by: title";

        public sealed record BatchResult(int Processed, long Remaining, long? NextCursor, int Spans, int SeriesWithBlock, int Flagged)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", spans: {Spans}, seriesWithBlock: {SeriesWithBlock}, pageAuditFlags: {Flagged} }}  [cv-spans]";
        }

        public static BatchResult RunBatch(TargetWriter hot, SqliteConnection legs, long afterSeriesId, int batchSize,
            Action<string>? sample = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var seriesIds = ProducerSupport.SeriesPage(hot, afterSeriesId, batchSize);
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0, 0, 0);

            var idList = string.Join(",", seriesIds);
            var volumeOf = LoadVolumeIds(hot, idList);
            var books = ProducerSupport.LoadCollectionBooks(hot, idList);
            var names = ProducerSupport.LoadSeriesNames(hot, idList);
            var descriptions = LoadDescriptions(hot, legs, volumeOf.Values.Distinct().ToList());

            int spans = 0, withBlock = 0, flagged = 0;
            foreach (var seriesId in seriesIds)
            {
                if (!books.TryGetValue(seriesId, out var list) || list.Count == 0) continue;
                if (!volumeOf.TryGetValue(seriesId, out var volumeId)) continue;
                if (!descriptions.TryGetValue(volumeId, out var description)) continue;
                var editions = CvEditionParser.ParseEditions(description);
                if (editions.Count == 0) continue;

                withBlock++;
                ProducerSupport.ClearSpans(hot, EditionSource.Cv, seriesId);
                var seriesNorm = EditionMatcher.Norm(names.GetValueOrDefault(seriesId));

                var claims = new List<(CollectionBook Book, EditionCandidate Edition, double Confidence)>();
                foreach (var book in list)
                    if (EditionMatcher.MatchRanged(book, editions, seriesNorm) is { } matched)
                        claims.Add((book, matched.Edition, matched.Confidence));

                foreach (var (book, edition, confidence) in
                         ProducerSupport.ResolveContention(claims, c => (c.Edition.Title, c.Edition.Start, c.Edition.End), c => c.Confidence))
                {
                    var flag = PageArithmetic.Flag(book.PageCount, edition.Start, edition.End);
                    if (flag != null) flagged++;
                    var note = TitleMatchNote + "; cv collected-editions"
                             + (flag == null ? "" : $"; page-audit: {flag}");
                    ProducerSupport.WriteSpan(hot, EditionSource.Cv, book, edition.Start, edition.End,
                        edition.Title, volumeId.ToString(CultureInfo.InvariantCulture), confidence, true, note);
                    spans++;
                    sample?.Invoke($"  s{seriesId,-6} i{book.ItemId,-6} {Trim(book.FileName, 52)} -> {Trim(edition.Title, 34)}"
                        + $" #{ProducerSupport.Fmt(edition.Start)}-{ProducerSupport.Fmt(edition.End)} @{confidence:0.00}"
                        + (flag == null ? "" : $"  [{flag}: {PageArithmetic.PagesPerIssue(book.PageCount, edition.Start, edition.End):0.#} pp/issue]"));
                }
            }

            var next = seriesIds[^1];
            return new BatchResult(seriesIds.Count, hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}"),
                next, spans, withBlock, flagged);
        }

        public static (int Spans, int SeriesWithBlock, int Flagged) RunAll(TargetWriter hot, string legsPath, int batchSize,
            Action<string> log, bool resume = false, int sampleTop = 25)
        {
            using var legs = LegsTagFoldJob.OpenLegs(legsPath);
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            int spans = 0, withBlock = 0, flagged = 0, printed = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, legs, cursor, batchSize, s => { if (printed++ < sampleTop) log(s); });
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                spans += r.Spans; withBlock += r.SeriesWithBlock; flagged += r.Flagged;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            hot.Commit();
            return (spans, withBlock, flagged);
        }

        /// <summary>Series → ComicVine volume: the series link first, else the modal volume of its items' own
        /// matched issue links (a series whose link is ambiguous — "Saga" ties two volumes — still has every
        /// comic matched to the right volume, and that volume carries the Collected-Editions prose).</summary>
        private static Dictionary<int, long> LoadVolumeIds(TargetWriter hot, string seriesIdList)
        {
            var map = new Dictionary<int, long>();
            foreach (var (id, vol) in hot.Pairs(
                $"SELECT Id, coalesce(CvVolumeId, '') FROM Series WHERE Id IN ({seriesIdList})"))
                if (!string.IsNullOrEmpty(vol) && long.TryParse(vol, out var v)) map[(int)id] = v;

            var votes = new Dictionary<int, Dictionary<long, int>>();
            foreach (var (id, payload) in hot.Pairs($@"
SELECT i.SeriesId, l.SecondaryKey || char(31) || count(*)
FROM Item i JOIN ItemProviderLink l ON l.ItemId = i.Id
WHERE l.Provider = {(int)Provider.Cv} AND l.Status = {(int)LinkStatus.Matched} AND l.SecondaryKey IS NOT NULL
  AND i.SeriesId IN ({seriesIdList})
GROUP BY i.SeriesId, l.SecondaryKey"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (!long.TryParse(p[0], out var vol)) continue;
                if (!votes.TryGetValue((int)id, out var d)) votes[(int)id] = d = new Dictionary<long, int>();
                d[vol] = d.GetValueOrDefault(vol) + int.Parse(p[1], CultureInfo.InvariantCulture);
            }
            foreach (var (seriesId, d) in votes)
                if (!map.ContainsKey(seriesId) && d.Count > 0)
                    map[seriesId] = d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            return map;
        }

        /// <summary>Volume → description: the legs rip table first (153k volumes), the hot row as a fallback.</summary>
        private static Dictionary<long, string> LoadDescriptions(TargetWriter hot, SqliteConnection legs, List<long> volumeIds)
        {
            var map = new Dictionary<long, string>();
            foreach (var chunk in ProducerSupport.Chunk(volumeIds, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT CvVolumeId, Description FROM CvVolumeDescription WHERE CvVolumeId IN ("
                                + ProducerSupport.Placeholders(cmd, chunk) + ") AND Description IS NOT NULL";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) map[rd.GetInt64(0)] = rd.GetString(1);
            }
            var missing = volumeIds.Where(v => !map.ContainsKey(v)).ToList();
            if (missing.Count > 0)
                foreach (var (id, desc) in hot.Pairs(
                    $"SELECT Id, Description FROM CvVolume WHERE Id IN ({string.Join(",", missing)}) AND Description IS NOT NULL"))
                    if (!string.IsNullOrEmpty(desc)) map[id] = desc;
            return map;
        }

        private static string Trim(string s, int n) => (s.Length <= n ? s : s[..n]).PadRight(n);
    }
}
