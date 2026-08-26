using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Providers
{
    /// <summary>What one import batch did.</summary>
    public sealed record ImportBatchResult(int Processed, long Remaining, string? NextCursor, int Written, int Skipped)
    {
        public bool Done => Processed == 0;
        public override string ToString() =>
            $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", skipped: {Skipped} }}  [written: {Written}]";
    }

    /// <summary>
    /// The CONSUME side of the offline scrape pipelines.
    ///
    /// <para><b>The scrapers themselves are not ported and never will be.</b> LOCG and GCD are Node and Python
    /// pipelines that run offline, produce a file, and are the right tool for that job; what the site needs is
    /// the ability to take their output IN. So these verbs read a JSONL export, a GCD SQLite dump or a
    /// MangaUpdates payload and land it in the warehouse plus the links the runtime reads — and nothing here
    /// opens a socket.</para>
    ///
    /// <para>All three are chunked by their source's own ordering, idempotent (upsert by key), and treat a row
    /// naming an unknown hot id as SKIP-AND-REPORT: no FK crosses the hot/legs boundary, so a warehouse row
    /// about a comic we do not own is a fact, not an error.</para>
    /// </summary>
    public static class LegImporters
    {
        // ── LOCG ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>books-locg-import</c> — one line of JSON per LOCG comic into `LocgComicRaw` (the warehouse keeps
        /// every row, including the ~73k stubs) and, for rows an `ItemProviderLink(Locg)` references, into the
        /// hot `LocgComic` subset the projection and modal actually read.
        /// </summary>
        public static ImportBatchResult ImportLocgJsonl(TargetWriter hot, SqliteConnection legs, string jsonlPath, long afterLine, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            var referenced = ReferencedLocgIds(hot);

            using var reader = new StreamReader(jsonlPath);
            for (long i = 0; i < afterLine; i++) if (reader.ReadLine() == null) return new ImportBatchResult(0, 0, null, 0, 0);

            int processed = 0, written = 0, skipped = 0;
            using var tx = legs.BeginTransaction();
            hot.Begin();
            string? line;
            while (processed < batchSize && (line = reader.ReadLine()) != null)
            {
                processed++;
                if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { skipped++; continue; }
                using (doc)
                {
                    var e = doc.RootElement;
                    var id = Long(e, "id") ?? Long(e, "locgComicId");
                    if (id == null) { skipped++; continue; }

                    UpsertLegs(legs, tx, "LocgComicRaw", "LocgComicId", id.Value, new (string, object?)[]
                    {
                        ("LocgSeriesId", Long(e, "seriesId")), ("SeriesName", Str(e, "seriesName")), ("Title", Str(e, "title")),
                        ("IssueNumber", Str(e, "issueNumber")), ("Format", Str(e, "format")), ("ReleaseDate", Str(e, "releaseDate")),
                        ("CoverDate", Str(e, "coverDate")), ("PageCount", Long(e, "pageCount")), ("Description", Str(e, "description")),
                        ("CommunityRating", Dbl(e, "communityRating")), ("RatingCount", Long(e, "ratingCount")),
                        ("IsKey", Bool(e, "isKey")), ("KeyType", Str(e, "keyType")), ("KeyReason", Str(e, "keyReason")),
                        ("Isbn", Str(e, "isbn")), ("Upc", Str(e, "upc")), ("DistributorSku", Str(e, "distributorSku")),
                        ("CoverPrice", Str(e, "coverPrice")), ("EstimatedValue", Str(e, "estimatedValue")),
                        ("CoverUrl", Str(e, "coverUrl")), ("Url", Str(e, "url")), ("StoryCount", Long(e, "storyCount")),
                        ("StoryIdsJson", Raw(e, "storyIds")), ("ScrapedAt", DateTime.UtcNow.ToString("O")),
                    });

                    if (referenced.Contains(id.Value))
                        hot.Upsert("LocgComic", new
                        {
                            LocgComicId = (int)id.Value,
                            LocgSeriesId = (int?)Long(e, "seriesId"),
                            SeriesName = Str(e, "seriesName"), Title = Str(e, "title"), IssueNumber = Str(e, "issueNumber"),
                            Format = Str(e, "format"), CoverDate = Str(e, "coverDate"), PageCount = (int?)Long(e, "pageCount"),
                            Description = Str(e, "description"), CommunityRating = Dbl(e, "communityRating"),
                            RatingCount = (int?)Long(e, "ratingCount"), IsKey = Bool(e, "isKey") ?? false, KeyType = Str(e, "keyType"),
                            Isbn = Str(e, "isbn"), Upc = Str(e, "upc"), CoverPrice = Str(e, "coverPrice"),
                            CoverUrl = Str(e, "coverUrl"), StoryCount = (int?)Long(e, "storyCount"), ScrapedAt = DateTime.UtcNow,
                        });

                    // The creator list is normalized for EVERY row, so the hot ItemCredit(Source=Locg) subset can
                    // be re-derived at any time without going back to the export.
                    if (e.TryGetProperty("creators", out var creators) && creators.ValueKind == JsonValueKind.Array)
                    {
                        var ordinal = 0;
                        foreach (var c in creators.EnumerateArray())
                            UpsertLegs(legs, tx, "LocgCreatorRaw", new[] { ("LocgComicId", (object?)id.Value), ("Ordinal", ordinal++) },
                                new (string, object?)[] { ("Role", Str(c, "role")), ("Name", Str(c, "name")), ("PeopleId", Str(c, "peopleId")) });
                    }
                    written++;
                }
            }
            hot.Commit();
            tx.Commit();
            return new ImportBatchResult(processed, -1, (afterLine + processed).ToString(CultureInfo.InvariantCulture), written, skipped);
        }

        /// <summary>
        /// <c>books-locg-import-map</c> — a two-column CSV (<c>itemId,locgComicId</c>) of matches decided
        /// offline, landed as `ItemProviderLink(Locg, Manual)`. A row naming an unknown item is skipped and
        /// reported; nothing is created for it.
        /// </summary>
        public static ImportBatchResult ImportLocgMap(TargetWriter hot, string csvPath, long afterLine, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            using var reader = new StreamReader(csvPath);
            for (long i = 0; i < afterLine; i++) if (reader.ReadLine() == null) return new ImportBatchResult(0, 0, null, 0, 0);

            int processed = 0, written = 0, skipped = 0;
            hot.Begin();
            string? line;
            while (processed < batchSize && (line = reader.ReadLine()) != null)
            {
                processed++;
                var parts = line.Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var itemId) || !long.TryParse(parts[1].Trim(), out var locgId)) { skipped++; continue; }
                if (hot.Scalar<long>("SELECT count(*) FROM Item WHERE Id = $id", ("$id", itemId)) == 0) { skipped++; continue; }
                hot.Upsert("ItemProviderLink", new
                {
                    ItemId = itemId,
                    Provider = Provider.Locg,
                    ProviderKey = locgId.ToString(CultureInfo.InvariantCulture),
                    Status = LinkStatus.Manual,
                    Method = "offline-map",
                    Quality = LinkQuality.High,
                    Applied = true,
                    AttemptCount = 1,
                    AttemptedAt = DateTime.UtcNow,
                });
                written++;
            }
            hot.Commit();
            return new ImportBatchResult(processed, -1, (afterLine + processed).ToString(CultureInfo.InvariantCulture), written, skipped);
        }

        // ── GCD ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>books-gcd-match</c> — match items to Grand Comics Database issues out of a READ-ONLY GCD SQLite
        /// dump, by ISBN first and barcode second. Both are exact identifiers: GCD's strength is that its rows
        /// are human-verified, so an exact-identifier match is trustworthy and a fuzzy one would not be — which
        /// is why there is no name fallback here.
        /// </summary>
        public static ImportBatchResult MatchGcd(TargetWriter hot, SqliteConnection gcd, SqliteConnection legs, long afterItemId, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            var candidates = new List<(int ItemId, string? Isbn, string? Barcode)>();
            foreach (var (itemId, payload) in hot.Pairs($@"
SELECT i.Id, coalesce(bd.Isbn, ce.Identifier, '') || char(31) || coalesce(ce.Identifier, '')
FROM Item i
LEFT JOIN BookDetail bd ON bd.ItemId = i.Id
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
WHERE i.Id > {afterItemId} ORDER BY i.Id LIMIT {batchSize}"))
            {
                var p = payload!.Split(TargetWriter.Sep);
                candidates.Add(((int)itemId, p[0].Length == 0 ? null : p[0], p[1].Length == 0 ? null : p[1]));
            }
            if (candidates.Count == 0) return new ImportBatchResult(0, 0, null, 0, 0);

            int written = 0, skipped = 0;
            hot.Begin();
            using var tx = legs.BeginTransaction();
            foreach (var (itemId, isbn, barcode) in candidates)
            {
                var issue = isbn != null ? FindGcdIssue(gcd, "ValidIsbn", isbn) ?? FindGcdIssue(gcd, "Isbn", isbn) : null;
                issue ??= barcode != null ? FindGcdIssue(gcd, "Barcode", barcode) : null;
                if (issue == null) { skipped++; continue; }

                // The issue row goes to the WAREHOUSE (only the genre fold reads it), the LINK to the hot file.
                UpsertLegs(legs, tx, "GcdIssue", "GcdIssueId", issue.Value.Id, new (string, object?)[]
                {
                    ("GcdSeriesId", issue.Value.SeriesId), ("SeriesName", issue.Value.SeriesName),
                    ("Number", issue.Value.Number), ("ValidIsbn", issue.Value.Isbn), ("Barcode", issue.Value.Barcode),
                    ("StoryGenres", issue.Value.StoryGenres), ("ImportedAt", DateTime.UtcNow.ToString("O")),
                });
                hot.Upsert("ItemProviderLink", new
                {
                    ItemId = itemId,
                    Provider = Provider.Gcd,
                    ProviderKey = issue.Value.Id.ToString(CultureInfo.InvariantCulture),
                    SecondaryKey = issue.Value.SeriesId?.ToString(CultureInfo.InvariantCulture),
                    Status = LinkStatus.Matched,
                    Method = isbn != null ? "isbn" : "barcode",
                    Quality = LinkQuality.High,
                    Confidence = 0.95,
                    AttemptCount = 1,
                    AttemptedAt = DateTime.UtcNow,
                });
                written++;
            }
            tx.Commit();
            hot.Commit();
            var next = candidates[^1].ItemId;
            return new ImportBatchResult(candidates.Count, hot.Scalar<long>($"SELECT count(*) FROM Item WHERE Id > {next}"),
                next.ToString(CultureInfo.InvariantCulture), written, skipped);
        }

        private readonly record struct GcdIssue(long Id, long? SeriesId, string? SeriesName, string? Number, string? Isbn, string? Barcode, string? StoryGenres);

        private static GcdIssue? FindGcdIssue(SqliteConnection gcd, string column, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            using var cmd = gcd.CreateCommand();
            cmd.CommandText = $"SELECT GcdIssueId, GcdSeriesId, SeriesName, Number, ValidIsbn, Barcode, StoryGenres FROM GcdIssue WHERE \"{column}\" = $v LIMIT 1";
            cmd.Parameters.AddWithValue("$v", value.Trim());
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;
            return new GcdIssue(rd.GetInt64(0),
                rd.IsDBNull(1) ? null : rd.GetInt64(1), rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetString(6));
        }

        // ── MangaUpdates ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>books-mu-import</c> — a JSON array of MangaUpdates series into the hot `MuSeries` (description and
        /// bayesian rating are projected) and the raw genre/category lists into `MuSeriesRaw`. The genres reach
        /// the facets through <c>books-resolve --tags</c>, never from here.
        /// </summary>
        public static ImportBatchResult ImportMangaUpdates(TargetWriter hot, SqliteConnection legs, string jsonPath, long afterIndex, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The MangaUpdates export must be a JSON array.");

            var all = doc.RootElement.EnumerateArray().ToList();
            if (afterIndex >= all.Count) return new ImportBatchResult(0, 0, null, 0, 0);
            var page = all.Skip((int)afterIndex).Take(batchSize).ToList();

            int written = 0, skipped = 0;
            hot.Begin();
            using var tx = legs.BeginTransaction();
            foreach (var e in page)
            {
                var id = Long(e, "muSeriesId") ?? Long(e, "id");
                if (id == null) { skipped++; continue; }
                hot.Upsert("MuSeries", new
                {
                    Id = id.Value,
                    Title = Str(e, "title"), Year = (int?)Long(e, "year"), Type = Str(e, "type"), Status = Str(e, "status"),
                    Completed = Bool(e, "completed") ?? false, Description = Str(e, "description"),
                    BayesianRating = Dbl(e, "bayesianRating"), Url = Str(e, "url"), ScrapedAt = DateTime.UtcNow,
                });
                UpsertLegs(legs, tx, "MuSeriesRaw", "MuSeriesId", id.Value, new (string, object?)[]
                {
                    ("GenresJson", Raw(e, "genres")), ("CategoriesJson", Raw(e, "categories")), ("RawJson", e.GetRawText()),
                });
                written++;
            }
            tx.Commit();
            hot.Commit();
            return new ImportBatchResult(page.Count, all.Count - afterIndex - page.Count,
                (afterIndex + page.Count).ToString(CultureInfo.InvariantCulture), written, skipped);
        }

        // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────

        private static HashSet<long> ReferencedLocgIds(TargetWriter hot)
        {
            var set = new HashSet<long>();
            foreach (var (_, key) in hot.Pairs($"SELECT rowid, ProviderKey FROM ItemProviderLink WHERE Provider = {(int)Provider.Locg} AND ProviderKey IS NOT NULL"))
                if (long.TryParse(key, out var id)) set.Add(id);
            return set;
        }

        private static void UpsertLegs(SqliteConnection legs, SqliteTransaction tx, string table, string keyColumn, object keyValue, (string, object?)[] values) =>
            UpsertLegs(legs, tx, table, new[] { (keyColumn, (object?)keyValue) }, values);

        private static void UpsertLegs(SqliteConnection legs, SqliteTransaction tx, string table, (string Name, object? Value)[] key, (string Name, object? Value)[] values)
        {
            var cols = key.Select(k => k.Name).Concat(values.Select(v => v.Name)).ToList();
            using var cmd = legs.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO \"{table}\" ({string.Join(",", cols.Select(c => '"' + c + '"'))}) VALUES ({string.Join(",", cols.Select(c => "$" + c))})" +
                $" ON CONFLICT({string.Join(",", key.Select(k => '"' + k.Name + '"'))}) DO UPDATE SET " +
                string.Join(",", values.Select(v => $"\"{v.Name}\"=excluded.\"{v.Name}\""));
            foreach (var (name, value) in key.Concat(values))
                cmd.Parameters.AddWithValue("$" + name, value ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long? Long(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
                _ => null,
            };
        }

        private static double? Dbl(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDouble(out var n) => n,
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            };
        }

        private static bool? Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
                _ => null,
            } : null;

        private static string? Raw(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetRawText() : null;
    }
}
