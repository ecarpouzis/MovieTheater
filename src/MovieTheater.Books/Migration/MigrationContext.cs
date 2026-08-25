using System.Text.Json;

namespace MovieTheater.Books.Migration
{
    public sealed class MigrationOptions
    {
        public required string SourcePath { get; init; }
        public required string TargetPath { get; init; }
        public required string LegsPath { get; init; }
        public string? CalibreLinkPath { get; init; }
        public string? CacheDir { get; init; }
        public string? ReportDir { get; init; }
        public int BatchSize { get; init; } = 5000;
        /// <summary>0 = drain (with the no-progress safety break).</summary>
        public int MaxBatches { get; init; }
        public bool DryRun { get; init; }
        /// <summary>Run only this stage (or "stage/Unit").</summary>
        public string? Stage { get; init; }
        /// <summary>Override the persisted cursor of the first unit run.</summary>
        public long? After { get; init; }
        /// <summary>The site user id the standalone site's owner account becomes (decision 5: the only user copied).</summary>
        public int UserIdForOwner { get; init; } = 1;
        /// <summary>The standalone site's owner username (a configured value — never a literal in code).</summary>
        public required string OwnerUsername { get; init; }
    }

    /// <summary>
    /// Everything a stage needs besides its rows: the v1 source, the contract, options, and the lazily built
    /// lookup sets (which ids exist, how a parsed key resolves to a series, which LOCG rows are matched…). The
    /// lookups are built from the v1 SOURCE, so a guard like "does this item exist" answers what the migration
    /// WILL have written by the time the FK is checked, not what a half-run target currently holds.
    /// </summary>
    public sealed class MigrationContext
    {
        public V1Source Source { get; }
        public MappingContract Mapping { get; }
        public MigrationOptions Options { get; }
        public Action<string> Log { get; }

        public MigrationContext(V1Source source, MappingContract mapping, MigrationOptions options, Action<string> log)
        {
            Source = source; Mapping = mapping; Options = options; Log = log;
        }

        private HashSet<long>? itemIds, seriesIds, folderIds, cvVolumeIds, cvIssueIds, publisherIds, dupGroupIds, locgMatchedIds, bookInsightItemIds, carriedSeriesInsightIds, muSeriesIds, externalWorkIds;
        private Dictionary<string, long>? seriesByCanonical, seriesByNameLower, seriesByParsedKeyLower;
        private Dictionary<long, long>? itemSeries;
        private Dictionary<long, List<long>>? locgComicToItems;
        private Dictionary<long, long>? calibreByItem;
        private HashSet<string>? cvdbResolvedNames;
        private List<(long Id, string Path, int Kind)>? roots;
        private Dictionary<long, (long? ParentId, string Path)>? folderTree;

        private HashSet<long> Ids(string sql) => Source.Rows(sql).Select(r => r.L(r.Has("Id") ? "Id" : "v") ?? 0).ToHashSet();
        private HashSet<long> IdSet(string table, string col, string? where = null) =>
            Source.Rows($"SELECT \"{col}\" AS v FROM \"{table}\"" + (where == null ? "" : " WHERE " + where)).Select(r => r.L("v") ?? 0).ToHashSet();

        public bool ItemExists(long? id) => id != null && (itemIds ??= IdSet("Comics", "Id")).Contains(id.Value);
        public bool SeriesExists(long? id) => id != null && (seriesIds ??= IdSet("Series", "Id")).Contains(id.Value);
        public bool FolderExists(long? id) => id != null && (folderIds ??= IdSet("Folders", "Id")).Contains(id.Value);
        public bool PublisherExists(long? id) => id != null && (publisherIds ??= IdSet("Publishers", "Id")).Contains(id.Value);
        public bool CvVolumeExists(long? id) => id != null && (cvVolumeIds ??= IdSet("ComicvineVolumes", "ComicvineId")).Contains(id.Value);
        public bool CvIssueExists(long? id) => id != null && (cvIssueIds ??= IdSet("ComicvineIssues", "ComicvineId")).Contains(id.Value);
        public bool DuplicateGroupExists(long? id) => id != null && (dupGroupIds ??= IdSet("DuplicateGroups", "Id")).Contains(id.Value);
        public bool MuSeriesExists(long? id) => id != null && (muSeriesIds ??= IdSet("MangaUpdatesSeries", "MuSeriesId")).Contains(id.Value);
        public bool ExternalWorkExists(long? id) => id != null && (externalWorkIds ??= IdSet("ExternalWorks", "Id")).Contains(id.Value);
        public bool BookInsightExists(long itemId) => (bookInsightItemIds ??= IdSet("ClaudeBookMetadata", "ComicId")).Contains(itemId);

