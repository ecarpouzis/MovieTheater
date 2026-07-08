using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Services.Igdb
{
    /// <summary>
    /// Final box-art cascade step: a Google image search (via a web-wide Custom Search Engine, NOT the
    /// imdb-locked one) that finds a cover for the last titles even SteamGridDB lacks — homebrew, one-off
    /// multicarts, obscure releases whose art exists on the open web but in no game database. Best-effort:
    /// returns the top image result for "&lt;title&gt; &lt;system&gt; box art"; it's the last resort, so a
    /// looser hit is acceptable where the alternative is a blank card.
    /// </summary>
    public sealed class GoogleImageCoverClient
    {
        private readonly HttpClient http;
        private readonly string apiKey;
        private readonly string cx;

        public GoogleImageCoverClient(HttpClient http, string apiKey, string cx)
        {
            this.http = http;
            this.apiKey = apiKey;
            this.cx = cx;
        }

        public static bool IsConfigured(MovieTheaterConfiguration cfg) =>
            !string.IsNullOrWhiteSpace(cfg.GoogleSearchApiKey) && !string.IsNullOrWhiteSpace(cfg.BoxArtImageSearchEngineId);

        public async Task<string?> FindBoxArtUrlAsync(string title, string systemHint, CancellationToken ct = default)
        {
            var q = Uri.EscapeDataString($"{title} {systemHint} box art cover".Trim());
            var url = $"https://www.googleapis.com/customsearch/v1?key={apiKey}&cx={cx}" +
                      $"&searchType=image&imgType=photo&num=5&q={q}";
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return null;
                // Prefer a portrait-ish image (box art is taller than wide) when the metadata is present.
                string first = null;
                foreach (var it in items.EnumerateArray())
                {
                    if (!it.TryGetProperty("link", out var l) || l.GetString() is not string link || link.Length == 0) continue;
                    first ??= link;
                    if (it.TryGetProperty("image", out var im)
                        && im.TryGetProperty("height", out var h) && im.TryGetProperty("width", out var w)
                        && h.ValueKind == JsonValueKind.Number && w.ValueKind == JsonValueKind.Number
                        && h.GetInt32() > w.GetInt32())
                        return link; // portrait — most likely a box/cover
                }
                return first;
            }
            catch { return null; }
        }
    }
}
