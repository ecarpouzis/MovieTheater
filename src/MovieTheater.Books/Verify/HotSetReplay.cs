using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Verify
{
    /// <summary>
    /// The R4 proof for the v2 model: the standalone site's hot query set (the facets, the two-phase group
    /// heads/bands, the catalog sorts, the Home rails, the Kids browse — everything CacheWarmupService warmed)
    /// re-expressed as EF queries over v2, each timed twice (cold, warm) and its plan read back with
    /// EXPLAIN QUERY PLAN. A query that full-scans a large table or sorts through a TEMP B-TREE is FLAGGED —
    /// that is precisely what the v1 census found (21 GROUP BY full scans behind the 48 h facet cache).
    /// </summary>
    public sealed class HotSetReplay
    {
        public sealed record Row(string Name, long ColdMs, long WarmMs, int Rows, string Plan, IReadOnlyList<string> Flags);

        private readonly string dbPath;
        private readonly int largeTableRows;

        public HotSetReplay(string dbPath, int largeTableRows = 50_000) { this.dbPath = dbPath; this.largeTableRows = largeTableRows; }

        /// <summary>The query set. Names mirror the v1 endpoints so the census timings line up beside them.</summary>
        public IEnumerable<(string Name, Func<BooksDb, IQueryable> Query)> Queries()
        {
            IQueryable<Item> live(BooksDb db) => db.Items.Where(i => i.Kind == ItemKind.Comic && !i.IsExcluded);
            // facets (BrowseFacetsController.GetFacets)
            yield return ("facets/series", db => live(db).Where(i => i.SeriesId != null).GroupBy(i => i.SeriesId).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(500));
            yield return ("facets/publishers", db => live(db).Where(i => i.ResolvedPublisher != null).GroupBy(i => i.ResolvedPublisher).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(200));
            yield return ("facets/decades", db => live(db).Where(i => i.ResolvedYear != null).GroupBy(i => i.ResolvedYear / 10).Select(g => new { g.Key, n = g.Count() }).OrderBy(x => x.Key));
            yield return ("facets/events", db => db.ComicDetails.Where(d => d.EventName != null).GroupBy(d => d.EventName).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(200));
            yield return ("facets/franchises", db => db.Series.Where(s => s.Franchise != null).GroupBy(s => s.Franchise).Select(g => new { g.Key, n = g.Sum(s => s.IssueCount) }).OrderByDescending(x => x.n).Take(200));
            yield return ("facets/collections", db => live(db).Where(i => i.TopFolderId != null).GroupBy(i => i.TopFolderId).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(200));
            yield return ("facets/authors", db => db.ItemCredits.Where(c => c.Role == "Writer" || c.Role == "Author").GroupBy(c => c.NormalizedName).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(300));
            yield return ("facets/artists", db => db.ItemCredits.Where(c => c.Role == "Penciller" || c.Role == "Cover Artist").GroupBy(c => c.NormalizedName).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(300));
            yield return ("facets/tags", db => db.ItemTags.Where(t => t.Category == "tag" || t.Category == "genre").GroupBy(t => t.Value).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(300));
            yield return ("facets/series-tags", db => db.SeriesTags.Where(t => t.Category == "tag").GroupBy(t => t.Value).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(300));
            // group heads (BrowseGroupsController.GetGroups) per groupBy
            yield return ("groups/series heads", db => db.Series.Where(s => s.IssueCount > 0).OrderBy(s => s.Name).ThenBy(s => s.Id).Select(s => new { s.Id, s.Name, s.IssueCount }).Take(60));
            yield return ("groups/series heads (top rated)", db => db.Series.Where(s => s.IssueCount > 0 && s.ResolvedRating != null).OrderByDescending(s => s.ResolvedRating).ThenBy(s => s.Id).Take(60));
            yield return ("groups/publisher heads", db => live(db).GroupBy(i => i.ResolvedPublisher).Select(g => new { g.Key, n = g.Count(), first = g.Min(i => i.Id) }).OrderBy(x => x.Key).Take(60));
            yield return ("groups/decade heads", db => live(db).GroupBy(i => i.ResolvedYear / 10).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.Key).Take(30));
            yield return ("groups/letters", db => db.Series.Where(s => s.IssueCount > 0 && s.Name != null).GroupBy(s => s.Name!.Substring(0, 1)).Select(g => new { g.Key, n = g.Count() }).OrderBy(x => x.Key));
            // band items (BandItemsAsync): the issues of one series in reading order, then the next 8 series
            // a band is walked FROM the reading order (its (SeriesId, ReadIndex) index is the order) and joins the items in
            // (the series id is a bound parameter: comparing a nullable column to a nullable subquery renders as
            //  "= x OR (IS NULL AND x IS NULL)", a MULTI-INDEX OR — the shape every band/modal query must avoid)
            yield return ("band/series items", db =>
            {
                var sid = db.Series.Where(s => s.IssueCount > 20).OrderBy(s => s.Id).Select(s => s.Id).FirstOrDefault();
                return db.ReadingOrderEntries.Where(r => r.SeriesId == sid).OrderBy(r => r.ReadIndex).ThenBy(r => r.ItemId)
                    .Join(db.Items.Where(i => !i.IsExcluded), r => r.ItemId, i => i.Id, (r, i) => new { i.Id, i.ResolvedTitle, i.CoverAspect, r.ReadIndex }).Take(40);
            });
            yield return ("band/first 8 series (by series id)", db => db.Items.Where(i => i.SeriesId != null && !i.IsExcluded).OrderBy(i => i.SeriesId).ThenBy(i => i.Id).Select(i => new { i.Id, i.SeriesId, i.ResolvedTitle }).Take(320));
            // catalog (CatalogController: ApplySortQuery's sorts, default + the rest)
            yield return ("catalog/default (series, id)", db => live(db).OrderBy(i => i.ResolvedSeries).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle, i.ResolvedSeries, i.CoverAspect }).Take(120));
            yield return ("catalog/newest (year desc, indexed desc)", db => live(db).OrderByDescending(i => i.ResolvedYear).ThenByDescending(i => i.IndexedAt).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle }).Take(120));
            yield return ("catalog/top rated", db => live(db).Where(i => i.ResolvedRating != null).OrderByDescending(i => i.ResolvedRating).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle }).Take(120));
            yield return ("catalog/recently added", db => live(db).OrderByDescending(i => i.IndexedAt).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle }).Take(120));
            yield return ("catalog/title", db => live(db).OrderBy(i => i.NormalizedTitle).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle }).Take(120));
            yield return ("catalog/filter publisher + series sort", db => live(db).Where(i => i.ResolvedPublisher == "Marvel").OrderBy(i => i.ResolvedSeries).ThenBy(i => i.Id).Select(i => new { i.Id }).Take(120));
            yield return ("catalog/filter series page 2", db => live(db).Where(i => i.SeriesId == 1).OrderBy(i => i.Id).Skip(120).Take(120).Select(i => new { i.Id }));
            yield return ("catalog/count with filter", db => live(db).Where(i => i.ResolvedYear >= 2010 && i.ResolvedYear < 2020).Select(i => i.Id));
            yield return ("catalog/tag filter (exists)", db => live(db).Where(i => db.ItemTags.Any(t => t.ItemId == i.Id && t.Value == "Superhero")).OrderBy(i => i.ResolvedSeries).ThenBy(i => i.Id).Select(i => i.Id).Take(120));
            // home rails (ComicsController.GetHome)
            yield return ("home/highest rated series", db => db.Series.Where(s => s.ResolvedRating >= 60 && s.IssueCount >= 3).OrderByDescending(s => s.ResolvedRating).ThenBy(s => s.Id).Take(24));
            yield return ("home/big collected editions", db => db.CollectionNodes.Where(n => n.ContainsCount >= 6).Join(db.Items, n => n.ItemId, i => i.Id, (n, i) => new { i.Id, i.ResolvedTitle, n.ContainsCount, i.ResolvedRating }).Where(x => x.ResolvedRating >= 60).OrderByDescending(x => x.ContainsCount).Take(24));
            yield return ("home/fresh arrivals", db => live(db).OrderByDescending(i => i.IndexedAt).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle, i.SeriesId }).Take(48));
            yield return ("home/top shelf reads (user)", db => db.UserItemStates.Where(u => u.UserId == 1 && u.Status == ReadStatus.Finished).OrderByDescending(u => u.UpdatedAt).Join(db.Items, u => u.ItemId, i => i.Id, (u, i) => new { i.Id, i.SeriesId, u.UpdatedAt }).Take(48));
            yield return ("home/continue reading (user)", db => db.UserItemStates.Where(u => u.UserId == 1 && u.Status == ReadStatus.InProgress).OrderByDescending(u => u.UpdatedAt).Take(24));
            // kids (KidsController.Browse): series whose current insight is allow-listed + maturity 0, PerSeries 40 / MaxSeries 160
            yield return ("kids/series allow-list", db => db.SeriesTags.Where(t => t.Category == "audience" && t.Source == TagSource.AI && db.KidSafeTags.Any(k => k.Category == "audience" && k.Tag == t.Value))
                .Select(t => t.SeriesId).Distinct().Take(160));
            yield return ("kids/books maturity 0", db => db.Insights.Where(n => n.SubjectKind == SubjectKind.Item && n.IsCurrent && n.Maturity == 0).OrderBy(n => n.SubjectId).Select(n => n.SubjectId).Take(160));
            // novels (BooksController): facets by author/decade/tag over books
            yield return ("novels/authors", db => db.ItemCredits.Where(c => c.Source == TagSource.Calibre).GroupBy(c => c.NormalizedName).Select(g => new { g.Key, n = g.Count() }).OrderByDescending(x => x.n).Take(300));
            yield return ("novels/rated books", db => db.Items.Where(i => i.Kind == ItemKind.Book && !i.IsExcluded && i.ResolvedRating != null).OrderByDescending(i => i.ResolvedRating).ThenBy(i => i.Id).Select(i => new { i.Id, i.ResolvedTitle }).Take(120));
            // fts
            yield return ("fts/search join", db => ItemFts.Search(db, "batman", 200));
            // suggestions/bookshelf: series the user marked
            yield return ("bookshelf/marked series", db => db.GroupMarks.Where(g => g.UserId == 1 && g.GroupType == GroupType.Series && (g.IsRead || g.WantToRead)).Take(200));
        }

        public List<Row> Run(Action<string>? log = null)
        {
            var rows = new List<Row>();
            using var plain = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            plain.Open();
            var large = LargeTables(plain);
            foreach (var (name, build) in Queries())
            {
                string sql; long cold, warm; int count;
                try
                {
                    using (var db = new BooksDb(BooksDbOptions.Hot(dbPath, readOnly: true)))
                    {
                        var q = build(db);
                        sql = q is IQueryable<int> qi ? "-- raw --" : q.ToQueryString();
                        var sw = Stopwatch.StartNew(); count = Materialize(q); cold = sw.ElapsedMilliseconds;
                        sw.Restart(); Materialize(q); warm = sw.ElapsedMilliseconds;
                    }
                }
                catch (Exception e)
                {
                    rows.Add(new Row(name, -1, -1, 0, e.GetType().Name + ": " + e.Message, new[] { "ERROR" }));
                    log?.Invoke($"{name}: ERROR {e.Message}");
                    continue;
                }
                var plan = sql == "-- raw --" ? "(raw SQL: FTS MATCH)" : Explain(plain, sql);
                var flags = Flag(plan, large, isAggregate: sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
                rows.Add(new Row(name, cold, warm, count, plan, flags));
                log?.Invoke($"{name}: cold {cold} ms, warm {warm} ms, {count} rows" + (flags.Count > 0 ? "  FLAGS: " + string.Join(", ", flags) : ""));
            }
            return rows;
        }

        private static int Materialize(IQueryable q)
        {
            var n = 0;
            foreach (var _ in q) n++;
            return n;
        }

        private static string Explain(SqliteConnection conn, string sql)
        {
            // ToQueryString() prints parameters as ".param set" preamble lines; strip them and bind defaults
            var body = string.Join("\n", sql.Split('\n').Where(l => !l.StartsWith(".param", StringComparison.Ordinal)));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + body;
            var lines = new List<string>();
            try
            {
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) lines.Add(rd.GetString(3));
            }
            catch (SqliteException e) { lines.Add("EXPLAIN failed: " + e.Message); }
            return string.Join("\n", lines);
        }

        private Dictionary<string, long> LargeTables(SqliteConnection conn)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'ItemFts%'";
            var names = new List<string>();
            using (var rd = cmd.ExecuteReader()) while (rd.Read()) names.Add(rd.GetString(0));
            foreach (var n in names)
            {
                using var c = conn.CreateCommand();
                c.CommandText = $"SELECT count(*) FROM \"{n}\"";
                var rows = (long)c.ExecuteScalar()!;
                if (rows >= largeTableRows) d[n] = rows;
            }
            return d;
        }

        private static List<string> Flag(string plan, Dictionary<string, long> large, bool isAggregate)
        {
            var flags = new List<string>();
            foreach (var line in plan.Split('\n'))
            {
                // ORDER BY count(*) over a few thousand groups is a sort of the AGGREGATE rows — inherent and cheap;
                // a temp sort of a non-aggregate browse page means the ORDER BY missed its index
                if (line.Contains("USE TEMP B-TREE", StringComparison.Ordinal) && !isAggregate) flags.Add("TEMP B-TREE");
                var m = System.Text.RegularExpressions.Regex.Match(line, @"SCAN (?:TABLE )?""?(\w+)""?");
                if (m.Success && large.ContainsKey(m.Groups[1].Value) && !line.Contains("USING COVERING INDEX", StringComparison.Ordinal) && !line.Contains("USING INDEX", StringComparison.Ordinal))
                    flags.Add("SCAN " + m.Groups[1].Value);
            }
            return flags.Distinct().ToList();
        }

        public static string Render(IReadOnlyList<Row> rows, string title)
        {
            var L = new List<string> { "# " + title, "", "| Query | cold ms | warm ms | rows | flags |", "|---|---:|---:|---:|---|" };
            foreach (var r in rows) L.Add($"| {r.Name} | {r.ColdMs} | {r.WarmMs} | {r.Rows} | {string.Join(", ", r.Flags)} |");
            L.Add(""); L.Add("## Plans"); L.Add("");
            foreach (var r in rows) { L.Add("### " + r.Name); L.Add("```"); L.Add(r.Plan); L.Add("```"); L.Add(""); }
            return string.Join("\n", L) + "\n";
        }
    }
}
