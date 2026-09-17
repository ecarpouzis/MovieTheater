using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The run refs of a Curated span (<c>CollectedEditionSpanRun</c>) across a rewrite of that span.
    ///
    /// <para>A span says "#1-4"; its run refs say #1-4 OF WHAT — the ComicVine volume, the GCD series, the
    /// MangaUpdates series a reader named on the identity pass's `C` line. They hang off the span by a
    /// CASCADing foreign key, which is what stops a retracted range from leaving a ref behind, and is also
    /// what makes any DELETE of the span take a person's reading with it. <c>books-curated-spans-import</c>
    /// deletes the Curated row on the retraction path, so it reads the refs first and puts them back.</para>
    ///
    /// <para>It lives here rather than inside the command so that the thing the wave depends on is testable
    /// without the host: the test does what the verb does, against a real file, and the cascade is real.</para>
    /// </summary>
    public static class CuratedSpanRuns
    {
        /// <param name="Start">The range in THIS run's numbering; null = the span's own range.</param>
        public readonly record struct Ref(Provider Provider, string Key, double? Start, double? End, double? Confidence);

        /// <summary>Every Curated run ref of the given items, by item. Absent from the map = the item has none.</summary>
        public static Dictionary<int, List<Ref>> Read(TargetWriter hot, string idList)
        {
            var runs = new Dictionary<int, List<Ref>>();
            // The ranges ride along in the same packed string; a ProviderKey never contains a ':' (it is a
            // provider id or a Disney series code), so the split stays unambiguous with a fixed field count.
            foreach (var (id, payload) in hot.Pairs(
                $@"SELECT ItemId, group_concat(Provider || ':' || ProviderKey || ':' || coalesce(Confidence,'')
                                               || ':' || coalesce(IssueStart,'') || ':' || coalesce(IssueEnd,''),
                                               char(31))
                   FROM CollectedEditionSpanRun
                   WHERE Source = {(int)EditionSource.Curated} AND ItemId IN ({idList})
                   GROUP BY ItemId"))
            {
                var list = new List<Ref>();
                foreach (var part in (payload ?? "").Split(TargetWriter.Sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = part.Split(':', 5);
                    if (bits.Length < 2) continue;
                    list.Add(new Ref((Provider)int.Parse(bits[0], CultureInfo.InvariantCulture), bits[1],
                        Dbl(bits, 3), Dbl(bits, 4), Dbl(bits, 2)));
                }
                if (list.Count > 0) runs[(int)id] = list;
            }
            return runs;
        }

        private static double? Dbl(string[] bits, int at) =>
            bits.Length > at && bits[at].Length > 0
                ? double.Parse(bits[at], CultureInfo.InvariantCulture) : null;

        /// <summary>Put one item's refs back onto its (re-written) Curated span. Returns how many there were.</summary>
        public static int Reattach(TargetWriter hot, int itemId, Dictionary<int, List<Ref>> runs, bool apply)
        {
            if (!runs.TryGetValue(itemId, out var list)) return 0;
            if (!apply) return list.Count;
            foreach (var r in list)
                hot.Upsert("CollectedEditionSpanRun", new
                {
                    ItemId = itemId,
                    Source = EditionSource.Curated,
                    Provider = r.Provider,
                    ProviderKey = r.Key,
                    IssueStart = r.Start,
                    IssueEnd = r.End,
                    Confidence = r.Confidence,
                    CreatedAt = DateTime.UtcNow,
                });
            return list.Count;
        }
    }
}
