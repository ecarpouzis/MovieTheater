using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Parse;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-reading-order</c> — the DERIVED per-issue reading position of every comic, rebuilt per series.
    ///
    /// <para><b>The chain, best signal first:</b> a matched ComicVine issue's own number and cover date; then the
    /// parsed issue number with the local publication date; then the current series insight's start year as a
    /// coarse last resort. A 2000 AD prog's scraped cover date upgrades anything weaker than Day precision (and
    /// only that — ComicVine's own day-precise date stays authoritative).</para>
    ///
    /// <para><b>The run is grouped by `Item.SeriesId`</b>, not by a reconstructed name key: v2 already resolved
    /// series identity, so the reading order inherits it and cannot disagree with the browse.</para>
    ///
    /// <para><b>A collected edition with a known span is pulled onto the main line</b> at its span start, with a
    /// negative suffix so it sorts just BEFORE the first issue it collects and wider spans (the omnibus) come
    /// before narrower ones (the TPB). That only happens in a REAL issue run — three or more orderable main-tier
    /// issues — so a volume-numbered manga run is left on its own scale.</para>
    ///
    /// <para><b>Chunked by series.</b> The cursor is `Series.Id`, the batch query's own ordering. Each batch
    /// rewrites only its own series' rows, so a killed run leaves every finished series correct.</para>
    /// </summary>
    public static class ReadingOrderJob
    {
        public const string DerivedName = "ReadingOrderEntry";

        private sealed class Row
        {
            public int ItemId;
            public int? SeriesId;
            public int Tier;
            public double? Number;
            public double Suffix;
            public string? Date;
            public DatePrecision DatePrecision;
            public ReadingOrderSource Source = ReadingOrderSource.Unordered;
            public Confidence Confidence = Confidence.Low;
            public string? Notes;
            public bool Orderable;
            public int? ReadIndex;
            public int ReadCount;
        }

        public sealed record BatchResult(int Processed, long Remaining, long? NextCursor, int Rows)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\" }}  [reading-order, rows: {Rows}]";
        }

        /// <summary>Rebuild one page of series. Returns where it stopped; the caller loops.</summary>
        public static BatchResult RunBatch(TargetWriter hot, long afterSeriesId, int batchSize, int? onlySeriesId = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);

            var seriesIds = onlySeriesId is int only
                ? new List<long> { only }
                : hot.Pairs($"SELECT Id, '' FROM Series WHERE Id > {afterSeriesId} ORDER BY Id LIMIT {batchSize}")
                     .Select(p => p.Item1).ToList();
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0);

            var idList = string.Join(",", seriesIds);
            var claudeYear = LoadClaudeYears(hot, idList);
            var written = 0;

            foreach (var seriesId in seriesIds)
            {
                var rows = BuildRows(hot, (int)seriesId, claudeYear.GetValueOrDefault((int)seriesId));
                Order(rows);
                hot.Exec($"DELETE FROM ReadingOrderEntry WHERE SeriesId = {seriesId}");
                var now = DateTime.UtcNow;
                foreach (var r in rows)
                {
                    hot.Upsert("ReadingOrderEntry", new
                    {
                        ItemId = r.ItemId,
                        SeriesId = r.SeriesId,
                        ReadTier = r.Tier,
                        ReadNumber = r.Number,
                        ReadNumberSuffix = r.Suffix,
                        ReadDate = r.Date,
                        ReadDatePrecision = r.DatePrecision,
                        ReadIndex = r.ReadIndex,
                        ReadCount = r.ReadCount,
                        Source = r.Source,
                        Confidence = r.Confidence,
                        Notes = r.Notes,
                        ComputedAt = now,
                    });
                    written++;
                }
            }

            var next = seriesIds[^1];
            var remaining = onlySeriesId != null ? 0 : hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}");
            return new BatchResult(seriesIds.Count, remaining, next, written);
        }

        /// <summary>Drain every series (the CLI verb's default and the admin's recompute trigger).</summary>
        public static int RunAll(TargetWriter hot, int batchSize, Action<string> log, int? onlySeriesId = null)
        {
            long cursor = 0;
            var total = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, cursor, batchSize, onlySeriesId);
                hot.Commit();
                total += r.Rows;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
                if (onlySeriesId != null) break;
            }
            hot.Begin();
            Stamp(hot);
            hot.Commit();
            return total;
        }

        // ── the per-series computation ───────────────────────────────────────────────────────────────────

        private static List<Row> BuildRows(TargetWriter hot, int seriesId, int? claudeYear)
        {
            var rows = new List<Row>();
            // Everything one series needs, in one read: the parse, the embedded date, the matched CV issue and
            // the matched prog date. Items are the unit; a series holds a few hundred at most.
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id,
       coalesce(i.FileName,'') || char(31) || coalesce(cd.IssueNo,'') || char(31) || coalesce(cd.Format, 13) || char(31)
    || coalesce(cd.VolumeNo,'') || char(31) || coalesce(cd.Year,'') || char(31) || coalesce(ce.PublicationDate,'') || char(31)
    || coalesce(cvi.IssueNumber,'') || char(31) || coalesce(cvi.CoverDate, cvi.StoreDate, '') || char(31)
    || coalesce(bp.CoverDate,'') || char(31) || coalesce(cd.FormatRaw,'')
FROM Item i
LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
LEFT JOIN ItemProviderLink cvl ON cvl.ItemId = i.Id AND cvl.Provider = {(int)Provider.Cv} AND cvl.Status = {(int)LinkStatus.Matched}
LEFT JOIN CvIssue cvi ON cvi.Id = CAST(cvl.ProviderKey AS INTEGER)
LEFT JOIN ItemProviderLink bl ON bl.ItemId = i.Id AND bl.Provider = {(int)Provider.Barney}
LEFT JOIN BarneyProg bp ON bp.ProgNo = CAST(bl.ProviderKey AS INTEGER)
WHERE i.SeriesId = {seriesId} AND i.Kind = 0 AND coalesce(i.IsExcluded, 0) = 0
ORDER BY i.Id"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                var fileName = p[0];
                var issueNo = Blank(p[1]);
                var format = (ComicFormat)int.Parse(p[2]);
                var volumeNo = p[3].Length == 0 ? (int?)null : int.Parse(p[3]);
                var year = p[4].Length == 0 ? (int?)null : int.Parse(p[4]);
                var pubDate = Blank(p[5]);
                var cvNumber = Blank(p[6]);
                var cvDate = Blank(p[7]);
                var progDate = Blank(p[8]);
                var formatRaw = Blank(p[9]);

                var haveCvIssue = cvNumber != null || cvDate != null;
                ReadingOrderParser.IssueOrder issue;
                ReadingOrderParser.NormalizedDate date;
                var fromClaudeYear = false;

                if (haveCvIssue)
                {
                    issue = ReadingOrderParser.ParseIssue(cvNumber, format, fileName);
                    date = ReadingOrderParser.NormalizeDate(cvDate);
                    if (date.Iso == null) (date, fromClaudeYear) = ResolveDate(pubDate, year, claudeYear);
                    if (issue.Number == null) issue = ReadingOrderParser.ParseIssue(issueNo, format, fileName);
                }
                else
                {
                    issue = ReadingOrderParser.ParseIssue(issueNo, format, fileName);
                    (date, fromClaudeYear) = ResolveDate(pubDate, year, claudeYear);
                }

                // A prog cover date upgrades anything weaker than Day precision, and nothing else.
                if (date.Precision != DatePrecision.Day && ReadingOrderParser.NormalizeProgDate(progDate) is string iso)
                {
                    date = ReadingOrderParser.NormalizeDate(iso);
                    fromClaudeYear = false;
                }

                // A collected edition carrying a VOLUME number but no issue number (manga TPBs) orders by that
                // volume — otherwise the volumes read by printing date instead of 1..N.
                var volumeAsNumber = issue.Number == null && volumeNo != null && issue.Tier == ReadingOrderParser.TierCollection;
                var number = volumeAsNumber ? volumeNo : issue.Number;

                var source = haveCvIssue ? ReadingOrderSource.ComicVine
                    : number != null && date.Iso != null ? (fromClaudeYear ? ReadingOrderSource.IssueNoClaudeYear : ReadingOrderSource.IssueNoDate)
                    : number != null ? ReadingOrderSource.IssueNo
                    : date.Iso != null ? (fromClaudeYear ? ReadingOrderSource.ClaudeYear : ReadingOrderSource.Date)
                    : ReadingOrderSource.Unordered;
                var confidence = haveCvIssue ? Confidence.High
                    : number != null ? (issue.Tier == ReadingOrderParser.TierMain ? Confidence.High : Confidence.Medium)
                    : Confidence.Low;

                var orderable = number != null || date.Iso != null;
                var tier = orderable ? issue.Tier : ReadingOrderParser.TierUnorderable;
                if (!orderable) source = ReadingOrderSource.Unordered;

                _ = formatRaw;
                rows.Add(new Row
                {
                    ItemId = (int)itemId, SeriesId = seriesId, Tier = tier, Number = number, Suffix = issue.Suffix,
                    Date = date.Iso, DatePrecision = date.Precision, Source = source, Confidence = confidence,
                    Notes = issue.Note, Orderable = orderable,
                });
            }

            PullInCollections(hot, seriesId, rows);
            return rows;
        }

        /// <summary>
        /// The containment pull-in: in a REAL issue run (three or more orderable main-tier issues), a collected
        /// edition whose span is known joins the main line at its span start. The negative suffix sorts it just
        /// before the first issue it collects, and a WIDER span sorts first — so the omnibus precedes the TPB
        /// that precedes the issues.
        /// </summary>
        private static void PullInCollections(TargetWriter hot, int seriesId, List<Row> rows)
        {
            if (rows.Count(r => r.Tier == ReadingOrderParser.TierMain && r.Orderable) < 3) return;

            var spans = LoadSpans(hot, seriesId);
            foreach (var r in rows)
            {
                if (r.Tier != ReadingOrderParser.TierCollection || !spans.TryGetValue(r.ItemId, out var sp)) continue;
                if (sp.End < sp.Start || sp.Start <= 0) continue;
                r.Tier = ReadingOrderParser.TierMain;
                r.Number = sp.Start;
                r.Suffix = -1 - (sp.End - sp.Start);
                r.Source = ReadingOrderSource.Containment;
                if (r.Confidence == Confidence.Low) r.Confidence = Confidence.Medium;
                r.Notes = $"collects #{sp.Start:0.##}-#{sp.End:0.##} [{sp.Source}]";
                r.Orderable = true;
            }
        }

        /// <summary>
        /// The one span per item, by the containment precedence <b>Locg &gt; Gcd &gt; Cv &gt; Curated</b>. LOCG
        /// is first because it comes from an explicit per-issue containment edge list — the relationship behind
        /// its own navigation — where GCD's reprint graph is spotty and ComicVine's is a free-text parse.
        /// </summary>
        public static Dictionary<int, (double Start, double End, EditionSource Source)> LoadSpans(TargetWriter hot, int? seriesId = null)
        {
            var spans = new Dictionary<int, (double, double, EditionSource)>();
            var where = seriesId == null ? "" : $" AND SeriesId = {seriesId}";
            // Reverse precedence order: a later load overwrites an earlier one.
            foreach (var source in new[] { EditionSource.Curated, EditionSource.Cv, EditionSource.Gcd, EditionSource.Locg })
                foreach (var (itemId, payload) in hot.Pairs(
                    $"SELECT ItemId, coalesce(IssueStart,'') || char(31) || coalesce(IssueEnd,'') FROM CollectedEditionSpan WHERE Source = {(int)source}{where}"))
                {
                    var p = payload!.Split(TargetWriter.Sep);
                    if (p[0].Length == 0 || p[1].Length == 0) continue;
                    spans[(int)itemId] = (double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                                          double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), source);
                }
            return spans;
        }

        private static void Order(List<Row> rows)
        {
            var ordered = rows
                .OrderBy(r => r.Tier)
                .ThenBy(r => r.Number == null)
                .ThenBy(r => r.Number ?? 0)
                .ThenBy(r => r.Suffix)
                .ThenBy(r => r.Date == null)
                .ThenBy(r => r.Date, StringComparer.Ordinal)
                .ThenBy(r => r.ItemId)
                .ToList();

            var count = ordered.Count(r => r.Orderable);
            var idx = 0;
            foreach (var r in ordered)
            {
                r.ReadIndex = r.Orderable ? ++idx : null;
                r.ReadCount = count;
            }
        }

        /// <summary>
        /// The coarse last resort: the current SERIES insight's start year, and only at High or Medium
        /// confidence. It is series-level, so it cannot tell relaunches apart — which is why it never overrides
        /// a real date.
        /// </summary>
        private static Dictionary<int, int?> LoadClaudeYears(TargetWriter hot, string seriesIdList)
        {
            var map = new Dictionary<int, int?>();
            foreach (var (seriesId, year) in hot.Pairs($@"
SELECT SubjectId, CAST(YearBegin AS TEXT) FROM Insight
WHERE SubjectKind = {(int)SubjectKind.Series} AND IsCurrent = 1 AND YearBegin IS NOT NULL
  AND Confidence IN ({(int)Confidence.High}, {(int)Confidence.Medium}) AND SubjectId IN ({seriesIdList})"))
                map[(int)seriesId] = int.Parse(year!);
            return map;
        }

        private static (ReadingOrderParser.NormalizedDate Date, bool FromClaude) ResolveDate(string? pubDate, int? year, int? claudeYear)
        {
            var d = ReadingOrderParser.NormalizeDate(pubDate);
            if (d.Iso == null && year is int y) d = ReadingOrderParser.NormalizeDate(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (d.Iso == null && claudeYear is int cy)
                return (ReadingOrderParser.NormalizeDate(cy.ToString(System.Globalization.CultureInfo.InvariantCulture)), true);
            return (d, false);
        }

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
                RowCount = (int)hot.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry"),
            });
        }

        /// <summary>
        /// <c>books-reading-order-audit</c> — one CSV row per series: how many issues it holds, how many the
        /// order could place, and which signal won. This is the sheet that tells an operator WHERE the order is
        /// guesswork, without opening the database.
        /// </summary>
        public static IEnumerable<string> AuditCsv(TargetWriter hot)
        {
            yield return "seriesId,seriesName,issues,ordered,unordered,comicVine,issueNoDate,issueNo,date,claudeYear,containment";
            foreach (var (seriesId, payload) in hot.Pairs($@"
SELECT s.Id,
       replace(coalesce(s.DisplayNameOverride, s.Name, ''), ',', ' ') || char(31)
    || count(ro.ItemId) || char(31)
    || sum(CASE WHEN ro.ReadIndex IS NOT NULL THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.Unordered} THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.ComicVine} THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.IssueNoDate} THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.IssueNo} THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.Date} THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source IN ({(int)ReadingOrderSource.ClaudeYear}, {(int)ReadingOrderSource.IssueNoClaudeYear}) THEN 1 ELSE 0 END) || char(31)
    || sum(CASE WHEN ro.Source = {(int)ReadingOrderSource.Containment} THEN 1 ELSE 0 END)
FROM Series s JOIN ReadingOrderEntry ro ON ro.SeriesId = s.Id
GROUP BY s.Id ORDER BY s.Id"))
                yield return seriesId + "," + string.Join(",", payload!.Split(TargetWriter.Sep));
        }
    }
}
