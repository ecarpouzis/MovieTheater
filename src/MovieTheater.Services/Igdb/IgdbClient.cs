using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Services.Igdb
{
    /// <summary>
    /// Thin IGDB (Internet Game Database) client over the Twitch OAuth client-credentials flow. IGDB is our
    /// second source for game-card review scores (<c>total_rating</c>) and for box art (covers) that
    /// libretro-thumbnails lacks or mis-formats. Caching the results locally is permitted by IGDB's terms.
    ///
    /// <para>One game lookup returns BOTH the cover image id and the rating, so a single pass serves art-fill,
    /// outlier-replacement, and scores. Matching is gated by a normalized-name check so a fuzzy IGDB search
    /// hit can't silently attach the wrong game's cover/score.</para>
    /// </summary>
    public sealed class IgdbClient
    {
        private readonly HttpClient http;
        private readonly string clientId;
        private readonly string clientSecret;
        private readonly SemaphoreSlim tokenGate = new(1, 1);
        private string? token;
        private DateTime tokenExpiresUtc;

        public IgdbClient(HttpClient http, string clientId, string clientSecret)
        {
            this.http = http;
            this.clientId = clientId;
            this.clientSecret = clientSecret;
        }

        public static bool IsConfigured(MovieTheaterConfiguration cfg) =>
            !string.IsNullOrWhiteSpace(cfg.IgdbClientId) && !string.IsNullOrWhiteSpace(cfg.IgdbClientSecret);

        public sealed record IgdbGame(long Id, string Name, string? CoverImageId, double? TotalRating,
            int? TotalRatingCount, IReadOnlyList<int> PlatformIds, int? FirstReleaseYear,
            string? Genres, string? Themes, string? GameModes, string? Summary,
            string? Developer, string? Publisher, int? OfflineMaxPlayers, string? EsrbRating);

        /// <summary>t_cover_big ≈ 264×374 (2× via _2x). Bigger than our 300px thumbnail target, so it
        /// downscales cleanly to a uniform card.</summary>
        public static string CoverUrl(string imageId, bool big2x = true) =>
            $"https://images.igdb.com/igdb/image/upload/t_cover_big{(big2x ? "_2x" : "")}/{imageId}.jpg";

        /// <summary>The cover image id for an already-resolved IGDB game id (the enrichment stored the id).
        /// Lets the on-demand box-art route fetch a cover without re-searching by title.</summary>
        public async Task<string?> CoverImageIdAsync(long igdbId, CancellationToken ct = default)
        {
            var arr = await QueryAsync("games", $"fields cover.image_id; where id = {igdbId};", ct);
            foreach (var g in arr.EnumerateArray())
                if (g.TryGetProperty("cover", out var c) && c.TryGetProperty("image_id", out var ci)) return ci.GetString();
            return null;
        }

        private async Task<string> GetTokenAsync(CancellationToken ct)
        {
            if (token != null && DateTime.UtcNow < tokenExpiresUtc) return token;
            await tokenGate.WaitAsync(ct);
            try
            {
                if (token != null && DateTime.UtcNow < tokenExpiresUtc) return token;
                var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(clientId)}" +
                          $"&client_secret={Uri.EscapeDataString(clientSecret)}&grant_type=client_credentials";
                using var resp = await http.PostAsync(url, null, ct);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                token = doc.RootElement.GetProperty("access_token").GetString();
                var secs = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 3600;
                tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, secs - 300)); // refresh 5 min early
                return token!;
            }
            finally { tokenGate.Release(); }
        }

        /// <summary>Run an apicalypse query against an IGDB endpoint (e.g. "games"), returning the raw JSON
        /// array. Retries once on 401 (token rotated) and respects IGDB's 4 req/s via the caller's pacing.</summary>
        public async Task<JsonElement> QueryAsync(string endpoint, string apicalypse, CancellationToken ct = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                var tok = await GetTokenAsync(ct);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.igdb.com/v4/{endpoint}");
                req.Headers.Add("Client-ID", clientId);
                req.Headers.Add("Authorization", "Bearer " + tok);
                req.Content = new StringContent(apicalypse, Encoding.UTF8, "text/plain");
                using var resp = await http.SendAsync(req, ct);
                if ((int)resp.StatusCode == 401 && attempt == 0) { token = null; continue; } // force re-auth
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                return doc.RootElement.Clone();
            }
        }

        /// <summary>Resolve a card title (optionally biased to a platform) to the best IGDB game, or null if no
        /// candidate's name is a confident match. Prefers a candidate on the wanted platform, with a cover, and
        /// the highest rating-count. The normalized-name gate blocks a fuzzy search from attaching wrong art.</summary>
        public async Task<IgdbGame?> ResolveGameAsync(string title, int? platformId, CancellationToken ct = default)
        {
            title = DeInvertArticle(title);   // No-Intro "Legend of Zelda, The" → "The Legend of Zelda" for IGDB
            var esc = title.Replace("\"", " ").Trim();
            if (esc.Length == 0) return null;
            // Single-pass: fetch the whole curated field set for the top candidates in ONE request.
            var q = $"search \"{esc}\"; fields name,total_rating,total_rating_count,cover.image_id,platforms," +
                    $"first_release_date,summary,genres.name,themes.name,game_modes.name," +
                    $"involved_companies.company.name,involved_companies.developer,involved_companies.publisher," +
                    $"multiplayer_modes.offlinemax,age_ratings.category,age_ratings.rating; limit 10;";
            var arr = await QueryAsync("games", q, ct);

            var wantNorm = NormalizeName(title);
            IgdbGame? best = null; int bestScore = int.MinValue;
            foreach (var g in arr.EnumerateArray())
            {
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var cover = g.TryGetProperty("cover", out var c) && c.TryGetProperty("image_id", out var ci) ? ci.GetString() : null;
                var rating = g.TryGetProperty("total_rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null;
                var rcount = g.TryGetProperty("total_rating_count", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : (int?)null;
                var plats = g.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => x.GetInt32()).ToList() : new List<int>();
                int? year = g.TryGetProperty("first_release_date", out var d) && d.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()).Year : (int?)null;

                // Name gate: exact normalized match, or one side a prefix of the other (subtitle/edition drift).
                var gotNorm = NormalizeName(name);
                bool nameOk = gotNorm == wantNorm ||
                              (gotNorm.Length >= 4 && wantNorm.Length >= 4 && (gotNorm.StartsWith(wantNorm) || wantNorm.StartsWith(gotNorm)));
                if (!nameOk) continue;

                int score = 0;
                if (gotNorm == wantNorm) score += 100;                      // exact name wins
                if (platformId is int pid && plats.Contains(pid)) score += 40; // right platform
                if (cover != null) score += 10;
                score += Math.Min(20, rcount ?? 0);                          // popularity tiebreak
                if (score <= bestScore) continue;
                bestScore = score;
                best = new IgdbGame(g.GetProperty("id").GetInt64(), name, cover, rating, rcount, plats, year,
                    Genres: JoinNames(g, "genres"), Themes: JoinNames(g, "themes"), GameModes: JoinNames(g, "game_modes"),
                    Summary: Cap(g.TryGetProperty("summary", out var s) ? s.GetString() : null, 1000),
                    Developer: Company(g, developer: true), Publisher: Company(g, developer: false),
                    OfflineMaxPlayers: OfflineMax(g), EsrbRating: Esrb(g));
            }
            return best;
        }

        // Comma-join the "name" of each object in an expanded array field (genres/themes/game_modes).
        private static string? JoinNames(JsonElement game, string field)
        {
            if (!game.TryGetProperty(field, out var a) || a.ValueKind != JsonValueKind.Array) return null;
            var names = a.EnumerateArray()
                .Select(x => x.TryGetProperty("name", out var nm) ? nm.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        // First involved company flagged developer (or publisher). IGDB flags are per involvement.
        private static string? Company(JsonElement game, bool developer)
        {
            if (!game.TryGetProperty("involved_companies", out var a) || a.ValueKind != JsonValueKind.Array) return null;
            foreach (var ic in a.EnumerateArray())
            {
                var flag = ic.TryGetProperty(developer ? "developer" : "publisher", out var f) && f.ValueKind == JsonValueKind.True;
                if (!flag) continue;
                if (ic.TryGetProperty("company", out var co) && co.TryGetProperty("name", out var nm))
                    return nm.GetString();
            }
            return null;
        }

        // Max offline local players across the game's multiplayer_modes entries.
        private static int? OfflineMax(JsonElement game)
        {
            if (!game.TryGetProperty("multiplayer_modes", out var a) || a.ValueKind != JsonValueKind.Array) return null;
            int max = 0;
            foreach (var m in a.EnumerateArray())
                if (m.TryGetProperty("offlinemax", out var om) && om.ValueKind == JsonValueKind.Number)
                    max = Math.Max(max, om.GetInt32());
            return max > 0 ? max : null;
        }

        // ESRB rating from age_ratings (category 1 = ESRB); enum → letter grade.
        private static string? Esrb(JsonElement game)
        {
            if (!game.TryGetProperty("age_ratings", out var a) || a.ValueKind != JsonValueKind.Array) return null;
            foreach (var ar in a.EnumerateArray())
            {
                if (!(ar.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Number && cat.GetInt32() == 1)) continue;
                if (!(ar.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number)) continue;
                return rt.GetInt32() switch // IGDB ESRB enum
                { 6 => "RP", 7 => "EC", 8 => "E", 9 => "E10+", 10 => "T", 11 => "M", 12 => "AO", _ => null };
            }
            return null;
        }

        // Undo No-Intro/Redump article inversion so the name gate and search see natural word order.
        private static string DeInvertArticle(string t)
        {
            foreach (var art in new[] { "The", "A", "An" })
            {
                var suffix = ", " + art;
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return art + " " + t[..^suffix.Length].TrimEnd();
            }
            return t;
        }

        private static string? Cap(string? s, int max) =>
            string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);

        // Normalize a game name for the match gate: drop tags, lowercase alphanumerics only.
        private static string NormalizeName(string s)
        {
            var sb = new StringBuilder(s.Length);
            int depth = 0;
            foreach (var ch in s)
            {
                if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') { if (depth > 0) depth--; }
                else if (depth == 0 && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Our arcade system code → IGDB platform id, for disambiguating a title to the right game.
        /// Null = don't bias by platform (still resolves by name).</summary>
        public static int? PlatformId(string system) => system switch
        {
            "arcade" => 52, "neogeo" => 80,
            "nes" or "fds" => 18, "snes" => 19, "n64" => 4, "gc" => 21, "wii" => 5,
            "gb" => 33, "gbc" => 22, "gba" => 24,
            "genesis" => 29, "sms" => 64, "gg" => 35, "sg1000" => 84, "segacd" => 78, "sega32x" => 30,
            "ps1" => 7, "ps2" => 8, "psp" => 38,
            "dc" => 23, "pce" => 86, "ngpc" => 120, "wsc" => 123,
            "a2600" => 59, "a7800" => 60, "lynx" => 61, "vb" => 87,
            _ => null,
        };
    }
}
