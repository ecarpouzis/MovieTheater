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
