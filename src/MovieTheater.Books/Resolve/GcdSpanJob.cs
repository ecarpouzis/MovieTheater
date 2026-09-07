using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-gcd-spans</c> — rebuild `CollectedEditionSpan(Source = Gcd)` from the Grand Comics Database's
    /// own REPRINT GRAPH, matching on the edition's title only.
    ///
    /// <para><b>What a GCD container is.</b> GCD records a collected edition as an issue of a COLLECTION series
    /// ("Saga Deluxe Edition"), and records what it reprints as `gcd_reprint` rows whose `target_issue_id` is
    /// that collection issue and whose `origin_issue_id` is the original floppy. So "this record is a container"
    /// has an exact test: it appears as a reprint TARGET. An issue that reprints nothing is not an edition, and
    /// this job will not link a collected edition to it — which is the whole repair. The 3,900 spans keyed
    /// `match-by: num` (the volume ordinal read as an issue number) are DELETED, not re-derived.</para>
    ///
    /// <para><b>The match.</b> The book's series name picks the GCD collection LINE (edition class dominates, so
    /// "Fables Vol. 09" stays on the TPB line rather than the Deluxe one), and inside that line the book is
    /// matched to an issue by TITLE. A GCD collection issue with no title is skipped: a provider that does not
    /// name the edition does not get to claim it.</para>
    ///
    /// <para><b>The dump is read where it lies.</b> `gcd_reprint` is indexed on `target_issue_id` and `gcd_issue`
    /// on `series_id`, so every per-book question is an indexed lookup; only `gcd_series` (230k rows) is scanned
    /// once per run to build the franchise index. Nothing is imported — a 1.5M-row copy would buy nothing that
    /// an indexed read-only open does not already have, and it would be one more thing to keep fresh.</para>
    ///
    /// <para>Chunked by `Series.Id`, delete-then-rewrite per series, dry-run by default.</para>
    /// </summary>
    public static class GcdSpanJob
    {
        public const string DerivedName = "CollectedEditionSpan(Source=Gcd)";
        public const string CursorKey = "books:recompute:gcd-spans";
        public const string TitleMatchNote = "match-by: title";

        /// <summary>Issues at most this far apart still belong to the same collected run (v1's cluster gap).</summary>
        public const double ClusterGap = 3;

        private static readonly Regex RxCollectionFormat =
            new("collect|deluxe|omnibus|trade|tpb|hardcover|compendium|library|absolute|complete|chronicl",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex RxSeriesNoise =
            new(@"\b(vol|volume|book|tp|tpb|hc|edition|deluxe|omnibus|compendium|library|absolute|complete|collection)\b",
                RegexOptions.Compiled);
        private static readonly Regex RxArticles = new(@"\b(the|a|an|of|and)\b", RegexOptions.Compiled);
        private static readonly Regex RxNumber = new(@"-?\d+(\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex RxFileYear = new(@"\((\d{4})\)", RegexOptions.Compiled);

        // ── the index built once per run ────────────────────────────────────────────────────────────────

        public sealed class Index
        {
            public readonly Dictionary<long, (string Name, int Year, bool IsCollection)> Series = [];
            public readonly Dictionary<string, List<long>> Franchise = new(StringComparer.Ordinal);
            private readonly Dictionary<long, List<(long IssueId, string Title)>> issueCache = [];

            /// <summary>The titled issues of one GCD collection series (indexed read, cached per run).</summary>
            public List<(long IssueId, string Title)> TitledIssues(SqliteConnection gcd, long gcdSeriesId)
            {
                if (issueCache.TryGetValue(gcdSeriesId, out var cached)) return cached;
                var list = new List<(long, string)>();
                using var cmd = gcd.CreateCommand();
                cmd.CommandText = "SELECT id, title FROM gcd_issue WHERE series_id = $s AND deleted = 0 AND variant_of_id IS NULL";
                cmd.Parameters.AddWithValue("$s", gcdSeriesId);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var title = rd.IsDBNull(1) ? "" : rd.GetString(1);
                    if (title.Trim().Length > 0) list.Add((rd.GetInt64(0), title));
                }
                return issueCache[gcdSeriesId] = list;
            }
        }

        /// <summary>One scan of `gcd_series` — the only unindexed read in the job.</summary>
        public static Index BuildIndex(SqliteConnection gcd)
        {
            var index = new Index();
            long english = 0;
            using (var lang = gcd.CreateCommand())
            {
                lang.CommandText = "SELECT id FROM stddata_language WHERE code = 'en'";
                english = Convert.ToInt64(lang.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }

            using var cmd = gcd.CreateCommand();
            cmd.CommandText = "SELECT id, name, year_began, publishing_format, format, has_isbn, language_id, deleted FROM gcd_series";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var id = rd.GetInt64(0);
                var name = rd.IsDBNull(1) ? "" : rd.GetString(1);
                var year = rd.IsDBNull(2) ? 0 : rd.GetInt32(2);
                var publishingFormat = rd.IsDBNull(3) ? "" : rd.GetString(3);
                var format = rd.IsDBNull(4) ? "" : rd.GetString(4);
                var hasIsbn = !rd.IsDBNull(5) && rd.GetInt32(5) == 1;
                var language = rd.IsDBNull(6) ? 0 : rd.GetInt64(6);
                var deleted = !rd.IsDBNull(7) && rd.GetInt32(7) == 1;

                var isCollection = RxCollectionFormat.IsMatch(publishingFormat) || RxCollectionFormat.IsMatch(format) || hasIsbn;
                index.Series[id] = (name, year, isCollection);
                if (!isCollection || deleted || (english != 0 && language != english)) continue;
                var key = NormalizeSeries(name);
                if (key.Length == 0) continue;
                if (!index.Franchise.TryGetValue(key, out var list)) index.Franchise[key] = list = [];
                list.Add(id);
            }
            return index;
        }

        // ── the batch ───────────────────────────────────────────────────────────────────────────────────

        public sealed record BatchResult(int Processed, long Remaining, long? NextCursor, int Spans, int Deleted, int Flagged)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", spans: {Spans}, deleted: {Deleted}, pageAuditFlags: {Flagged} }}  [gcd-spans]";
        }

        public static BatchResult RunBatch(TargetWriter hot, SqliteConnection gcd, Index index, long afterSeriesId, int batchSize,
            Action<string>? sample = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var seriesIds = ProducerSupport.SeriesPage(hot, afterSeriesId, batchSize);
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0, 0, 0);

            var idList = string.Join(",", seriesIds);
            var books = ProducerSupport.LoadCollectionBooks(hot, idList);
            var names = ProducerSupport.LoadSeriesNames(hot, idList);

            int spans = 0, flagged = 0;
            // Every Gcd span of every series in the window goes, whether or not this run can replace it: the
            // issue-keyed ones are the fault, and a title match we cannot reproduce is not evidence.
            var deleted = (int)hot.Scalar<long>(
                $"SELECT count(*) FROM CollectedEditionSpan WHERE Source = {(int)EditionSource.Gcd} "
                + $"AND ItemId IN (SELECT Id FROM Item WHERE SeriesId IN ({idList}))");
            foreach (var seriesId in seriesIds) ProducerSupport.ClearSpans(hot, EditionSource.Gcd, seriesId);

            foreach (var seriesId in seriesIds)
            {
                if (!books.TryGetValue(seriesId, out var list) || list.Count == 0) continue;
                var seriesName = names.GetValueOrDefault(seriesId, "");
                var candidates = index.Franchise.GetValueOrDefault(NormalizeSeries(seriesName));
                if (candidates == null || candidates.Count == 0) continue;
                var seriesNorm = EditionMatcher.Norm(seriesName);

                var claims = new List<(CollectionBook Book, long IssueId, string Title, double Confidence, bool Exact)>();
                foreach (var book in list)
                {
                    var line = PickLine(index, candidates, book.FileName);
                    if (line is not long gcdSeriesId) continue;
                    var titled = index.TitledIssues(gcd, gcdSeriesId);
                    if (titled.Count == 0) continue;
                    if (EditionMatcher.MatchTitleOnly(book, titled, seriesNorm) is { } m)
                        claims.Add((book, m.Key, m.Title, m.Confidence, m.Exact));
                }

                foreach (var m in ProducerSupport.ResolveContention(claims, c => c.IssueId, c => c.Confidence))
                {
                    var book = m.Book;
                    var range = ResolveRange(gcd, index, m.IssueId);
                    if (range is not { } r) continue;   // reprints nothing ⇒ not a container ⇒ emit nothing

                    var confidence = (r.Contiguous ? 0.9 : 0.8) * m.Confidence;
                    var flag = PageArithmetic.Flag(book.PageCount, r.Start, r.End);
                    if (flag != null) flagged++;
                    var note = $"{TitleMatchNote} ({(m.Exact ? "exact" : "overlap")}); gcd reprint; {r.Count} issues"
                             + (flag == null ? "" : $"; page-audit: {flag}");
                    ProducerSupport.WriteSpan(hot, EditionSource.Gcd, book, r.Start, r.End, m.Title,
                        m.IssueId.ToString(CultureInfo.InvariantCulture), confidence, r.Contiguous, note);
                    spans++;
                    sample?.Invoke($"  s{seriesId,-6} i{book.ItemId,-6} {Trim(book.FileName, 52)} -> {Trim(m.Title, 34)}"
                        + $" #{ProducerSupport.Fmt(r.Start)}-{ProducerSupport.Fmt(r.End)} @{confidence:0.00}"
                        + (flag == null ? "" : $"  [{flag}]"));
                }
            }

            var next = seriesIds[^1];
            return new BatchResult(seriesIds.Count, hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}"),
                next, spans, deleted, flagged);
        }

        public static (int Spans, int Deleted, int Flagged) RunAll(TargetWriter hot, string gcdPath, int batchSize,
            Action<string> log, bool resume = false, int sampleTop = 25)
        {
            using var gcd = OpenReadOnly(gcdPath);
            var index = BuildIndex(gcd);
            log($"gcd index: {index.Series.Count} series, {index.Franchise.Count} collection franchises");
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            int spans = 0, deleted = 0, flagged = 0, printed = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, gcd, index, cursor, batchSize, s => { if (printed++ < sampleTop) log(s); });
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                spans += r.Spans; deleted += r.Deleted; flagged += r.Flagged;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            hot.Commit();
            return (spans, deleted, flagged);
        }

        public static SqliteConnection OpenReadOnly(string path)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            return conn;
        }

        // ── the pure helpers ────────────────────────────────────────────────────────────────────────────

        /// <summary>Series-name key: lower-cased, punctuation folded, articles and edition words removed — so
        /// "Fables Deluxe Edition" and "Fables" share a franchise.</summary>
        public static string NormalizeSeries(string? s)
        {
            var n = RxArticles.Replace(RxNonAlnum.Replace((s ?? "").ToLowerInvariant(), " ").Trim(), " ");
            n = RxSeriesNoise.Replace(n, " ");
            return Regex.Replace(n, @"\s+", " ").Trim();
        }

        /// <summary>Which edition CLASS a title reads as. It dominates line selection: a plain "Vol. 09" must not
        /// be answered by the Deluxe line, whose volume 9 collects entirely different issues.</summary>
        public static string EditionClass(string? text)
        {
            var t = (text ?? "").ToLowerInvariant();
            if (t.Contains("omnibus", StringComparison.Ordinal)) return "omnibus";
            if (t.Contains("compendium", StringComparison.Ordinal)) return "compendium";
            if (t.Contains("absolute", StringComparison.Ordinal) || t.Contains("library edition", StringComparison.Ordinal)) return "library";
            if (t.Contains("deluxe", StringComparison.Ordinal)) return "deluxe";
            return "tpb";
        }

        private static long? PickLine(Index index, List<long> candidates, string fileName)
        {
            var bookClass = EditionClass(fileName);
            var bookTokens = EditionMatcher.Tokens(EditionMatcher.Norm(StripFileName(fileName)));
            var year = RxFileYear.Match(fileName) is { Success: true } m
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;

            long? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var id in candidates)
            {
                var (name, seriesYear, _) = index.Series[id];
                var seriesTokens = EditionMatcher.Tokens(EditionMatcher.Norm(name));
                if (seriesTokens.Count == 0) continue;
                var union = new HashSet<string>(seriesTokens, StringComparer.Ordinal);
                union.UnionWith(bookTokens);
                var jaccard = union.Count == 0 ? 0 : (double)seriesTokens.Count(bookTokens.Contains) / union.Count;
                var classScore = EditionClass(name) == bookClass ? 2.0 : 0.0;
                var yearScore = 0.0;
                if (year is int y && seriesYear > 0)
                {
                    var d = Math.Abs(y - seriesYear);
                    yearScore = d <= 2 ? 0.15 : d <= 5 ? 0.07 : d <= 12 ? 0 : -0.2;
                }
                var score = classScore + jaccard + yearScore;
                if (score > bestScore) { bestScore = score; best = id; }
            }
            return best;
        }

        private static string StripFileName(string fileName)
        {
            var s = Regex.Replace(fileName, @"\.(cbz|cbr|cb7|pdf|epub)$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\([^)]*\)", " ");
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            return s.Trim();
        }

        /// <summary>
        /// The reprint range of one GCD collection issue: every issue it reprints, grouped by the source series
        /// it came from, the biggest group taken, then clustered so a stray far-off reprint cannot stretch the
        /// span across a whole run. Returns null when the issue reprints nothing — the container test.
        /// </summary>
        public static (double Start, double End, int Count, bool Contiguous)? ResolveRange(SqliteConnection gcd, Index index, long collectionIssueId)
        {
            var origins = new List<long>();
            using (var cmd = gcd.CreateCommand())
            {
                cmd.CommandText = "SELECT origin_issue_id FROM gcd_reprint WHERE target_issue_id = $t";
                cmd.Parameters.AddWithValue("$t", collectionIssueId);
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) if (!rd.IsDBNull(0)) origins.Add(rd.GetInt64(0));
            }
            if (origins.Count == 0) return null;

            var bySource = new Dictionary<long, List<double>>();
            foreach (var chunk in ProducerSupport.Chunk(origins.Distinct().ToList(), 400))
            {
                using var cmd = gcd.CreateCommand();
                cmd.CommandText = "SELECT series_id, number FROM gcd_issue WHERE id IN (" + ProducerSupport.Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var sid = rd.IsDBNull(0) ? 0L : rd.GetInt64(0);
                    var number = ParseNumber(rd.IsDBNull(1) ? null : rd.GetString(1));
                    if (number is not double n) continue;
                    // A collection reprinting another collection tells us nothing about issue numbers.
                    if (index.Series.TryGetValue(sid, out var meta) && meta.IsCollection) continue;
                    if (!bySource.TryGetValue(sid, out var list)) bySource[sid] = list = [];
                    list.Add(n);
                }
            }
            if (bySource.Count == 0) return null;

            var winner = bySource.OrderByDescending(kv => kv.Value.Distinct().Count()).ThenBy(kv => kv.Key).First().Value;
            var all = winner.Distinct().OrderBy(v => v).ToList();
            var (start, end, count) = Cluster(all);
            var contiguous = all.Count > 0 && all[^1] - all[0] + 1 == all.Count;
            return (start, end, count, contiguous);
        }

        /// <summary>The longest run of numbers no more than <see cref="ClusterGap"/> apart.</summary>
        public static (double Start, double End, int Count) Cluster(IReadOnlyList<double> sorted)
        {
            if (sorted.Count == 0) return (0, 0, 0);
            (double Start, double End, int Count) best = (sorted[0], sorted[0], 1);
            var runStart = sorted[0];
            var count = 1;
            var prev = sorted[0];
            for (var i = 1; i < sorted.Count; i++)
            {
                var n = sorted[i];
                if (n - prev <= ClusterGap) count++;
                else
                {
                    if (count > best.Count) best = (runStart, prev, count);
                    runStart = n; count = 1;
                }
                prev = n;
            }
            if (count > best.Count) best = (runStart, prev, count);
            return best;
        }

        private static double? ParseNumber(string? s)
        {
            if (s == null) return null;
            var m = RxNumber.Match(s);
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static string Trim(string s, int n) => (s.Length <= n ? s : s[..n]).PadRight(n);
    }
}
