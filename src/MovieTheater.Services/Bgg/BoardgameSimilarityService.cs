using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Services.Bgg
{
    public record SimilarGameDto(
        int id,
        string? name,
        decimal? averageRating,
        int? imageVersion,
        IReadOnlyList<string> sharedMechanics,
        IReadOnlyList<string> sharedCategories
    );

    public class BoardgameSimilarityService
    {
        private volatile IReadOnlyDictionary<int, IReadOnlyList<SimilarGameDto>> _index =
            new Dictionary<int, IReadOnlyList<SimilarGameDto>>();

        public IReadOnlyList<SimilarGameDto> GetSimilar(int gameId)
            => _index.TryGetValue(gameId, out var result) ? result : [];

        /// <summary>
        /// Populates the in-memory index from the similarities already persisted on each
        /// game's <see cref="BoardgameExtraDetails.SimilarGamesJson"/>. Returns the number
        /// of games loaded so the caller can decide whether a one-time rebuild is needed.
        /// </summary>
        public async Task<int> LoadAsync(MovieDb db)
        {
            var rows = await db.BoardgameExtraDetails
                .AsNoTracking()
                .Where(e => e.SimilarGamesJson != null)
                .Select(e => new { e.BoardgameId, e.SimilarGamesJson })
                .ToListAsync();

            var loaded = new Dictionary<int, IReadOnlyList<SimilarGameDto>>();
            foreach (var row in rows)
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<SimilarGameDto>>(row.SimilarGamesJson!);
                    if (list != null) loaded[row.BoardgameId] = list;
                }
                catch { /* skip malformed cache rows; a rebuild will overwrite them */ }
            }

            _index = loaded;
            return loaded.Count;
        }

        /// <summary>
        /// True when some non-expansion game has an <see cref="BoardgameExtraDetails"/> row
        /// whose <see cref="BoardgameExtraDetails.SimilarGamesJson"/> is still NULL — i.e. it
        /// was never run through the compare. After a successful <see cref="RebuildAsync"/>
        /// every such game holds a value (an array or "[]"), so a remaining NULL means an
        /// insert's rebuild broke and the cache needs to be rebuilt.
        /// </summary>
        public Task<bool> HasUncomputedGamesAsync(MovieDb db)
            => db.Boardgames.AnyAsync(g =>
                g.ThingType != "boardgameexpansion"
                && g.ExtraDetails != null
                && g.ExtraDetails.SimilarGamesJson == null);

        public async Task RebuildAsync(MovieDb db)
        {
            var games = await db.Boardgames
                .Where(g => g.ThingType != "boardgameexpansion")
                .Include(g => g.ExtraDetails)
                .Include(g => g.ImageDetails)
                .ToListAsync();

            var linkSets = games.ToDictionary(g => g.id, g => ParseLinkSets(g.ExtraDetails?.LinksJson));

            int n = games.Count;
            var allTagCounts = new Dictionary<(string type, int id), int>();
            foreach (var (mech, cat) in linkSets.Values)
            {
                foreach (var id in mech.Keys) { var k = ("m", id); allTagCounts[k] = allTagCounts.GetValueOrDefault(k) + 1; }
                foreach (var id in cat.Keys) { var k = ("c", id); allTagCounts[k] = allTagCounts.GetValueOrDefault(k) + 1; }
            }
            double TagIdf((string type, int id) key) => Math.Log((double)n / (1.0 + allTagCounts.GetValueOrDefault(key))) * (key.type == "m" ? 1.2 : 1.0);

            var newIndex = new Dictionary<int, IReadOnlyList<SimilarGameDto>>();
            foreach (var game in games)
            {
                var (targetMechanics, targetCategories) = linkSets[game.id];
                if (targetMechanics.Count == 0 && targetCategories.Count == 0) continue;

                var targetTags = targetMechanics.ToDictionary(kv => ("m", kv.Key), kv => kv.Value)
                    .Concat(targetCategories.ToDictionary(kv => ("c", kv.Key), kv => kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var similar = games
                    .Where(other => other.id != game.id)
                    .Select(other =>
                    {
                        var (mechanics, categories) = linkSets[other.id];
                        var otherTags = mechanics.ToDictionary(kv => ("m", kv.Key), kv => kv.Value)
                            .Concat(categories.ToDictionary(kv => ("c", kv.Key), kv => kv.Value))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
                        double score = WeightedJaccard(targetTags, otherTags, TagIdf);
                        var sharedMechanics = targetMechanics.Where(kv => mechanics.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                        var sharedCategories = targetCategories.Where(kv => categories.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                        return (other, score, sharedMechanics, sharedCategories);
                    })
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .Take(3)
                    .Select(x => new SimilarGameDto(x.other.id, x.other.Name, x.other.AverageRating, x.other.ImageDetails?.ImageVersion, x.sharedMechanics, x.sharedCategories))
                    .ToList();

                newIndex[game.id] = similar;
            }

            // Persist the freshly computed result onto each game's ExtraDetails so the
            // compare does not have to re-run on the next startup. Every game considered
            // here (all non-expansions with an ExtraDetails row) gets a non-null value —
            // "[]" when there are no matches — so that a leftover NULL unambiguously means
            // "never computed" (e.g. a game inserted after a rebuild that failed midway).
            // HasUncomputedGamesAsync relies on that distinction.
            foreach (var game in games)
            {
                if (game.ExtraDetails == null) continue;
                game.ExtraDetails.SimilarGamesJson =
                    newIndex.TryGetValue(game.id, out var list) && list.Count > 0
                        ? JsonSerializer.Serialize(list)
                        : "[]";
            }
            await db.SaveChangesAsync();

            _index = newIndex;
        }

        private static (Dictionary<int, string> mechanics, Dictionary<int, string> categories) ParseLinkSets(string? linksJson)
        {
            var mechanics = new Dictionary<int, string>();
            var categories = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(linksJson)) return (mechanics, categories);
            try
            {
                using var doc = JsonDocument.Parse(linksJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (!el.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var linkId)) continue;
                    var name = el.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                    if (type == "boardgamemechanic") mechanics[linkId] = name;
                    else if (type == "boardgamecategory") categories[linkId] = name;
                }
            }
            catch { }
            return (mechanics, categories);
        }

        private static double WeightedJaccard(Dictionary<(string type, int id), string> a, Dictionary<(string type, int id), string> b, Func<(string type, int id), double> idf)
        {
            if (a.Count == 0 && b.Count == 0) return 0;
            double intersection = 0, union = 0;
            foreach (var id in a.Keys)
            {
                double w = idf(id);
                union += w;
                if (b.ContainsKey(id)) intersection += w;
            }
            foreach (var id in b.Keys)
            {
                if (!a.ContainsKey(id)) union += idf(id);
            }
            return union == 0 ? 0 : intersection / union;
        }
    }
}
