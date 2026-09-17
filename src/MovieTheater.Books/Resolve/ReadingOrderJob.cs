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

        /// <summary>
        /// One run a span's range counts in: the leg it is named on, the run's id there, and the range in
        /// THAT run's numbering (null = the same range as the span's own, which is the ordinary case).
        ///
        /// <para>A LIST, not a map keyed by leg: a trade may collect two minis and an omnibus four, so one
        /// book names several runs on the SAME leg (Hellboy Vol. 06/12, every Library Edition). The same run
        /// named on two legs is two entries too — that is what makes two spans comparable on any shared
        /// (leg, key).</para>
        /// </summary>
        public readonly record struct SpanRun(Provider Provider, string Key, double? Start, double? End);

        /// <summary>
        /// The one span an item is believed to have, plus the RUNS that range counts in.
        ///
        /// <para><paramref name="Runs"/> is the span's <c>CollectedEditionSpanRun</c> rows — "#1-5 OF Wake
        /// the Devil", named on whichever legs have a run concept. Null or empty means unknown, which is what
        /// almost every span says and is NOT a claim that two spans describe the same run.</para>
        /// </summary>
        public readonly record struct SpanInfo(double Start, double End, EditionSource Source, string? Note,
            IReadOnlyList<SpanRun>? Runs = null);

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
            var shelf = new List<(Row Row, double Ours)>();
            var collections = new List<(Row Row, ReadingOrderParser.NormalizedDate Own)>();
            // Everything one series needs, in one read: the parse, the embedded date, the matched CV issue and
            // the matched prog date. Items are the unit; a series holds a few hundred at most.
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id,
       coalesce(i.FileName,'') || char(31) || coalesce(cd.IssueNo,'') || char(31) || coalesce(cd.Format, 13) || char(31)
    || coalesce(cd.VolumeNo,'') || char(31) || coalesce(cd.Year,'') || char(31) || coalesce(ce.PublicationDate,'') || char(31)
    || coalesce(cvi.IssueNumber,'') || char(31) || coalesce(cvi.CoverDate, cvi.StoreDate, '') || char(31)
    || coalesce(bp.CoverDate,'') || char(31) || coalesce(cd.FormatRaw,'') || char(31) || coalesce(cd.IsCollection, 0)
    || char(31) || CASE WHEN cvi.Id IS NOT NULL AND s.CvVolumeId IS NOT NULL AND cvi.VolumeId = s.CvVolumeId THEN 1 ELSE 0 END