        /// <summary>LOCG comic ids some item is matched to (the hot LocgComic subset).</summary>
        public bool LocgMatched(long locgComicId) => (locgMatchedIds ??= IdSet("LocgMatches", "LocgComicId", "Status='matched' AND LocgComicId IS NOT NULL")).Contains(locgComicId);

        public IReadOnlyList<long> ItemsForLocgComic(long locgComicId)
        {
            locgComicToItems ??= Source.Rows("SELECT ComicId, LocgComicId FROM LocgMatches WHERE Status='matched' AND LocgComicId IS NOT NULL")
                .GroupBy(r => r.L("LocgComicId")!.Value).ToDictionary(g => g.Key, g => g.Select(r => r.L("ComicId")!.Value).ToList());
            return locgComicToItems.TryGetValue(locgComicId, out var l) ? l : Array.Empty<long>();
        }

        /// <summary>Series by CanonicalKey ("cv:19752", "parsed:sad") — the reading-order GroupKey vocabulary.</summary>
        public long? SeriesByCanonicalKey(string? key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            seriesByCanonical ??= Source.Rows("SELECT Id, CanonicalKey FROM Series WHERE CanonicalKey <> ''")
                .GroupBy(r => r.S("CanonicalKey")!).ToDictionary(g => g.Key, g => g.First().L("Id")!.Value, StringComparer.Ordinal);
            return seriesByCanonical.TryGetValue(key, out var id) ? id : null;
        }

        /// <summary>The standalone site's Claude-link backfill rule: a series NAME (lower-cased) matches Series.ResolvedName,
        /// else any SeriesParsedKeys.ParsedKey. Used for the name-keyed insight rows and the name-keyed group marks.</summary>
        public long? SeriesByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            seriesByNameLower ??= Source.Rows("SELECT Id, ResolvedName FROM Series WHERE ResolvedName IS NOT NULL")
                .GroupBy(r => r.S("ResolvedName")!.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Min(r => r.L("Id")!.Value), StringComparer.Ordinal);
            seriesByParsedKeyLower ??= Source.Rows("SELECT ParsedKey, SeriesId FROM SeriesParsedKeys")
                .GroupBy(r => r.S("ParsedKey")!.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Min(r => r.L("SeriesId")!.Value), StringComparer.Ordinal);
            var k = name.Trim().ToLowerInvariant();
            if (seriesByNameLower.TryGetValue(k, out var id)) return id;
            if (seriesByParsedKeyLower.TryGetValue(k, out id)) return id;
            return null;
        }

        private Dictionary<long, long>? insightSeriesByEdge;

