using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Providers
{
    /// <summary>
    /// The two OFFLINE rips the v2 port left behind, restored into the legs warehouse. Neither opens a socket:
    /// each reads a local file the operator names and writes `books-legs.db`.
    ///
    /// <para><b>Why they exist.</b> The containment repair needs two things v2 never carried over: ComicVine's
    /// volume DESCRIPTIONS (the "Collected Editions" prose — the only place ComicVine publishes what an edition
    /// collects) and the LOCG REVERSE reprint edges (an edition whose own detail page was a shell still shows up
    /// in the "reprinted in" list of every issue it collects, so the table of contents is recoverable from the
    /// other side).</para>
    /// </summary>
    public static class RipImporters
    {
        public sealed record ImportResult(int Processed, long Remaining, long? NextCursor, int Written, int Skipped)
        {
            public bool Done => Processed == 0;
            public override string ToString() =>
                $"{{ processed: {Processed}, remaining: {Remaining}, nextCursor: \"{NextCursor}\", written: {Written}, skipped: {Skipped} }}";
        }

        // ── ComicVine volume descriptions ───────────────────────────────────────────────────────────────

        /// <summary>
        /// One page of `cv_volume` from the offline ComicVine rip → legs `CvVolumeDescription`. Only the
        /// description text is kept (the rip's `raw_api_response` runs to 14.5 GB and nothing else in it is
        /// read here), so the file is streamed a page at a time and never loaded whole.
        /// </summary>
        public static ImportResult ImportCvDescriptions(SqliteConnection rip, SqliteConnection legs, long after, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            var rows = new List<(long Id, string? Description)>();
            using (var cmd = rip.CreateCommand())
            {
                cmd.CommandText = "SELECT id, raw_api_response FROM cv_volume WHERE id > $a ORDER BY id LIMIT $n";
                cmd.Parameters.AddWithValue("$a", after);
                cmd.Parameters.AddWithValue("$n", batchSize);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var id = rd.GetInt64(0);
                    rows.Add((id, rd.IsDBNull(1) ? null : DescriptionOf(rd.GetString(1))));
                }
            }
            if (rows.Count == 0) return new ImportResult(0, 0, null, 0, 0);

            int written = 0, skipped = 0;
            using (var tx = legs.BeginTransaction())
            using (var cmd = legs.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CvVolumeDescription (CvVolumeId, Description, HasCollectedBlock, ImportedAt)
VALUES ($id, $desc, $has, $at)
ON CONFLICT(CvVolumeId) DO UPDATE SET Description = excluded.Description,
    HasCollectedBlock = excluded.HasCollectedBlock, ImportedAt = excluded.ImportedAt";
                var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                var pDesc = cmd.Parameters.Add("$desc", SqliteType.Text);
                var pHas = cmd.Parameters.Add("$has", SqliteType.Integer);
                var pAt = cmd.Parameters.Add("$at", SqliteType.Text);
                var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (var (id, description) in rows)
                {
                    if (string.IsNullOrWhiteSpace(description)) { skipped++; continue; }
                    pId.Value = id;
                    pDesc.Value = description;
                    pHas.Value = CvEditionParser.HasCollectedSection(description) ? 1 : 0;
                    pAt.Value = now;
                    cmd.ExecuteNonQuery();
                    written++;
                }
                tx.Commit();
            }

            var next = rows[^1].Id;
            long remaining;
            using (var cmd = rip.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM cv_volume WHERE id > $a";
                cmd.Parameters.AddWithValue("$a", next);
                remaining = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }
            return new ImportResult(rows.Count, remaining, next, written, skipped);
        }

        /// <summary>The rip stores the ComicVine `results` object itself, so `description` is a top-level field.</summary>
        public static string? DescriptionOf(string rawApiResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawApiResponse);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object)
                    root = results;
                return root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        // ── LOCG reverse reprint edges ──────────────────────────────────────────────────────────────────

        /// <summary>One entry of a cached `reprints/&lt;containedId&gt;.json`: a comic that REPRINTS it.</summary>
        public readonly record struct LocgReprintEdge(long ContainerId, long ContainedId);

        /// <summary>Parse one cached reverse-reprint file. The file is named for the comic being reprinted; the
        /// entries are the editions that reprint it — so each entry is a CONTAINER of this comic.</summary>
        public static List<LocgReprintEdge> ParseReprintFile(string fileName, string json)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            if (!long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contained) || contained <= 0)
                return [];
            var edges = new List<LocgReprintEdge>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("locgId", out var idProp)) continue;
                    var raw = idProp.ValueKind == JsonValueKind.String ? idProp.GetString()
                        : idProp.ValueKind == JsonValueKind.Number ? idProp.GetRawText() : null;
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var container)) continue;
                    if (container <= 0 || container == contained) continue;
                    edges.Add(new LocgReprintEdge(container, contained));
                }
            }
            catch (JsonException) { return []; }
            return edges;
        }

        /// <summary>
        /// One page of `reprints/*.json` → legs `LocgContainment`. Idempotent: the table's unique
        /// (Container, Contained) index makes a re-run a no-op, and ids continue from the table's own maximum.
        /// The page is a slice of the directory listing sorted by the numeric file name, which IS the cursor.
        /// </summary>
        public static ImportResult ImportLocgReprints(SqliteConnection legs, IReadOnlyList<string> files, long after, int batchSize)
        {
            batchSize = Math.Clamp(batchSize, 1, 20_000);
            var page = new List<string>();
            foreach (var f in files)
            {
                if (KeyOf(f) <= after) continue;
                page.Add(f);
                if (page.Count >= batchSize) break;
            }
            if (page.Count == 0) return new ImportResult(0, 0, null, 0, 0);

            long nextId;
            using (var cmd = legs.CreateCommand())
            {
                cmd.CommandText = "SELECT coalesce(max(Id), 0) FROM LocgContainment";
                nextId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) + 1;
            }

            int written = 0, skipped = 0;
            using (var tx = legs.BeginTransaction())
            using (var cmd = legs.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO LocgContainment (Id, ContainerLocgComicId, ContainedLocgComicId, Ordinal, Source, ScrapedAt)
VALUES ($id, $container, $contained, 0, 'reprints-cache', $at)
ON CONFLICT(ContainerLocgComicId, ContainedLocgComicId) DO NOTHING";
                var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
                var pContainer = cmd.Parameters.Add("$container", SqliteType.Integer);
                var pContained = cmd.Parameters.Add("$contained", SqliteType.Integer);
                var pAt = cmd.Parameters.Add("$at", SqliteType.Text);
                pAt.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (var file in page)
                {
                    List<LocgReprintEdge> edges;
                    try { edges = ParseReprintFile(file, File.ReadAllText(file)); }
                    catch (IOException) { skipped++; continue; }
                    foreach (var edge in edges)
                    {
                        pId.Value = nextId;
                        pContainer.Value = edge.ContainerId;
                        pContained.Value = edge.ContainedId;
                        if (cmd.ExecuteNonQuery() > 0) { written++; nextId++; }
                        else skipped++;
                    }
                }
                tx.Commit();
            }

            var next = KeyOf(page[^1]);
            var remaining = files.Count(f => KeyOf(f) > next);
            return new ImportResult(page.Count, remaining, next, written, skipped);
        }

        /// <summary>The numeric file name, which orders the walk and is the cursor. Unparseable names sort last
        /// and are never visited (they carry no comic id, so they carry no edge).</summary>
        public static long KeyOf(string path) =>
            long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : long.MaxValue;

        /// <summary>The reprint files in cursor order.</summary>
        public static List<string> ReprintFiles(string dir) =>
            Directory.EnumerateFiles(dir, "*.json").Where(f => KeyOf(f) != long.MaxValue).OrderBy(KeyOf).ToList();
    }
}
