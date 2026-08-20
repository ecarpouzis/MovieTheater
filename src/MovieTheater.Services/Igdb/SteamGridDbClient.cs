using System;
using System.Globalization;
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
                if (!NameMatches(want, Normalize(name))) continue;
                gameId = g.GetProperty("id").GetInt64();
                break;
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

        /// <summary>Does a SteamGridDB search result name the game we asked for? Exact on the normalized
        /// names, or a prefix relationship where the shorter name is at least <see cref="MinPrefixRatio"/>
        /// of the longer — which is what keeps "Sonic Adventure" ⇄ "Sonic Adventure DX" while rejecting the
        /// short-prefix false matches the ratio-free gate let through.
        ///
        /// <para>Those were not hypothetical. A backfill of 20 coverless cards produced 6 covers and TWO were
        /// wrong, both from this rule: our "Super Masters!" (Intellivision golf) matched a SteamGridDB game
        /// literally named "Super", and "Rack + Roll" matched a visual novel named "Rack" — because a bare
        /// <c>want.StartsWith(got)</c> accepts ANY 4-character prefix. At the scale of a full backfill that
        /// bakes hundreds of confidently-wrong covers onto a shared mount we cannot delete from.</para></summary>
        public static bool NameMatches(string want, string got)
        {
            if (want.Length == 0 || got.Length == 0) return false;
            if (got == want) return true;
            if (got.Length < 4 || want.Length < 4) return false;
            if (!got.StartsWith(want, StringComparison.Ordinal) &&
                !want.StartsWith(got, StringComparison.Ordinal)) return false;
            var (min, max) = got.Length < want.Length ? (got.Length, want.Length) : (want.Length, got.Length);
            return min * 100 >= max * MinPrefixRatio;
        }

        /// <summary>How much of the longer name a prefix match must cover, in percent. 70 keeps the real
        /// edition/subtitle cases ("Sonic Adventure" vs "…DX", 14/16 = 87%) and rejects the false ones
        /// ("Super" vs "Super Masters", 5/12 = 41%; "Rack" vs "Rack Roll", 4/8 = 50%).</summary>
        private const int MinPrefixRatio = 70;

        // Diacritics are folded (FormD + drop non-spacing marks) because SteamGridDB stores the real,
        // accented name ("Pokémon Kart 64") while our catalog follows the No-Intro convention and spells it
        // "Pokemon" — without the fold the gate rejects an exact match and the card falls through to the
        // web-image last resort.
        public static string Normalize(string sIn)
        {
            var sb = new StringBuilder(sIn.Length);
            int depth = 0;
            foreach (var chRaw in sIn.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(chRaw) == UnicodeCategory.NonSpacingMark) continue;
                var ch = chRaw;
                if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') { if (depth > 0) depth--; }
                else if (depth == 0 && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}
