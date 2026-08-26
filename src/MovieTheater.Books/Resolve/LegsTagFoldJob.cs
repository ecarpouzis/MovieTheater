using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// <c>books-resolve --tags</c> — the three folds whose INPUTS live in the offline warehouse
    /// (`books-legs.db`), rewritten as a job that reads that file rather than as migration-time code.
    ///
    /// <para>R4 ran the External / MU / GCD folds inside the migration because their inputs
    /// (`OpenLibraryWork.SubjectsJson`, `MuSeriesRaw.GenresJson`/`CategoriesJson`, `GcdIssue.StoryGenres`)
    /// only exist in the legs file, which the migration had open anyway. That was a deviation, noted at the
    /// time. This is the job the registry names: it re-derives `SeriesTag(Source=External)`,
    /// `SeriesTag(Source=Mu)` and `ItemTag(Source=Gcd)` from those inputs at any time, without a migration.</para>
    ///
    /// <para>The folding functions themselves are the PURE ones in <see cref="TagFolds"/> — this class only
    /// supplies them rows and writes what they return. No FK crosses the file boundary: the legs file is opened
    /// READ-ONLY on its own connection and a row naming a missing hot id is simply skipped.</para>
    ///
    /// <para>Chunked like every bulk job. The two series-level folds are bounded by the linked-series count (a
    /// few hundred to a few thousand); the GCD fold walks 76k links and is paged by <c>Item.Id</c> — the batch
    /// query's own ordering, which is the cursor.</para>
    /// </summary>
    public static class LegsTagFoldJob
    {
        public const long ExternalCursor = 0;
        public const long MuCursor = 1;
        public const long GcdBase = 1_000_000;

        public sealed record FoldCounts(int External, int Mu, int Gcd)
        {
            public override string ToString() => $"external: {External}, mu: {Mu}, gcd: {Gcd}";
        }

        /// <summary>Open the warehouse read-only. It is never written by any runtime job.</summary>
        public static SqliteConnection OpenLegs(string legsPath)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = legsPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conn.Open();
            return conn;
        }

        /// <summary>Drain every fold. Each phase commits on its own so a kill costs at most one page.</summary>
        public static FoldCounts RunAll(TargetWriter hot, string legsPath, Action<string> log)
        {
            using var legs = OpenLegs(legsPath);
            int external, mu, gcd = 0;

            hot.Begin();
            external = FoldExternal(hot, legs);
            log($"tags: External subjects folded onto {external} series");
            mu = FoldMu(hot, legs);
            log($"tags: MangaUpdates genres folded onto {mu} series");
            hot.Commit();

            long cursor = 0;
            while (true)
            {
                hot.Begin();
                var last = FoldGcdPage(hot, legs, cursor, 5_000, out var seen, out var written);
                hot.Commit();
                gcd += written;
                log($"{{ processed: {seen}, remaining: ?, nextCursor: \"{last}\" }}  [gcd-fold, tags: {gcd}]");
                if (seen == 0) break;
                cursor = last;
            }

            hot.Begin();
            Stamp(hot);
            hot.Commit();
            return new FoldCounts(external, mu, gcd);
        }

        // ── External (Open Library / Google Books subjects) → SeriesTag(Source=External) ──────────────────

        /// <summary>
        /// `Series.ExternalWorkId` → the hot `ExternalWork` row → its provider key → the legs
        /// `OpenLibraryWork.SubjectsJson` → the closed substring whitelist. Series-level, so this is bounded by
        /// how many series carry an external identity at all.
        /// </summary>
        public static int FoldExternal(TargetWriter hot, SqliteConnection legs)
        {
            var workKeyBySeries = new Dictionary<int, string>();
            foreach (var (seriesId, key) in hot.Pairs(@"
SELECT s.Id, ew.ProviderKey FROM Series s
JOIN ExternalWork ew ON ew.Id = s.ExternalWorkId
WHERE s.ExternalWorkId IS NOT NULL AND ew.ProviderKey IS NOT NULL AND ew.ProviderKey <> ''"))
                workKeyBySeries[(int)seriesId] = key!;

            hot.Exec("DELETE FROM SeriesTag WHERE Source = $s", ("$s", (int)TagSource.External));
            if (workKeyBySeries.Count == 0) return 0;

            var subjects = ReadByKey(legs, "SELECT WorkKey, SubjectsJson FROM OpenLibraryWork WHERE WorkKey IN ",
                workKeyBySeries.Values.Distinct().ToList());

            var touched = 0;
            foreach (var (seriesId, workKey) in workKeyBySeries)
            {
                if (!subjects.TryGetValue(workKey, out var json)) continue;
                var canon = TagFolds.FoldSubjects(json);
                if (canon.Count == 0) continue;
                foreach (var value in canon)
                    hot.Upsert("SeriesTag", new { SeriesId = seriesId, Category = TagFolds.FoldedCategory, Value = value, Source = TagSource.External });
                touched++;
            }
            return touched;
        }

        // ── MangaUpdates genres/categories → SeriesTag(Source=Mu) ────────────────────────────────────────

        /// <summary>
        /// The MU link is series-keyed (it is matched AFTER resolution), so this reads `MuSeriesLink` — with
        /// `Series.MuSeriesId` as the materialized fallback — and folds the raw JSON lists from the warehouse.
        /// </summary>
        public static int FoldMu(TargetWriter hot, SqliteConnection legs)
        {
            var muBySeries = new Dictionary<int, long>();
            foreach (var (seriesId, muId) in hot.Pairs(@"
SELECT s.Id, CAST(coalesce(l.MuSeriesId, s.MuSeriesId) AS TEXT) FROM Series s
LEFT JOIN MuSeriesLink l ON l.SeriesId = s.Id AND l.Status = 1
WHERE coalesce(l.MuSeriesId, s.MuSeriesId) IS NOT NULL"))
                muBySeries[(int)seriesId] = long.Parse(muId!);

            hot.Exec("DELETE FROM SeriesTag WHERE Source = $s", ("$s", (int)TagSource.Mu));
            if (muBySeries.Count == 0) return 0;

            var raw = new Dictionary<string, (string? Genres, string? Categories)>(StringComparer.Ordinal);
            var ids = muBySeries.Values.Distinct().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            foreach (var chunk in Chunk(ids, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = "SELECT MuSeriesId, GenresJson, CategoriesJson FROM MuSeriesRaw WHERE MuSeriesId IN (" + Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    raw[rd.GetValue(0).ToString()!] = (rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
            }

            var touched = 0;
            foreach (var (seriesId, muId) in muBySeries)
            {
                if (!raw.TryGetValue(muId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var r)) continue;
                var canon = TagFolds.FoldMu(r.Genres, r.Categories);
                if (canon.Count == 0) continue;
                foreach (var value in canon)
                    hot.Upsert("SeriesTag", new { SeriesId = seriesId, Category = TagFolds.FoldedCategory, Value = value, Source = TagSource.Mu });
                touched++;
            }
            return touched;
        }

        // ── GCD story genres → ItemTag(Source=Gcd) ───────────────────────────────────────────────────────

        /// <summary>
        /// One page of matched GCD links, by `Item.Id`. The densest genre source in the vertical (~75k comics),
        /// which is exactly why it is the paged one. Returns the id the page ended on.
        /// </summary>
        public static long FoldGcdPage(TargetWriter hot, SqliteConnection legs, long after, int batchSize, out int seen, out int written)
        {
            batchSize = Math.Clamp(batchSize, 100, 50_000);
            var links = new List<(int ItemId, string GcdIssueId)>();
            foreach (var (itemId, key) in hot.Pairs($@"
SELECT ItemId, ProviderKey FROM ItemProviderLink
WHERE Provider = {(int)Provider.Gcd} AND Status = {(int)LinkStatus.Matched} AND ProviderKey IS NOT NULL AND ItemId > {after}
ORDER BY ItemId LIMIT {batchSize}"))
                links.Add(((int)itemId, key!));

            seen = links.Count;
            written = 0;
            if (seen == 0) return after;
            var upto = links[^1].ItemId;

            hot.Exec("DELETE FROM ItemTag WHERE Source = $s AND ItemId > $after AND ItemId <= $upto",
                ("$s", (int)TagSource.Gcd), ("$after", after), ("$upto", upto));

            var genres = ReadByKey(legs, "SELECT GcdIssueId, StoryGenres FROM GcdIssue WHERE GcdIssueId IN ",
                links.Select(l => l.GcdIssueId).Distinct().ToList());

            foreach (var (itemId, gcdIssueId) in links)
            {
                if (!genres.TryGetValue(gcdIssueId, out var storyGenres)) continue;
                foreach (var value in TagFolds.FoldGcd(storyGenres))
                {
                    hot.Upsert("ItemTag", new { ItemId = itemId, Category = TagFolds.FoldedCategory, Value = value, Source = TagSource.Gcd });
                    written++;
                }
            }
            return upto;
        }

        // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────

        private static Dictionary<string, string?> ReadByKey(SqliteConnection legs, string sqlPrefix, List<string> keys)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var chunk in Chunk(keys, 400))
            {
                using var cmd = legs.CreateCommand();
                cmd.CommandText = sqlPrefix + "(" + Placeholders(cmd, chunk) + ")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) result[rd.GetValue(0).ToString()!] = rd.IsDBNull(1) ? null : rd.GetString(1);
            }
            return result;
        }

        private static string Placeholders(SqliteCommand cmd, IReadOnlyList<string> values)
        {
            var names = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                names[i] = "$p" + i;
                cmd.Parameters.AddWithValue(names[i], values[i]);
            }
            return string.Join(",", names);
        }

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (var i = 0; i < source.Count; i += size)
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }

        internal static void Stamp(TargetWriter hot)
        {
            var entry = DerivedTables.All.First(e => e.Name == "ItemTag/SeriesTag(folds)");
            hot.Upsert("DerivedTable", new
            {
                Name = entry.Name,
                RebuildJob = entry.RebuildJob,
                InputFingerprint = ResolvePipeline.Fingerprint(hot, entry.FingerprintSql),
                LastRebuiltAt = DateTime.UtcNow,
                RowCount = (int)hot.Scalar<long>("SELECT (SELECT count(*) FROM ItemTag) + (SELECT count(*) FROM SeriesTag)"),
            });
        }
    }
}
