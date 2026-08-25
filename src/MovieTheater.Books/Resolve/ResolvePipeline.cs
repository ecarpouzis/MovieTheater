using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-resolve</c> — rebuilds every DERIVED column the browse reads, in dependency order:
    /// insight currency → AI tag fold → series scalars → item scalars (chunked) → registry stamps. The FTS
    /// rebuild is its own stage (<see cref="FtsBuilder"/>). Cursor protocol (shared with the migration's
    /// resolve stage): 0..3 are the single-pass steps; <see cref="ItemsBase"/>+id is "items after id".
    /// </summary>
    public static class ResolvePipeline
    {
        public const long ItemsBase = 1_000;

        public static bool RunStep(TargetWriter hot, long cursor, int batchSize, Action<string> log, UnitCounts counts, out long nextCursor)
        {
            switch (cursor)
            {
                case 0:
                {
                    var (current, total) = InsightCurrency.Rebuild(hot);
                    log($"resolve: insight currency — {current} current of {total}");
                    counts.Bump("insights-current", (int)current);
                    nextCursor = 1; return false;
                }
                case 1:
                {
                    var (raw, folded, kept) = TagFolds.RebuildAiFold(hot);
                    log($"resolve: AI tag fold — {raw} raw tag rows, {folded} folded rows over {kept} kept canonical tags");
                    counts.Bump("ai-tags-raw", raw); counts.Bump("ai-tags-folded", folded);
                    nextCursor = 2; return false;
                }
                case 2:
                {
                    var n = ItemResolver.ResolveSeries(hot);
                    log($"resolve: series scalars — {n} series");
                    counts.Bump("series-resolved", n);
                    nextCursor = ItemsBase; return false;
                }
                default:
                {
                    var after = cursor - ItemsBase;
                    var last = ItemResolver.ResolveItems(hot, after, batchSize, out var n);
                    counts.Bump("items-resolved", n);
                    nextCursor = ItemsBase + last;
                    if (n < batchSize)
                    {
                        Stamp(hot);
                        log("resolve: registry stamped");
                        return true;
                    }
                    return false;
                }
            }
        }

        /// <summary>Drain every step in one call (the CLI verb's default); returns the number of items resolved.</summary>
        public static int RunAll(TargetWriter hot, int batchSize, Action<string> log)
        {
            var counts = new UnitCounts();
            long cursor = 0;
            while (!RunStep(hot, cursor, batchSize, log, counts, out cursor)) { }
            return counts.Detail.GetValueOrDefault("items-resolved");
        }

        /// <summary>What this pipeline rebuilds — the series identity itself is the books-resolve --series job (R6), not this pass.</summary>
        private static readonly HashSet<string> Stamped = new(StringComparer.Ordinal) { "Item.Resolved*", "Series.Resolved*", "ItemTag/SeriesTag(folds)", "Insight.IsCurrent" };

        private static void Stamp(TargetWriter hot)
        {
            var now = DateTime.UtcNow;
            foreach (var e in DerivedTables.All)
            {
                if (!Stamped.Contains(e.Name)) continue;
                var fp = Fingerprint(hot, e.FingerprintSql);
                var rows = e.Name switch
                {
                    "Item.Resolved*" => hot.Scalar<long>("SELECT count(*) FROM Item WHERE ResolvedAt IS NOT NULL"),
                    "Series.Resolved*" => hot.Scalar<long>("SELECT count(*) FROM Series WHERE ResolvedAt IS NOT NULL"),
                    "ItemTag/SeriesTag(folds)" => hot.Scalar<long>("SELECT (SELECT count(*) FROM ItemTag) + (SELECT count(*) FROM SeriesTag)"),
                    "Insight.IsCurrent" => hot.Scalar<long>("SELECT count(*) FROM Insight WHERE IsCurrent = 1"),
                    _ => (long?)null,
                };
                hot.Upsert("DerivedTable", new { Name = e.Name, RebuildJob = e.RebuildJob, InputFingerprint = fp, LastRebuiltAt = now, RowCount = (int)(rows ?? 0) });
            }
        }

        /// <summary>A fingerprint SQL may return several rows (UNION ALL); the fingerprint is their join.</summary>
        public static string Fingerprint(TargetWriter hot, string sql)
        {
            using var cmd = hot.CreateCommand(sql);
            using var rd = cmd.ExecuteReader();
            var parts = new List<string>();
            while (rd.Read()) parts.Add(rd.IsDBNull(0) ? "" : Convert.ToString(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? "");
            return string.Join("|", parts);
        }
    }
}
