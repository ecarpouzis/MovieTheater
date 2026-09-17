using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Projections
{
    /// <summary>One leg's name for the RUN a collected edition's range counts in.</summary>
    /// <param name="Provider">The leg the run is named on (Cv volume, Gcd series, Mu series, Barney, Marvel, Inducks).</param>
    /// <param name="Key">The run's id ON that leg, exactly as the reader typed it.</param>
    /// <param name="Name">The run's title when we hold the record; null when we only hold the id.</param>
    /// <param name="IssueStart">The range in THIS run's numbering; null = the span's own range. A book that
    /// collects two minis carries a row per mini, each with its own #a-b.</param>
    public sealed record SpanRunRef(Provider Provider, string Key, string? Name,
        double? IssueStart = null, double? IssueEnd = null);

    /// <summary>
    /// Reads <c>CollectedEditionSpanRun</c> for a page of items and puts a NAME on each id where one can be
    /// had, so a reader sees "collects #1-5 of Wake the Devil" rather than "#1-5" on a shelf where every book
    /// collects a different mini.
    ///
    /// <para><b>Where the names come from.</b> ComicVine volumes and MangaUpdates series are in the hot file,
    /// so they resolve from the same <see cref="BooksDb"/> the caller already has. GCD series names are in
    /// <c>books-legs.db</c>, which no request-path context opens — a caller that HAS the legs path (the admin
    /// screens do; the item modal does not) passes it and gets those names too. Barney, Marvel and Inducks
    /// have no local catalog at all: their ids stand alone, which is exactly what the decision grammar says
    /// about them.</para>
    /// </summary>
    public static class SpanRunRefs
    {
        public static async Task<Dictionary<(int ItemId, EditionSource Source), List<SpanRunRef>>> LoadAsync(
            BooksDb db, IReadOnlyCollection<int> itemIds, string? legsDbPath = null, CancellationToken ct = default)
        {
            var byspan = new Dictionary<(int, EditionSource), List<SpanRunRef>>();
            if (itemIds.Count == 0) return byspan;

            var rows = await db.CollectedEditionSpanRuns.AsNoTracking()
                .Where(r => itemIds.Contains(r.ItemId))
                .Select(r => new { r.ItemId, r.Source, r.Provider, r.ProviderKey, r.IssueStart, r.IssueEnd })
                .ToListAsync(ct);
            if (rows.Count == 0) return byspan;

            var cvIds = rows.Where(r => r.Provider == Provider.Cv).Select(r => Int(r.ProviderKey))
                .Where(v => v != null).Select(v => v!.Value).Distinct().ToList();
            var muIds = rows.Where(r => r.Provider == Provider.Mu).Select(r => Long(r.ProviderKey))
                .Where(v => v != null).Select(v => v!.Value).Distinct().ToList();

            var cvNames = cvIds.Count == 0
                ? new Dictionary<int, string?>()
                : await db.CvVolumes.AsNoTracking().Where(v => cvIds.Contains(v.Id))
                    .ToDictionaryAsync(v => v.Id, v => v.Name, ct);
            var muNames = muIds.Count == 0
                ? new Dictionary<long, string?>()
                : await db.MuSeries.AsNoTracking().Where(s => muIds.Contains(s.Id))
                    .ToDictionaryAsync(s => s.Id, s => s.Title, ct);
            var gcdNames = GcdNames(legsDbPath,
                rows.Where(r => r.Provider == Provider.Gcd).Select(r => r.ProviderKey).Distinct().ToList());

            foreach (var r in rows.OrderBy(r => r.Provider).ThenBy(r => r.IssueStart).ThenBy(r => r.ProviderKey, StringComparer.Ordinal))
            {
                string? name = null;
                if (r.Provider == Provider.Cv && Int(r.ProviderKey) is { } cv) cvNames.TryGetValue(cv, out name);
                else if (r.Provider == Provider.Mu && Long(r.ProviderKey) is { } mu) muNames.TryGetValue(mu, out name);
                else if (r.Provider == Provider.Gcd) gcdNames.TryGetValue(r.ProviderKey, out name);
                if (!byspan.TryGetValue((r.ItemId, r.Source), out var list))
                    byspan[(r.ItemId, r.Source)] = list = new List<SpanRunRef>();
                list.Add(new SpanRunRef(r.Provider, r.ProviderKey, name, r.IssueStart, r.IssueEnd));
            }
            return byspan;
        }

        /// <summary>The Curated span's refs for one item — the shape the item modal wants.</summary>
        public static List<SpanRunRef> For(
            Dictionary<(int ItemId, EditionSource Source), List<SpanRunRef>> byspan, int itemId, EditionSource source) =>
            byspan.TryGetValue((itemId, source), out var list) ? list : [];

        private static Dictionary<string, string?> GcdNames(string? legsDbPath, List<string> keys)
        {
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (keys.Count == 0 || string.IsNullOrWhiteSpace(legsDbPath) || !File.Exists(legsDbPath)) return names;
            try
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = legsDbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
                }.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                var ids = keys.Select(Int).Where(v => v != null).Select(v => v!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
                if (ids.Count == 0) return names;
                cmd.CommandText = $"SELECT GcdSeriesId, Name FROM GcdSeries WHERE GcdSeriesId IN ({string.Join(",", ids)})";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    names[r.GetInt64(0).ToString(CultureInfo.InvariantCulture)] = r.IsDBNull(1) ? null : r.GetString(1);
            }
            catch (SqliteException)
            {
                // A missing or locked warehouse costs the NAMES, never the ids — the payload still ships.
            }
            return names;
        }

        private static int? Int(string? s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static long? Long(string? s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