FROM Item i
LEFT JOIN Series s ON s.Id = i.SeriesId
LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
LEFT JOIN ItemProviderLink cvl ON cvl.ItemId = i.Id AND cvl.Provider = {(int)Provider.Cv} AND cvl.Status IN {LinkStatuses.UsableSql}
LEFT JOIN CvIssue cvi ON cvi.Id = CAST(cvl.ProviderKey AS INTEGER)
LEFT JOIN ItemProviderLink bl ON bl.ItemId = i.Id AND bl.Provider = {(int)Provider.Barney} AND bl.Status IN {LinkStatuses.UsableSql}
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
                var isCollection = p.Length > 10 && p[10] == "1";
                // Is the matched ComicVine record an ISSUE OF THIS SHELF'S OWN RUN? That is the v1
                // match-a-collection-to-the-issue-numbered-like-its-volume shape. A link into a DIFFERENT
                // ComicVine volume is a match against the edition's own record, and its cover date is the
                // trade's own printing date — the book's own fact, not an issue number coincidence.
                var cvIsOwnRun = p.Length > 11 && p[11] == "1";

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
                var row = new Row
                {
                    ItemId = (int)itemId, SeriesId = seriesId, Tier = tier, Number = number, Suffix = issue.Suffix,
                    Date = date.Iso, DatePrecision = date.Precision, Source = source, Confidence = confidence,
                    Notes = issue.Note, Orderable = orderable,
                };
                rows.Add(row);
                if (tier == ReadingOrderParser.TierMain && number != null
                    && ReadingOrderParser.ParseIssue(issueNo, format, fileName).Number is double ours)
                    shelf.Add((row, ours));
                // A collection's own imprint date — the (b) fallback of the container-date rule. It is computed
                // here because this is the only place the row's local inputs are in hand.
                // A collection's OWN date — the (b) fallback of the container-date rule. Where the row is dated
                // by an issue of this shelf's own run it is the by-number match, and the book's own date is the
                // local chain instead; anything else already IS the book's own (its edition record, its
                // ComicInfo, its year). Computed here because this is the only place those inputs are in hand.
                if (isCollection)
                    collections.Add((row, cvIsOwnRun ? ResolveDate(pubDate, year, claudeYear).Date : date));
            }

            ReconcileCollapsedShelf(shelf);

            // Spans are read at most once per series, and only when something actually asks for them.
            Dictionary<int, SpanInfo>? spanCache = null;
            Dictionary<int, SpanInfo> Spans() =>
                spanCache ??= LoadSpans(hot, seriesId);

            DateContainers(hot, seriesId, rows, collections, Spans);
            PullInCollections(rows, Spans);
            return rows;
        }

        /// <summary>
        /// <b>A collection is dated by what it COLLECTS, never by a per-file provider link.</b>
        ///
        /// <para>v1 matched a collected edition to the ComicVine issue whose number equals its VOLUME number, and
        /// the match survived into v2's `ItemProviderLink(Cv)`: `Saga Vol. 07` carried issue #7's cover date
        /// (2012-11-01) though it collects #37-42, `Vol. 08` carried #8's, `Book 03` carried #3's. Since
        /// `ReadDate` is the top source of every item's resolved year (`ItemResolver.ResolveDate`), a shelf of
        /// trades printed 2014-2022 all read as 2012.</para>
        ///
        /// <para>The rule, for a row the parse calls a collection and whose SELECTED span is a JUDGED
        /// (<see cref="EditionSource.Curated"/>) range — an unjudged provider claim is not enough to re-date a
        /// book: <b>(a)</b> the cover date of the FIRST issue of that range, taken from `CvIssue` for the
        /// shelf's `Series.CvVolumeId` when it is cached, else from the OWNED issue file carrying that number;
        /// <b>(b)</b> failing both, the book's OWN date. NEVER the shelf's own issue matched by number.</para>
        ///
        /// <para><b>Where that line falls.</b> The defect is a link to an ISSUE OF THIS SHELF'S RUN picked
        /// because its number equals the trade's volume ordinal — Saga Vol. 07 → Saga #7. A link into a
        /// DIFFERENT ComicVine volume is a match against the edition's own record in a collected-editions
        /// volume, and its cover date is the trade's printing date: `Terry Moore's Echo Vol. 01` links to CV
        /// volume 47310 #1 at 2009-05-31 while the shelf's run is volume 20806, and its filename says 2018
        /// because that is when it was scanned. So (b) is the local chain
        /// (`ComicEmbedded.PublicationDate` → `ComicDetail.Year` → the insight year) for a same-run match, and
        /// otherwise whatever the row already had. Live, the two halves are 2,611 and 3,301 rows.</para>
        ///
        /// <para>Numbered issue files are untouched, and so is the ordering: a pulled-in collection is placed by
        /// its span number and negative suffix (<see cref="PullInCollections"/>), not by its date.</para>
        /// </summary>
        private static void DateContainers(
            TargetWriter hot, int seriesId, List<Row> rows,
            List<(Row Row, ReadingOrderParser.NormalizedDate Own)> collections,
            Func<Dictionary<int, SpanInfo>> spans)
        {
            if (collections.Count == 0) return;
            var byItem = spans();
            var judged = collections
                .Select(c => (c.Row, c.Own, Span: byItem.TryGetValue(c.Row.ItemId, out var sp) ? sp : default))
                .Where(c => c.Span.Source == EditionSource.Curated && c.Span.Start > 0 && c.Span.End >= c.Span.Start)
                .ToList();
            if (judged.Count == 0) return;

            // The issues we OWN, by number — a collection row can never date another (a trade's own number is
            // the volume ordinal, not an issue number).
            var owned = new Dictionary<double, (string Iso, DatePrecision Precision)>();
            var isCollection = collections.Select(c => c.Row.ItemId).ToHashSet();
            foreach (var r in rows)
                if (!isCollection.Contains(r.ItemId) && r.Number is double n && r.Date is string iso
                    && !owned.ContainsKey(n))
                    owned[n] = (iso, r.DatePrecision);

            // The shelf's ComicVine volume, whether or not any of its issues is a file we hold.
            var cached = new Dictionary<double, string>();
            foreach (var (_, payload) in hot.Pairs($@"
SELECT cvi.Id, coalesce(cvi.IssueNumber,'') || char(31) || coalesce(cvi.CoverDate, cvi.StoreDate, '')
FROM CvIssue cvi JOIN Series s ON s.CvVolumeId = cvi.VolumeId
WHERE s.Id = {seriesId} AND cvi.IssueNumber IS NOT NULL"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p[1].Length == 0) continue;
                if (double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var num)
                    && !cached.ContainsKey(num)) cached[num] = p[1];
            }

            foreach (var (row, own, span) in judged)
            {
                if (cached.TryGetValue(span.Start, out var cvIso)
                    && ReadingOrderParser.NormalizeDate(cvIso) is { Iso: not null } d)
                {
                    row.Date = d.Iso;
                    row.DatePrecision = d.Precision;
                }
                else if (owned.TryGetValue(span.Start, out var file))
                {
                    row.Date = file.Iso;
                    row.DatePrecision = file.Precision;
                }
                else if (own.Iso != null)
                {
                    row.Date = own.Iso;
                    row.DatePrecision = own.Precision;
                }
                // Only the DATE changes. A row that could not be ordered before is not made orderable here —
                // `PullInCollections` is what puts a judged edition on the main line, and it owns that decision.
            }
        }

        /// <summary>
        /// A shelf whose reading numbers COLLAPSE it is being numbered in somebody else's coordinate.
        ///
        /// <para>ComicVine numbers by MINISERIES. Our forty Baltimore files carry a continuous library index and
        /// each arc's own count in the same name — "Baltimore 016 - The Infernal Train 01 (of 03)" — and where a
        /// file is the first of its arc, ComicVine says 1. Ten of them said 1, so the shelf read
        /// 001, 006, 011, 016, 019, 021, 024, 026, 031, 036 and only then 002. Witchfinder collapsed 26 files
        /// onto 10 numbers, Iron Squad 6 onto 2.</para>
        ///
        /// <para>The test needs no provider and no judgement about who is right in general: if OUR parsed issue
        /// numbers tell more of these files apart than the numbers we are about to store, the numbers we are
        /// about to store cannot be this shelf's order. Measured over the live library it fires on seven series
        /// and 2,482 files, six of them this exact shape. Everywhere else ComicVine keeps the say it has earned.</para>
        /// </summary>
        private static void ReconcileCollapsedShelf(List<(Row Row, double Ours)> shelf)
        {
            if (shelf.Count == 0) return;
            var stored = shelf.Select(x => x.Row.Number).Distinct().Count();
            var ours = shelf.Select(x => x.Ours).Distinct().Count();
            if (ours <= stored) return;
            foreach (var (row, mine) in shelf)
            {
                row.Number = mine;
                row.Source = row.Date != null ? ReadingOrderSource.IssueNoDate : ReadingOrderSource.IssueNo;
                row.Notes = string.IsNullOrEmpty(row.Notes)
                    ? "shelf order taken from the filename: the provider numbering collapsed this series"
                    : row.Notes + "; shelf order taken from the filename: the provider numbering collapsed this series";
            }
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
        private static void PullInCollections(
            List<Row> rows,
            Func<Dictionary<int, SpanInfo>> loadSpans)
        {
            if (rows.Count(r => r.Tier == ReadingOrderParser.TierMain && r.Orderable) < 3) return;

            var spans = loadSpans();
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
        public static Dictionary<int, SpanInfo> LoadSpans(TargetWriter hot, int? seriesId = null)
        {
            // Narrowed by the ITEM's series, never by the span's own denormalized `SeriesId`: that copy goes
            // stale when identity moves an item (1,397 of 9,911 rows disagree with `Item.SeriesId` today), and
            // the per-series filter then silently hid the span from the very series that owns the book —
            // "Wolverine Omnibus Book 03" kept a Curated #31-59 nobody could see. Item.SeriesId is the grouping
            // authority everywhere else in these two jobs; it is the authority here too.
            var where = seriesId == null ? "" : $" AND i.SeriesId = {seriesId}";

            // A judged REFUSAL is recorded as a Curated row with no range — a tombstone. It means the shelf
            // was read and no range is known, and it has to be louder than a provider leg, or the vacuum just
            // refills: retracting Hellboy Omnibus Vol. 04's wrong #11-12 handed the book straight to a GCD
            // #1-10 that is wronger. §6.5 measured 746 containers that went from honest silence to a leg's
            // claim this way. Silence is the answer; these items are dropped before anything is ranked.
            var refused = hot.Pairs($@"
SELECT ces.ItemId, '' FROM CollectedEditionSpan ces JOIN Item i ON i.Id = ces.ItemId
WHERE ces.Source = {(int)EditionSource.Curated} AND ces.IssueStart IS NULL{where}")
                .Select(r => (int)r.Item1).ToHashSet();
            var byItem = new Dictionary<int, (bool IsCollection, int PageCount, double? VolumeNo,
                                              List<SpanSelection.Candidate> Candidates)>();

            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT ces.ItemId,
       ces.Source || char(31) || coalesce(ces.IssueStart,'') || char(31) || coalesce(ces.IssueEnd,'') || char(31)
    || coalesce(ces.Confidence,'') || char(31) || coalesce(ces.Note,'') || char(31)
    || coalesce((SELECT l.MatchedKey FROM ItemProviderLink l
                 WHERE l.ItemId = ces.ItemId AND l.Provider = {(int)Provider.Gcd}), '') || char(31)
    || coalesce(cd.IsCollection, 0) || char(31) || coalesce(i.PageCount, 0) || char(31)
    || coalesce(cd.VolumeNo, '')
FROM CollectedEditionSpan ces
JOIN Item i ON i.Id = ces.ItemId
LEFT JOIN ComicDetail cd ON cd.ItemId = ces.ItemId
WHERE ces.IssueStart IS NOT NULL AND ces.IssueEnd IS NOT NULL{where}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p[1].Length == 0 || p[2].Length == 0) continue;
                var id = (int)itemId;
                if (refused.Contains(id)) continue;
                if (!byItem.TryGetValue(id, out var entry))
                    byItem[id] = entry = (p[6] == "1", int.Parse(p[7]),
                                          p.Length > 8 && p[8].Length > 0
                                              ? double.Parse(p[8], System.Globalization.CultureInfo.InvariantCulture)
                                              : null,
                                          new List<SpanSelection.Candidate>());
                entry.Candidates.Add(new SpanSelection.Candidate(
                    (EditionSource)int.Parse(p[0]),
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                    p[3].Length == 0 ? null : double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                    Blank(p[4]), Blank(p[5])));
            }

            // The run refs of every span in scope, keyed by (item, source) — the WINNING span's refs are the
            // ones that travel, so they are attached after the selection, not before it. One sweep, the same
            // shape as the candidate read above; items with none (almost all of them) get null.
            var runsBySpan = new Dictionary<(int ItemId, EditionSource Source), List<SpanRun>>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT r.ItemId, r.Source || char(31) || r.Provider || char(31) || r.ProviderKey || char(31)
    || coalesce(r.IssueStart,'') || char(31) || coalesce(r.IssueEnd,'')
FROM CollectedEditionSpanRun r
JOIN Item i ON i.Id = r.ItemId{where}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (p.Length < 3 || p[2].Length == 0) continue;
                var key = ((int)itemId, (EditionSource)int.Parse(p[0]));
                if (!runsBySpan.TryGetValue(key, out var list)) runsBySpan[key] = list = new List<SpanRun>();
                list.Add(new SpanRun((Provider)int.Parse(p[1]), p[2], Dbl(p, 3), Dbl(p, 4)));
            }

            static double? Dbl(string[] p, int at) =>
                p.Length > at && p[at].Length > 0
                    ? double.Parse(p[at], System.Globalization.CultureInfo.InvariantCulture) : null;

            var spans = new Dictionary<int, SpanInfo>();
            foreach (var (id, entry) in byItem)
                if (SpanSelection.Select(entry.Candidates, entry.IsCollection, entry.PageCount, entry.VolumeNo)
                        is SpanSelection.Candidate w)
                    spans[id] = new SpanInfo(w.Start, w.End, w.Source, w.Note,
                        runsBySpan.TryGetValue((id, w.Source), out var runs) ? runs : null);
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
        /// <summary>
        /// A degenerate span (<c>End &lt;= Start</c>) on a collection-shaped book is normally a number echoed
        /// back rather than a range — a 1,226-page omnibus does not collect one issue.
        ///
        /// <para>Two different things produce one: a provider echoing its own match key, and a curated row
        /// that recorded the VOLUME ORDINAL where the range belonged ("Transformers Classics Vol. 01" →
        /// <c>#1-1</c> over 318 pages, its own note naming the real contents). Both are noise, and the
        /// discriminator is the ordinal: 30 curated degenerates equal their volume number, and every one is an
        /// artefact.</para>
        ///
        /// <para>What survives is the genuine one-issue edition, where the number is NOT the ordinal — Batman
        /// #238, a 100-page giant; Fables Vol. 22 "Farewell", which IS issue #150. Discarding those cost 19
        /// correct spans their win and left the books with no containment at all.</para>
        /// </summary>
        public static bool IsDiscardable(Candidate c, bool isCollection, int pageCount, double? volumeNo = null)
        {
            if (c.End > c.Start) return false;
            if (c.Source != EditionSource.Curated) return isCollection || pageCount >= DegeneratePageFloor;
            // a curated degenerate that merely restates the volume ordinal is the ordinal, not a range
            return volumeNo is { } v && Math.Abs(c.Start - v) < 0.001;
        }

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
        public static Candidate? Select(IReadOnlyList<Candidate> candidates, bool isCollection, int pageCount,
                                        double? volumeNo = null)
        {
            Candidate? best = null;
            var bestKey = (Rank: int.MaxValue, Conf: double.MinValue, Legacy: int.MaxValue);
            foreach (var c in candidates)
            {
                if (IsDiscardable(c, isCollection, pageCount, volumeNo)) continue;
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