        /// <summary>
        /// The series a v1 series-insight row belongs to: first through the edge v1 actually used at runtime
        /// (ComicParsedDetails.ClaudeSeriesMetadataId → the row's SeriesId, majority when a row's issues split), then
        /// by the name rule. The kids gate and the modal rode that edge, so it — not the spelling — decides.
        /// </summary>
        public long? SeriesForInsight(long v1Id, string? seriesName)
        {
            insightSeriesByEdge ??= Source.Rows("SELECT ClaudeSeriesMetadataId AS M, SeriesId AS S, count(*) AS N FROM ComicParsedDetails WHERE ClaudeSeriesMetadataId IS NOT NULL AND SeriesId IS NOT NULL GROUP BY 1, 2")
                .GroupBy(r => r.L("M")!.Value)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.L("N")).ThenBy(r => r.L("S")).First().L("S")!.Value);
            if (insightSeriesByEdge.TryGetValue(v1Id, out var sid)) return sid;
            return SeriesByName(seriesName);
        }

        private Dictionary<(long, long), int>? cloneIds;

        /// <summary>
        /// The OTHER series whose items pointed at a v1 insight row (the majority series owns the original). Each gets an
        /// append-only clone so no item loses its insight edge (the kids gate rides it). Clone ids are deterministic:
        /// <see cref="CloneBase"/> + the pair's position in (insight id, series id) order, so re-runs converge.
        /// </summary>
        public const int CloneBase = 20_000_000;

        public IReadOnlyList<(long SeriesId, int CloneId)> ClonesForInsight(long v1Id)
        {
            if (cloneIds == null)
            {
                SeriesForInsight(0, null); // builds the majority map
                var pairs = Source.Rows("SELECT ClaudeSeriesMetadataId AS M, SeriesId AS S FROM ComicParsedDetails WHERE ClaudeSeriesMetadataId IS NOT NULL AND SeriesId IS NOT NULL GROUP BY 1, 2 ORDER BY 1, 2")
                    .Select(r => (M: r.L("M")!.Value, S: r.L("S")!.Value))
                    .Where(p => insightSeriesByEdge!.TryGetValue(p.M, out var major) && major != p.S)
                    .ToList();
                cloneIds = new Dictionary<(long, long), int>();
                for (var i = 0; i < pairs.Count; i++) cloneIds[(pairs[i].M, pairs[i].S)] = CloneBase + i;
            }
            return cloneIds.Where(kv => kv.Key.Item1 == v1Id).Select(kv => (kv.Key.Item2, kv.Value)).OrderBy(x => x.Item1).ToList();
        }

        /// <summary>v1 ClaudeSeriesMetadata ids that resolve to a series (the carried subset; the rest are exported).</summary>
        public bool SeriesInsightCarried(long v1Id)
        {
            carriedSeriesInsightIds ??= Source.Rows("SELECT Id, SeriesName FROM ClaudeSeriesMetadata")
                .Where(r => SeriesForInsight(r.L("Id")!.Value, r.S("SeriesName")) != null).Select(r => r.L("Id")!.Value).ToHashSet();
            return carriedSeriesInsightIds.Contains(v1Id);
        }

        public long? ItemSeriesId(long itemId)
        {
            itemSeries ??= Source.Rows("SELECT ComicId, SeriesId FROM ComicParsedDetails WHERE SeriesId IS NOT NULL")
                .ToDictionary(r => r.L("ComicId")!.Value, r => r.L("SeriesId")!.Value);
            return itemSeries.TryGetValue(itemId, out var s) ? s : null;
        }

        public bool IsCvdbResolvedName(string value)
        {
            cvdbResolvedNames ??= Source.Rows("SELECT ResolvedName FROM CvdbResolutions WHERE Status='Resolved' AND ResolvedName IS NOT NULL")
                .Select(r => r.S("ResolvedName")!.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            return cvdbResolvedNames.Contains(value.Trim().ToLowerInvariant());
        }

        public long? CalibreBookId(long itemId)
        {
            if (calibreByItem == null)
            {
                calibreByItem = new Dictionary<long, long>();
                var path = Options.CalibreLinkPath;
                if (path != null && File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var e in doc.RootElement.EnumerateArray())
                        if (e.TryGetProperty("comicId", out var c) && e.TryGetProperty("calibreId", out var k) && c.ValueKind == JsonValueKind.Number && k.ValueKind == JsonValueKind.Number)
                            calibreByItem[c.GetInt64()] = k.GetInt64();
                }
            }
            return calibreByItem.TryGetValue(itemId, out var id) ? id : null;
        }

        public int CalibreLinkCount => calibreByItem?.Count ?? 0;

        private Dictionary<long, List<long>>? seriesByExternalWork;
        private Dictionary<long, (string?, string?)>? muJson;
        private Dictionary<long, string?>? gcdGenres;

        /// <summary>Series linked to an external work (v1 Series.ExternalWorkId) - the External fold's targets.</summary>
        public IReadOnlyList<long> SeriesForExternalWork(long workId)
        {
            seriesByExternalWork ??= Source.Rows("SELECT Id, ExternalWorkId FROM Series WHERE ExternalWorkId IS NOT NULL")
                .GroupBy(r => r.L("ExternalWorkId")!.Value).ToDictionary(g => g.Key, g => g.Select(r => r.L("Id")!.Value).ToList());
            return seriesByExternalWork.TryGetValue(workId, out var l) ? l : Array.Empty<long>();
        }

        public (string? genresJson, string? categoriesJson) MuJson(long muSeriesId)
        {
            muJson ??= Source.Rows("SELECT MuSeriesId, GenresJson, CategoriesJson FROM MangaUpdatesSeries")
                .ToDictionary(r => r.L("MuSeriesId")!.Value, r => (r.S("GenresJson"), r.S("CategoriesJson")));
            return muJson.TryGetValue(muSeriesId, out var j) ? j : (null, null);
        }

        public string? GcdStoryGenres(long gcdIssueId)
        {
            gcdGenres ??= Source.Rows("SELECT GcdIssueId, StoryGenres FROM GcdIssues WHERE StoryGenres IS NOT NULL AND StoryGenres <> ''")
                .ToDictionary(r => r.L("GcdIssueId")!.Value, r => r.S("StoryGenres"));
            return gcdGenres.TryGetValue(gcdIssueId, out var g) ? g : null;
        }

        // ── paths ───────────────────────────────────────────────────────────────────────────────

        private List<(long Id, string Path, int Kind)> Roots =>
            roots ??= Source.Rows("SELECT Id, Path, Category FROM LibraryPaths").Select(r => (r.L("Id")!.Value, NormPath(r.S("Path")!), r.Int("Category"))).ToList();

        public static string NormPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

        /// <summary>The root whose path is the longest prefix of <paramref name="path"/> (case-insensitive).</summary>
        public long? RootOf(string? path)
        {
            if (path == null) return null;
            var p = NormPath(path);
            long? best = null; var bestLen = -1;
            foreach (var (id, rp, _) in Roots)
                if (rp.Length > bestLen && (p.Equals(rp, StringComparison.OrdinalIgnoreCase) || p.StartsWith(rp + "\\", StringComparison.OrdinalIgnoreCase))) { best = id; bestLen = rp.Length; }
            return best;
        }

        public string? RootPath(long rootId) => Roots.FirstOrDefault(r => r.Id == rootId).Path;

        /// <summary>Segments below the root: the root folder itself is 0, its children 1.</summary>
        public int DepthOf(string path, long? rootId)
        {
            var rp = rootId == null ? null : RootPath(rootId.Value);
            var p = NormPath(path);
            if (rp == null || p.Length <= rp.Length) return 0;
            return p.Substring(rp.Length).Count(c => c == '\\');
        }

        private Dictionary<long, (long? ParentId, string Path)> FolderTree =>
            folderTree ??= Source.Rows("SELECT Id, ParentId, FolderPath FROM Folders").ToDictionary(r => r.L("Id")!.Value, r => (r.L("ParentId"), NormPath(r.S("FolderPath") ?? "")));

        /// <summary>The depth-1 ancestor (the v1 "collection"): itself at depth 1, null for a root folder.</summary>
        public long? TopFolderOf(long folderId)
        {
            var tree = FolderTree;
            if (!tree.TryGetValue(folderId, out var node)) return null;
            var rootId = RootOf(node.Path);
            var depth = DepthOf(node.Path, rootId);
            if (depth == 0) return null;
            var cur = folderId;
            for (var d = depth; d > 1; d--)
            {
                if (!tree.TryGetValue(cur, out var n) || n.ParentId == null) return null;
                cur = n.ParentId.Value;
            }
            return cur;
        }

        public bool FolderIconExists(long folderId)
        {
            var dir = Options.CacheDir;
            return dir != null && File.Exists(Path.Combine(dir, $"f_{folderId}.jpg"));
        }

        public string ReportPath(string name)
        {
            var dir = Options.ReportDir ?? Path.GetDirectoryName(Path.GetFullPath(Options.TargetPath))!;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }
    }
}
