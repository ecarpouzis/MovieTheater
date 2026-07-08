using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Services.Igdb
{
    /// <summary>
    /// SteamGridDB client — the community cover database used as the last step of the box-art cascade, to
    /// fill titles libretro-thumbnails and IGDB miss (homebrew, multicarts, obscure/digital games). Searches
    /// by title (with a normalized-name gate so a fuzzy hit can't attach the wrong game's cover) and returns
    /// a portrait "grid" (box-art-shaped) image URL.
    /// </summary>
    public sealed class SteamGridDbClient
    {
        private readonly HttpClient http;
        private readonly string apiKey;

        public SteamGridDbClient(HttpClient http, string apiKey)
        {
            this.http = http;
            this.apiKey = apiKey;
        }

        public static bool IsConfigured(MovieTheaterConfiguration cfg) =>
            !string.IsNullOrWhiteSpace(cfg.SteamGridDbApiKey);

        private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.steamgriddb.com/api/v2/" + path);
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }

        /// <summary>Best portrait cover URL for a title, or null. Picks the first searched game whose name
        /// matches (normalized exact or prefix), then its highest-scored portrait grid.</summary>
        public async Task<string?> FindCoverUrlAsync(string title, CancellationToken ct = default)
        {
            var term = title.Trim();
            if (term.Length == 0) return null;
            var search = await GetAsync("search/autocomplete/" + Uri.EscapeDataString(term), ct);
            if (search is not { } s || !s.TryGetProperty("data", out var games) || games.ValueKind != JsonValueKind.Array)
                return null;

            var want = Normalize(title);
            long? gameId = null;
            foreach (var g in games.EnumerateArray())
            {
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var got = Normalize(name);
                bool ok = got == want ||
                          (got.Length >= 4 && want.Length >= 4 && (got.StartsWith(want) || want.StartsWith(got)));
                if (ok) { gameId = g.GetProperty("id").GetInt64(); break; }
            }
            if (gameId is not long id) return null;

            // Portrait (box-art-shaped) grids, static, SFW, best-scored first (API default sort=score).
            var grids = await GetAsync($"grids/game/{id}?types=static&nsfw=false&dimensions=600x900,342x482,660x930", ct);
            if (grids is not { } gr || !gr.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty("url", out var u) && u.GetString() is string url && url.Length > 0)
                    return url;
            return null;
        }

        private static string Normalize(string sIn)
        {
            var sb = new StringBuilder(sIn.Length);
            int depth = 0;
            foreach (var ch in sIn)
            {
                if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') { if (depth > 0) depth--; }
                else if (depth == 0 && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}
