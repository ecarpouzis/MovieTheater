using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-locg-editions</c> — re-point a collected edition's `ItemProviderLink(Locg)` at the LOCG record
    /// that IS that edition, so the existing `books-collected-editions` reduction can give it a real span.
    ///
    /// <para><b>The fault.</b> The offline LOCG map matched books to LOCG comics by issue number, so a 505-page
    /// "Saga Book 1" points at LOCG's "Saga #1" — a 28-page floppy with zero containment edges. The reduction
    /// then has nothing to reduce and the edition stays spanless (or, worse, the wrong provider wins).</para>
    ///
    /// <para><b>The repair.</b> Two candidate sources, both containers by construction or by test:
    /// (1) LOCG's own series page lists every EDITION of a run and the scraper cached it as
    /// <c>rich/&lt;locgSeriesId&gt;.json</c> (id, slug, title) — the series is found by NAME against legs
    /// `LocgSeries` (`LocgComicRaw.LocgSeriesId` is populated on 53 of 156,839 rows and is useless);
    /// (2) whatever already CONTAINS the issue the book is wrongly linked to — the containers of "Saga #1" are
    /// exactly the editions that collect it, including "Saga Vol. 1 TP". The union is filtered to records with
    /// at least one `LocgContainment` edge and matched to the book by TITLE.</para>
    ///
    /// <para>A book whose current link is already a container is left alone; a book with no confident edition
    /// title match is left alone. Nothing is ever pointed at a record with no containment.</para>
    ///
    /// <para>Chunked by `Series.Id`, dry-run by default. Legs is opened READ-ONLY: the edges and the raw rows are
    /// warehouse facts, and the only thing this job writes is the hot link.</para>
    /// </summary>
    public static class LocgEditionRelinkJob
    {
        public const string CursorKey = "books:recompute:locg-editions";
        public const string Method = "edition-title";

        public sealed record BatchResult(
            int Processed, long Remaining, long? NextCursor, int Relinked, int AlreadyContainer, int NoCandidates, int NoMatch)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", relinked: {Relinked}, "
                + $"alreadyContainer: {AlreadyContainer}, noCandidates: {NoCandidates}, noMatch: {NoMatch} }}  [locg-editions]";
        }

        /// <summary>LOCG series by normalized name — the only usable bridge from a MovieTheater series to a
        /// cached LOCG series page. Built once per run from legs `LocgSeries` (8,376 rows).</summary>
        public static Dictionary<string, List<(long Id, string Name, int IssueCount)>> BuildSeriesIndex(SqliteConnection legs)
        {
            var index = new Dictionary<string, List<(long, string, int)>>(StringComparer.Ordinal);
            using var cmd = legs.CreateCommand();
            cmd.CommandText = "SELECT LocgSeriesId, coalesce(Name,''), coalesce(IssueCount,0) FROM LocgSeries";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var name = rd.GetString(1);
                var key = EditionMatcher.Norm(name);
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                list.Add((rd.GetInt64(0), name, rd.GetInt32(2)));
            }
            return index;
        }

        /// <summary>One entry of a cached LOCG series page.</summary>
        public readonly record struct RichEdition(long Id, string Slug, string Title);

        /// <summary>Parse a cached <c>rich/&lt;seriesId&gt;.json</c>. Only entries flagged `isEdition` count.</summary>
        public static List<RichEdition> ParseRich(string json)
        {
            var list = new List<RichEdition>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (e.TryGetProperty("isEdition", out var isEd) && isEd.ValueKind == JsonValueKind.False) continue;
                    if (!e.TryGetProperty("id", out var idProp)) continue;
                    var raw = idProp.ValueKind == JsonValueKind.String ? idProp.GetString()
                        : idProp.ValueKind == JsonValueKind.Number ? idProp.GetRawText() : null;
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
                    var slug = e.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    var title = e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    // The slug carries the edition words even when the title is bare, so it is the fallback.
                    if (title.Trim().Length == 0) title = slug.Replace('-', ' ');
                    if (title.Trim().Length == 0) continue;
                    list.Add(new RichEdition(id, slug, title));
                }
            }
            catch (JsonException) { return []; }
            return list;
        }

        public static BatchResult RunBatch(TargetWriter hot, SqliteConnection legs, string richDir,
            Dictionary<string, List<(long Id, string Name, int IssueCount)>> seriesIndex,
            long afterSeriesId, int batchSize, Action<string>? sample = null)
        {
            batchSize = Math.Clamp(batchSize, 1, 5_000);
            var seriesIds = ProducerSupport.SeriesPage(hot, afterSeriesId, batchSize);
            if (seriesIds.Count == 0) return new BatchResult(0, 0, null, 0, 0, 0, 0);

            var idList = string.Join(",", seriesIds);
            var books = ProducerSupport.LoadCollectionBooks(hot, idList);
            if (books.Count == 0)
            {
                var skipTo = seriesIds[^1];
                return new BatchResult(seriesIds.Count, hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {skipTo}"),
                    skipTo, 0, 0, 0, 0);
            }
            var names = ProducerSupport.LoadSeriesNames(hot, idList);

            // Every LOCG link the page's series hold: the item's current record, and the votes that decide which
            // LOCG series the run sits on.
            var linkOf = new Dictionary<int, long>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id, i.SeriesId || char(31) || l.ProviderKey
FROM Item i JOIN ItemProviderLink l ON l.ItemId = i.Id
WHERE l.Provider = {(int)Provider.Locg} AND l.Status = {(int)LinkStatus.Matched} AND l.ProviderKey IS NOT NULL
  AND i.SeriesId IN ({idList})"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var locgId)) continue;
                linkOf[(int)itemId] = locgId;
            }

            var containers = ContainerIds(legs, linkOf.Values.Distinct().ToList());
            // Whatever already CONTAINS an item's current (wrong) link is, by construction, an edition that
            // collects it: the containers of "Saga #1" include "Saga Vol. 1 TP".
            var containersOf = ContainersOf(legs, linkOf.Values.Distinct().ToList());

            int relinked = 0, already = 0, noCandidates = 0, noMatch = 0;
            foreach (var seriesId in seriesIds)
            {
                if (!books.TryGetValue(seriesId, out var list) || list.Count == 0) continue;
                var seriesName = names.GetValueOrDefault(seriesId, "");
                var seriesNorm = EditionMatcher.Norm(seriesName);

                // (1) every edition on the cached LOCG series page, when the name resolves to one.
                var byId = new Dictionary<long, (string Title, string Slug)>();
                if (seriesIndex.TryGetValue(seriesNorm, out var locgSeries))
                    foreach (var candidate in locgSeries.OrderByDescending(s => s.IssueCount).Take(2))
                    {
                        var path = Path.Combine(richDir, candidate.Id.ToString(CultureInfo.InvariantCulture) + ".json");
                        if (!File.Exists(path)) continue;
                        List<RichEdition> editions;
                        try { editions = ParseRich(File.ReadAllText(path)); }
                        catch (IOException) { continue; }
                        foreach (var e in editions) byId.TryAdd(e.Id, (e.Title, e.Slug));
                    }
                var fromRich = ContainerIds(legs, byId.Keys.ToList());
                foreach (var id in byId.Keys.Where(k => !fromRich.Contains(k)).ToList()) byId.Remove(id);

                // (2) the containers of each book's current link.
                var extra = new List<long>();
                foreach (var book in list)
                    if (linkOf.TryGetValue(book.ItemId, out var cur) && containersOf.TryGetValue(cur, out var owners))
                        extra.AddRange(owners.Where(o => !byId.ContainsKey(o)));
                foreach (var (id, title, slug) in LocgTitles(legs, extra.Distinct().ToList()))
                    byId.TryAdd(id, (title, slug));

                if (byId.Count == 0) { noCandidates += list.Count; continue; }
                var candidates = byId.Select(kv => (Key: kv.Key, Title: kv.Value.Title)).ToList();

                var claims = new List<(CollectionBook Book, long Key, string Title, double Confidence)>();
                foreach (var book in list)
                {
                    if (linkOf.TryGetValue(book.ItemId, out var current) && containers.Contains(current)) { already++; continue; }
                    if (EditionMatcher.MatchTitleOnly(book, candidates, seriesNorm) is not { } m) { noMatch++; continue; }
                    if (linkOf.TryGetValue(book.ItemId, out var was) && was == m.Key) { already++; continue; }
                    claims.Add((book, m.Key, m.Title, m.Confidence));
                }

                var kept = ProducerSupport.ResolveContention(claims, c => c.Key, c => c.Confidence);
                noMatch += claims.Count - kept.Count;
                foreach (var (book, key, title, confidence) in kept)
                {
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = book.ItemId,
                        Provider = Provider.Locg,
                        ProviderKey = key.ToString(CultureInfo.InvariantCulture),
                        Status = LinkStatus.Matched,
                        Method = LocgEditionRelinkJob.Method,
                        MatchedKey = byId[key].Slug,
                        Confidence = confidence,
                        Quality = LinkQuality.High,
                        Applied = true,
                    });
                    relinked++;
                    sample?.Invoke($"  s{seriesId,-6} i{book.ItemId,-6} {Trim(book.FileName, 52)} -> locg {key} {Trim(title, 40)} @{confidence:0.00}");
                }
            }

            var next = seriesIds[^1];
            return new BatchResult(seriesIds.Count, hot.Scalar<long>($"SELECT count(*) FROM Series WHERE Id > {next}"),
                next, relinked, already, noCandidates, noMatch);
        }

        public static (int Relinked, int AlreadyContainer, int NoCandidates, int NoMatch) RunAll(
            TargetWriter hot, string legsPath, string richDir, int batchSize, Action<string> log, bool resume = false, int sampleTop = 25)
        {
            using var legs = LegsTagFoldJob.OpenLegs(legsPath);
            var seriesIndex = BuildSeriesIndex(legs);
            log($"locg series index: {seriesIndex.Count} distinct names");
            long cursor = resume ? JobCursor.Read(hot, CursorKey) : 0;
            int relinked = 0, already = 0, noCandidates = 0, noMatch = 0, printed = 0;
            while (true)
            {
                hot.Begin();
                var r = RunBatch(hot, legs, richDir, seriesIndex, cursor, batchSize, s => { if (printed++ < sampleTop) log(s); });
                if (r.NextCursor is long next) JobCursor.Write(hot, CursorKey, next);
                hot.Commit();
                relinked += r.Relinked; already += r.AlreadyContainer; noCandidates += r.NoCandidates; noMatch += r.NoMatch;
                if (r.Done) break;
                log(r.ToString());
                cursor = r.NextCursor!.Value;
            }
            hot.Begin();
            JobCursor.Clear(hot, CursorKey);
            hot.Commit();
            return (relinked, already, noCandidates, noMatch);
        }

        /// <summary>Which of these LOCG comics are containers — at least one containment edge of their own.</summary>
        private static HashSet<long> ContainerIds(SqliteConnection legs, List<long> ids)
        {
            var set = new HashSet<long>();
            foreach (var chunk in ProducerSupport.Chunk(ids, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT ContainerLocgComicId FROM LocgContainment WHERE ContainerLocgComicId IN ("
                                + ProducerSupport.Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) set.Add(rd.GetInt64(0));
            }
            return set;
        }

        /// <summary>For each of these LOCG comics, the comics that CONTAIN it (the reverse edge).</summary>
        private static Dictionary<long, List<long>> ContainersOf(SqliteConnection legs, List<long> ids)
        {
            var map = new Dictionary<long, List<long>>();
            foreach (var chunk in ProducerSupport.Chunk(ids, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT ContainedLocgComicId, ContainerLocgComicId FROM LocgContainment WHERE ContainedLocgComicId IN ("
                                + ProducerSupport.Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var contained = rd.GetInt64(0);
                    if (!map.TryGetValue(contained, out var list)) map[contained] = list = [];
                    list.Add(rd.GetInt64(1));
                }
            }
            return map;
        }

        /// <summary>The warehouse's own title (and a slug built from it) for these LOCG comics.</summary>
        private static List<(long Id, string Title, string Slug)> LocgTitles(SqliteConnection legs, List<long> ids)
        {
            var rows = new List<(long, string, string)>();
            foreach (var chunk in ProducerSupport.Chunk(ids, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT LocgComicId, coalesce(Title,'') FROM LocgComicRaw WHERE LocgComicId IN ("
                                + ProducerSupport.Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var title = rd.GetString(1);
                    if (title.Trim().Length == 0) continue;
                    rows.Add((rd.GetInt64(0), title, EditionMatcher.Norm(title).Replace(' ', '-')));
                }
            }
            return rows;
        }

        private static string Trim(string s, int n) => (s.Length <= n ? s : s[..n]).PadRight(n);
    }
}
