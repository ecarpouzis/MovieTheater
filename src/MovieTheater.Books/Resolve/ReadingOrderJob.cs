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
        /// <summary>The persisted cursor — the SAME key the admin recompute route pages with (see <see cref="JobCursor"/>).</summary>
        public const string CursorKey = "books:recompute:reading-order";

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

        /// <summary>
        /// Drain every series (the CLI verb's default and the admin's recompute trigger). The cursor is persisted
        /// with each batch under <see cref="CursorKey"/> and cleared on completion; <paramref name="resume"/>
        /// starts from it instead of series 0, so a killed run continues rather than re-walking 19k series.
        /// </summary>
        public static int RunAll(TargetWriter hot, int batchSize, Action<string> log, int? onlySeriesId = null, bool resume = false)
        {
            var whole = onlySeriesId == null;
            long cursor = whole && resume ? JobCursor.Read(hot, CursorKey) : 0;
            var total = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, cursor, batchSize, onlySeriesId);
                if (whole && r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                total += r.Rows;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
                if (onlySeriesId != null) break;
            }
            hot.Begin();
            if (whole) JobCursor.Clear(hot, CursorKey);
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
LEFT JOIN ItemProviderLink bl ON bl.ItemId = i.Id AND bl.Provider = {(int)Provider.Barney} AND bl.Status = {(int)LinkStatus.Matched}
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
        /// The containment pull-in: in a REAL issue run (three or more orderable main-tier issues), ANY row whose
        /// span was selected joins the main line at its span start. The negative suffix sorts it just before the
        /// first issue it collects, and a WIDER span sorts first — so the omnibus precedes the TPB that precedes
        /// the issues.
        ///
        /// <para>The row's parsed tier does not gate this any more. A collected edition mis-parsed as a single
        /// issue used to keep its wrong main-line NUMBER (the volume number read as an issue number) even though
        /// its span said exactly where it belongs; now the span wins wherever there is one.</para>
        /// </summary>
        private static void PullInCollections(TargetWriter hot, int seriesId, List<Row> rows)
        {
            if (rows.Count(r => r.Tier == ReadingOrderParser.TierMain && r.Orderable) < 3) return;

            var spans = LoadSpans(hot, seriesId);
            foreach (var r in rows)
            {
                // A SELECTED span is the authority on where a row belongs, whatever TIER the parse gave it: the
                // span says "this thing collects #a-#b", which is a containment position and a stronger
                // statement than a format guess. (4,681 rows carried a span while sitting on the main line at
                // the wrong number, because their filename's volume number had been read as an issue number.)
                if (!spans.TryGetValue(r.ItemId, out var sp)) continue;
                if (sp.End < sp.Start || sp.Start <= 0) continue;
                // …but a row that already holds a number OF ITS OWN outside the collection tier is not moved.
                // Provider spans are not clean enough to relocate an issue: a shell-page span claiming to
                // collect a whole series dragged 1,647 genuine 32-page issues (2000 AD progs, "Iron Man 2020")
                // and a shelf of numbered specials to the front of their runs. A collected edition is exempt
                // because its number was never its own — it is the volume index, which is precisely what the
                // collected-edition parse rule takes away.
                if (r.Tier != ReadingOrderParser.TierCollection && r.Number != null) continue;
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
        /// The one span per item, chosen by <see cref="SpanSelection.Select"/> — the precedence Curated &gt;
        /// Cv-by-edition-title &gt; complete LOCG &gt; { Gcd-by-title, legacy Cv } &gt; partial LOCG &gt;
        /// issue-keyed Gcd, with degenerate "#N-#N" spans discarded on anything shaped like a collection.
        /// One read per call: the candidates plus the two facts the selection needs about the item itself.
        /// </summary>
        public static Dictionary<int, (double Start, double End, EditionSource Source)> LoadSpans(TargetWriter hot, int? seriesId = null)
        {
            // Narrowed by the ITEM's series, never by the span's own denormalized `SeriesId`: that copy goes
            // stale when identity moves an item (1,397 of 9,911 rows disagree with `Item.SeriesId` today), and
            // the per-series filter then silently hid the span from the very series that owns the book —
            // "Wolverine Omnibus Book 03" kept a Curated #31-59 nobody could see. Item.SeriesId is the grouping
            // authority everywhere else in these two jobs; it is the authority here too.
            var where = seriesId == null ? "" : $" AND i.SeriesId = {seriesId}";
            var byItem = new Dictionary<int, (bool IsCollection, int PageCount, List<SpanSelection.Candidate> Candidates)>();

            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT ces.ItemId,
       ces.Source || char(31) || coalesce(ces.IssueStart,'') || char(31) || coalesce(ces.IssueEnd,'') || char(31)
    || coalesce(ces.Confidence,'') || char(31) || coalesce(ces.Note,'') || char(31)
    || coalesce((SELECT l.MatchedKey FROM ItemProviderLink l
                 WHERE l.ItemId = ces.ItemId AND l.Provider = {(int)Provider.Gcd}), '') || char(31)
    || coalesce(cd.IsCollection, 0) || char(31) || coalesce(i.PageCount, 0)
FROM CollectedEditionSpan ces
JOIN Item i ON i.Id = ces.ItemId
LEFT JOIN ComicDetail cd ON cd.ItemId = ces.ItemId
WHERE ces.IssueStart IS NOT NULL AND ces.IssueEnd IS NOT NULL{where}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p[1].Length == 0 || p[2].Length == 0) continue;
                var id = (int)itemId;
                if (!byItem.TryGetValue(id, out var entry))
                    byItem[id] = entry = (p[6] == "1", int.Parse(p[7]), new List<SpanSelection.Candidate>());
                entry.Candidates.Add(new SpanSelection.Candidate(
                    (EditionSource)int.Parse(p[0]),
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                    p[3].Length == 0 ? null : double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                    Blank(p[4]), Blank(p[5])));
            }

            var spans = new Dictionary<int, (double, double, EditionSource)>();
            foreach (var (id, entry) in byItem)
                if (SpanSelection.Select(entry.Candidates, entry.IsCollection, entry.PageCount) is SpanSelection.Candidate w)
                    spans[id] = (w.Start, w.End, w.Source);
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

    /// <summary>
    /// WHICH "this edition collects #a-#b" claim to believe when several providers disagree — the pure half of
    /// <see cref="ReadingOrderJob.LoadSpans"/>, shared by the reading order and the containment job.
    ///
    /// <para>The old rule was source order alone (Locg &gt; Gcd &gt; Cv &gt; Curated) whatever the row said, and
    /// it lost 491 of 739 disagreements to a lower-confidence answer: 3,900 GCD spans come from a match keyed on
    /// the (wrongly parsed) ISSUE NUMBER, and many are degenerate "#44-#44" claims about a 1,226-page omnibus —
    /// "Wolverine Omnibus Book 03" took Gcd #44-44 at 0.8 over Curated #31-59 at 0.97, "Saga Book 1" took Gcd
    /// #1-6 over Cv #1-18 at 1.0.</para>
    ///
    /// <para><b>The rule now</b> (2026-09-07, after the containment repair). (a) A degenerate span
    /// (End == Start) on something shaped like a collection — the parse says so, or it is
    /// <see cref="DegeneratePageFloor"/> pages or more — is DISCARDED: a book that thick does not collect one
    /// issue. (b) The six classes, best first: <b>0</b> Curated (hand-read indicia); <b>1</b> Cv matched on the
    /// EDITION TITLE against ComicVine's own "Collected Editions" list (`books-cv-spans`, note
    /// <c>match-by: title</c>) — the publisher's own statement of what the edition collects; <b>2</b> a LOCG
    /// span whose contained count EQUALS its width, i.e. a complete table of contents; <b>3</b> GCD matched on
    /// the edition title, and a legacy Cv span with no title-match note (it may be right, but nothing in the
    /// row says how it was decided); <b>4</b> a PARTIAL LOCG span — LOCG truncates to the subset it scraped, so
    /// "#1-4" from three edges is a LOWER BOUND, not a range; <b>5</b> issue-keyed GCD, which
    /// <c>books-gcd-spans</c> no longer produces and which survives only as a safety net for rows it has not
    /// yet visited. (c) Within a class the higher <c>Confidence</c> wins, with the old source order as the
    /// tiebreak.</para>
    /// </summary>
    public static class SpanSelection
    {
        /// <summary>At or above this many pages, a "collects #N-#N" claim is self-evidently wrong.</summary>
        public const int DegeneratePageFloor = 100;

        /// <summary>One provider's claim about one item.</summary>
        public readonly record struct Candidate(
            EditionSource Source, double Start, double End, double? Confidence, string? Note, string? GcdMatchedKey);

        private static readonly System.Text.RegularExpressions.Regex RxBareIssueKey =
            new(@"^\s*\d+(?:\.\d+)?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxMatchByNum =
            new(@"match-by:\s*num", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxMatchByTitle =
            new(@"match-by:\s*title", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxContained =
            new(@"contained:?\s*(\d+)|\b(\d+)\s+contained\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Was this GCD span matched on a BARE ISSUE NUMBER rather than the edition's title? The note says so
        /// outright ("match-by: num" / "match-by: title"); when it is silent, a numeric `MatchedKey` on the GCD
        /// link is the same signal. A title match keyed to a numeric volume ("Rachel Rising Vol. 01" → key "1",
        /// "match-by: title") is NOT issue-keyed and keeps its rank.
        /// </summary>
        public static bool IsIssueKeyedGcd(Candidate c)
        {
            if (c.Source != EditionSource.Gcd) return false;
            if (c.Note != null && RxMatchByNum.IsMatch(c.Note)) return true;
            if (c.Note != null && RxMatchByTitle.IsMatch(c.Note)) return false;
            return c.GcdMatchedKey != null && RxBareIssueKey.IsMatch(c.GcdMatchedKey);
        }

        /// <summary>Was this span produced by an EDITION-TITLE match against a container record? Only the
        /// containment-repair producers (`books-cv-spans`, `books-gcd-spans`) say so, and only they may.</summary>
        public static bool IsTitleMatched(Candidate c) => c.Note != null && RxMatchByTitle.IsMatch(c.Note);

        /// <summary>The number of contained issues a LOCG note reports ("{n} contained"), or null.</summary>
        public static int? ContainedCount(string? note)
        {
            if (note == null) return null;
            var m = RxContained.Match(note);
            if (!m.Success) return null;
            var n = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return int.TryParse(n, out var count) ? count : null;
        }

        /// <summary>
        /// A LOCG span is COMPLETE when the number of edges it was reduced from covers its whole width. LOCG
        /// truncates to whatever the scrape actually saw, so a span reduced from three edges but spanning ten
        /// issues is a lower bound on the range, not the range — and a span reduced from ONE edge is a shell
        /// page, not a table of contents, whatever its width.
        /// </summary>
        public static bool LocgIsComplete(Candidate c) =>
            ContainedCount(c.Note) is int n && n >= 2 && n >= (int)(c.End - c.Start) + 1;

        /// <summary>A "#N-#N" claim about a collection is no claim at all — it is the match's own key echoed back.</summary>
        public static bool IsDiscardable(Candidate c, bool isCollection, int pageCount) =>
            c.End <= c.Start && (isCollection || pageCount >= DegeneratePageFloor);

        /// <summary>The class rank: lower is better. 0 Curated, 1 Cv title-matched, 2 complete LOCG,
        /// 3 GCD title-matched (and a legacy Cv row with no title-match note), 4 partial LOCG,
        /// 5 issue-keyed GCD.</summary>
        public static int Rank(Candidate c) => c.Source switch
        {
            EditionSource.Curated => 0,
            EditionSource.Cv => IsTitleMatched(c) ? 1 : 3,
            EditionSource.Locg => LocgIsComplete(c) ? 2 : 4,
            _ => IsIssueKeyedGcd(c) ? 5 : 3,
        };

        /// <summary>The historical order, kept as the tiebreak when two claims rank and score the same.</summary>
        private static int LegacyOrder(EditionSource s) => s switch
        {
            EditionSource.Locg => 0, EditionSource.Gcd => 1, EditionSource.Cv => 2, _ => 3,
        };

        /// <summary>The winner, or null when every claim was discarded (the edition stays a labelled leaf).</summary>
        public static Candidate? Select(IReadOnlyList<Candidate> candidates, bool isCollection, int pageCount)
        {
            Candidate? best = null;
            var bestKey = (Rank: int.MaxValue, Conf: double.MinValue, Legacy: int.MaxValue);
            foreach (var c in candidates)
            {
                if (IsDiscardable(c, isCollection, pageCount)) continue;
                var key = (Rank(c), c.Confidence ?? 0, LegacyOrder(c.Source));
                if (key.Item1 > bestKey.Rank) continue;
                if (key.Item1 == bestKey.Rank && key.Item2 < bestKey.Conf) continue;
                if (key.Item1 == bestKey.Rank && key.Item2 == bestKey.Conf && key.Item3 >= bestKey.Legacy) continue;
                best = c;
                bestKey = key;
            }
            return best;
        }
    }
}
