using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-run-status</c> — every comic run's PUBLICATION status (Ongoing / Completed / Ended / Cancelled) and
    /// how much of it we hold, written to <c>Series.RunStatus</c>, <c>RunPlanned</c>, <c>RunPublished</c>, <c>RunHeld</c>
    /// and <c>RunStatusBasis</c>. DERIVED: re-run after <c>books-resolve --series</c>, a GCD refresh
    /// (<c>books-gcd-series-import</c>), or new containment ranges.
    ///
    /// <para><b>Evidence, strongest first.</b> GCD's <c>is_current</c> (the series is still being published) and its
    /// format text ("limited series", "was ongoing series", "one-shot"…), reached through the identity pass's GCD link
    /// (<c>SeriesKeyLink</c>, Provider = Gcd); the planned length a limited run prints in its own filenames
    /// ("01 (of 06)"); ComicVine's issue count and description ("cancelled"). Without any of it a run whose newest
    /// issue is this or last year is Ongoing by recency, an older one with a known count is Ended, and the rest are
    /// Unknown — the basis column says which rule spoke.</para>
    ///
    /// <para><b>Held</b> counts the run's published issue numbers (ComicVine's issue list when it is complete enough,
    /// else 1..published) that we hold as an issue file or inside a collected edition's range on the shelf (curated,
    /// ComicVine or GCD spans — never LOCG, which over-claims).</para>
    /// </summary>
    public static class RunStatusJob
    {
        public sealed record Evidence(
            bool? GcdCurrent, string? GcdFormat, int? GcdIssueCount, int? GcdYearEnded, string? GcdNotes,
            int? FilesPlanned, int? CvCount, string? CvDescription, int? YearEnd, int? CvStartYear, int? HeldMax = null);

        public sealed record Decision(RunStatus Status, int? Planned, int? Published, string Basis);

        private static readonly Regex Limited = new(@"\b(limited|mini|maxi|finite)[- ]?series\b|\bminiseries\b|\bone[- ]shot\b|\bgraphic novel\b|\bgiveaway\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Collected = new(@"\bcollected (edition|editions|series)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WasOngoing = new(@"\bwas ongoing\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Cancelled = new(@"\b(cancel+ed|cancellation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OfN = new(@"\(\s*of\s*0*(\d{1,3})\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The planned length a filename states — "Zorro 01 (of 03)" → 3.</summary>
        public static int? PlannedFromFileName(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var m = OfN.Match(fileName);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 1 and < 500 ? n : null;
        }

        /// <summary>The rule, pure. <paramref name="year"/> is the current calendar year.</summary>
        public static Decision Decide(Evidence e, int year)
        {
            var published = e.GcdIssueCount is > 0 ? e.GcdIssueCount : e.CvCount is > 0 ? e.CvCount : null;
            // our own files PROVE an issue was published: a provider snapshot older than the run (the GCD dump is a dated
            // copy) must not call a finished mini "cancelled at #1" when we hold #1-3 of 3
            // — bounded, because a year mis-read as an issue number ("Archie 1948") would otherwise invent a 1,948-issue run
            if (e.HeldMax is int hm && hm > (published ?? 0) && (published is int pp0 ? hm <= pp0 + 50 : hm < 1000)) published = hm;
            var hasGcd = e.GcdFormat != null || e.GcdCurrent != null || e.GcdIssueCount != null;
            var limited = e.GcdFormat != null && Limited.IsMatch(e.GcdFormat);
            var planned = e.FilesPlanned;

            // 1. still being published, by GCD's own flag
            if (e.GcdCurrent == true)
                return new Decision(RunStatus.Ongoing, planned, published, "gcd:current");

            // 2. a planned length to measure against (files first — they print it; a GCD limited series is planned at its count)
            if (planned is int p && published is int n)
            {
                if (n >= p) return new Decision(RunStatus.Completed, p, n, $"files:of {p}, {(hasGcd ? "gcd" : "cv")} {n}");
                // short of plan: only a FINISHED record may call it cancelled — a run still coming out is just young
                var finished = hasGcd ? e.GcdCurrent == false : (e.YearEnd is int ye && ye < year - 1);
                if (finished) return new Decision(RunStatus.Cancelled, p, n, $"files:of {p}, {(hasGcd ? "gcd" : "cv")} {n}");
                return new Decision(RunStatus.Ongoing, p, n, $"files:of {p}, {n} out");
            }

            // 3. a source says cancelled
            var text = $"{e.GcdNotes} {e.CvDescription}";
            if (Cancelled.IsMatch(text) && !(e.YearEnd is int y3 && y3 >= year - 1))
                return new Decision(RunStatus.Cancelled, planned, published, hasGcd && e.GcdNotes != null && Cancelled.IsMatch(e.GcdNotes) ? "gcd:cancelled" : "cv:cancelled");

            // 4. GCD's format text on a finished record
            if (hasGcd && e.GcdCurrent == false)
            {
                if (limited) return new Decision(RunStatus.Completed, published, published, $"gcd:limited {published}");
                // a GCD "collected edition" series is a book LINE: one volume is complete in itself; a finished multi-volume line ended
                if (Collected.IsMatch(e.GcdFormat ?? "") && published is <= 1) return new Decision(RunStatus.Completed, published, published, "gcd:single collected volume");
                if (WasOngoing.IsMatch(e.GcdFormat ?? "") || e.GcdYearEnded != null)
                    return new Decision(RunStatus.Ended, planned, published, "gcd:ended");
            }

            // 5. no publisher record says: recency, then age
            if (e.YearEnd is int ye5 && ye5 >= year - 1)
                return new Decision(RunStatus.Ongoing, planned, published, "recency");
            if (published != null && e.YearEnd is int)
                return new Decision(RunStatus.Ended, planned, published, "cv:count, no issue in 2+ years");
            return new Decision(RunStatus.Unknown, planned, published, "no evidence");
        }

        public sealed record Counts(int Series, Dictionary<RunStatus, int> ByStatus, int WithHeld, int CompleteHeld)
        {
            public override string ToString() =>
                $"{{ series: {Series}, {string.Join(", ", ByStatus.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value}"))}, held-known: {WithHeld}, complete-held: {CompleteHeld} }}";
        }

        /// <summary>Compute every comic run, then write in chunks of <paramref name="batchSize"/> series (each its own transaction).</summary>
        public static Counts Run(TargetWriter hot, SqliteConnection? legs, int batchSize, bool apply, Action<string> log)
        {
            var year = DateTime.UtcNow.Year;
            var series = new Dictionary<int, (int? Cv, int? YearEnd)>();
            using (var cmd = hot.CreateCommand($@"SELECT s.Id, s.CvVolumeId, s.YearEnd FROM Series s
WHERE {SeriesResolver.NotBookSql} AND EXISTS (SELECT 1 FROM Item i WHERE i.SeriesId = s.Id AND i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0)"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) series[rd.GetInt32(0)] = (rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetInt32(2));

            // the identity pass's GCD link, by majority over the shelf's own keys
            var gcdOf = new Dictionary<int, int>();
            using (var cmd = hot.CreateCommand($@"SELECT i.SeriesId, l.ProviderKey, count(*) AS n FROM Item i
JOIN ComicDetail d ON d.ItemId = i.Id
JOIN SeriesKeyLink l ON l.ParsedKey = d.ParsedSeriesKey AND l.Provider = {(int)Provider.Gcd} AND l.Status IN {LinkStatuses.UsableSql}
WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL
GROUP BY i.SeriesId, l.ProviderKey ORDER BY i.SeriesId, n DESC"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var sid = rd.GetInt32(0);
                    if (!gcdOf.ContainsKey(sid) && int.TryParse(rd.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)) gcdOf[sid] = g;
                }

            var gcd = new Dictionary<int, (bool? Current, string? Format, int? Count, int? Ended, string? Notes)>();
            if (legs != null)
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT GcdSeriesId, IsCurrent, Format, IssueCount, YearEnded, Notes, TrackingNotes FROM GcdSeries";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    gcd[rd.GetInt32(0)] = (rd.IsDBNull(1) ? null : rd.GetInt64(1) != 0, rd.IsDBNull(2) ? null : rd.GetString(2),
                        rd.IsDBNull(3) ? null : rd.GetInt32(3), rd.IsDBNull(4) ? null : rd.GetInt32(4),
                        $"{(rd.IsDBNull(5) ? "" : rd.GetString(5))} {(rd.IsDBNull(6) ? "" : rd.GetString(6))}".Trim());
            }

            var cv = new Dictionary<int, (int? Count, int? Start, string? Desc)>();
            using (var cmd = hot.CreateCommand("SELECT Id, CountOfIssues, StartYear, coalesce(Deck,'') || ' ' || coalesce(Description,'') FROM CvVolume"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) cv[rd.GetInt32(0)] = (rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetInt32(2), rd.IsDBNull(3) ? null : rd.GetString(3));

            var planned = new Dictionary<int, int>();
            var heldNo = new Dictionary<int, HashSet<double>>();
            using (var cmd = hot.CreateCommand(@"SELECT i.SeriesId, i.FileName, d.IssueNo, coalesce(d.IsCollection,0) FROM Item i
JOIN ComicDetail d ON d.ItemId = i.Id WHERE i.Kind = 0 AND coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var sid = rd.GetInt32(0);
                    if (rd.GetInt64(3) != 0) continue;
                    if (PlannedFromFileName(rd.IsDBNull(1) ? null : rd.GetString(1)) is int p && p > planned.GetValueOrDefault(sid)) planned[sid] = p;
                    if (!rd.IsDBNull(2) && double.TryParse(rd.GetString(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var no))
                        (heldNo.TryGetValue(sid, out var set) ? set : heldNo[sid] = new HashSet<double>()).Add(no);
                }

            var spans = new Dictionary<int, List<(double A, double B)>>();
            using (var cmd = hot.CreateCommand($@"SELECT i.SeriesId, sp.IssueStart, sp.IssueEnd FROM CollectedEditionSpan sp JOIN Item i ON i.Id = sp.ItemId
WHERE coalesce(i.IsExcluded,0) = 0 AND i.SeriesId IS NOT NULL AND sp.IssueStart IS NOT NULL AND sp.IssueEnd IS NOT NULL
  AND sp.Source IN ({(int)EditionSource.Gcd}, {(int)EditionSource.Cv}, {(int)EditionSource.Curated})"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                    (spans.TryGetValue(rd.GetInt32(0), out var l) ? l : spans[rd.GetInt32(0)] = new()).Add((rd.GetDouble(1), rd.GetDouble(2)));

            var cvNumbers = new Dictionary<int, HashSet<double>>();
            using (var cmd = hot.CreateCommand("SELECT VolumeId, IssueNumber FROM CvIssue WHERE IssueNumber IS NOT NULL"))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                    if (double.TryParse(rd.GetString(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                        (cvNumbers.TryGetValue(rd.GetInt32(0), out var s) ? s : cvNumbers[rd.GetInt32(0)] = new()).Add(n);

            var results = new List<(int Sid, Decision D, int? Held)>();
            foreach (var (sid, (cvId, yearEnd)) in series)
            {
                var g = gcdOf.TryGetValue(sid, out var gid) && gcd.TryGetValue(gid, out var gr) ? gr : default;
                var hasG = gcdOf.ContainsKey(sid) && gcd.ContainsKey(gcdOf[sid]);
                var c = cvId is int ci && cv.TryGetValue(ci, out var cr) ? cr : default;
                var e = new Evidence(hasG ? g.Current : null, hasG ? g.Format : null, hasG ? g.Count : null, hasG ? g.Ended : null, hasG ? g.Notes : null,
                    planned.TryGetValue(sid, out var p) ? p : null, c.Count, c.Desc, yearEnd, c.Start,
                    heldNo.TryGetValue(sid, out var hn) && hn.Count > 0 ? (int?)Math.Min(1999, (int)hn.Where(x => x >= 1 && x < 2000 && x == Math.Floor(x)).DefaultIfEmpty(0).Max()) : null);
                var d = Decide(e, year);

                // held: the run's published numbers we have, as a file or inside a collected edition's range
                int? held = null;
                var pub = d.Planned is int pl && d.Status is RunStatus.Completed or RunStatus.Cancelled ? Math.Min(pl, d.Published ?? pl) : d.Published;
                if (pub is int total and > 0 and < 2000)
                {
                    IEnumerable<double> numbers = cvId is int v && cvNumbers.TryGetValue(v, out var cn) && cn.Count >= total * 0.8
                        ? cn : Enumerable.Range(1, total).Select(x => (double)x);
                    var have = heldNo.GetValueOrDefault(sid);
                    var sp = spans.GetValueOrDefault(sid);
                    held = numbers.Count(n => (have != null && have.Contains(n)) || (sp != null && sp.Any(r => r.A <= n && n <= r.B)));
                }
                results.Add((sid, d, held));
            }

            if (apply)
            {
                foreach (var chunk in results.OrderBy(r => r.Sid).Chunk(Math.Max(100, batchSize)))
                {
                    hot.Begin();
                    foreach (var (sid, d, held) in chunk)
                        hot.Exec("UPDATE Series SET RunStatus = $s, RunPlanned = $p, RunPublished = $n, RunHeld = $h, RunStatusBasis = $b WHERE Id = $id",
                            ("$s", (int)d.Status), ("$p", d.Planned), ("$n", d.Published), ("$h", held), ("$b", d.Basis), ("$id", sid));
                    hot.Commit();
                    log($"{{ processed: {chunk.Length}, lastSeriesId: {chunk[^1].Sid}, remaining: {results.Count(r => r.Sid > chunk[^1].Sid)} }}  [run-status]");
                }
            }
            var by = results.GroupBy(r => r.D.Status).ToDictionary(g => g.Key, g => g.Count());
            return new Counts(results.Count, by, results.Count(r => r.Held != null),
                results.Count(r => r.Held is int h && h > 0 && h >= (r.D.Status is RunStatus.Completed or RunStatus.Cancelled && r.D.Planned is int pp ? Math.Min(pp, r.D.Published ?? pp) : r.D.Published ?? int.MaxValue)));
        }
    }
}
