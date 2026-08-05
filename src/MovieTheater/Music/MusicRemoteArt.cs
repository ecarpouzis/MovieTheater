using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Music
{
    /// <summary>
    /// The remote album-art lookup (music-plan.md §2.5): MusicBrainz release search → Cover Art
    /// Archive front image, falling back to the iTunes Search API.
    ///
    /// <para>Lifted out of <see cref="MusicArtCommand"/> so the admin backfill endpoint can share one
    /// implementation. That split is load-bearing rather than tidiness: album art has to be written by
    /// the process that owns the live images mount, and a dev-box CLI run does not — exactly the
    /// reason <c>/API/Admin/IngestReview/BackfillPosters</c> exists for movie posters. Two copies of
    /// this logic would drift on the throttle or the User-Agent, both of which are conditions of use
    /// rather than preferences.</para>
    /// </summary>
    public static class MusicRemoteArt
    {
        /// <summary>MusicBrainz asks for ≤1 request/second and a contact-bearing User-Agent; both are
        /// conditions of use, not suggestions. Callers must space their calls by this much.</summary>
        public const int MusicBrainzThrottleMs = 1100;

        private const string RemoteUserAgent = "MovieTheater-music-art/1.0 (private home media library)";

        /// <summary>Process-wide gate for remote lookups. Both in-process callers (the lazy image route
        /// and the admin bulk warm) take THIS one, so the rate limit holds no matter how they interleave
        /// — two independent gates would silently double the request rate.
        ///
        /// <para>The lazy route takes it with a zero timeout (busy ⇒ show a placeholder, never queue a
        /// web request behind someone else's lookup); the bulk warm waits its turn.</para></summary>
        public static readonly SemaphoreSlim Gate = new(1, 1);

        private static DateTime lastCallUtc = DateTime.MinValue;

        /// <summary>Await immediately before a lookup, holding <see cref="Gate"/>, to keep consecutive
        /// MusicBrainz calls ≥1s apart.</summary>
        public static async Task SpaceCallAsync()
        {
            var wait = MusicBrainzThrottleMs - (int)(DateTime.UtcNow - lastCallUtc).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            lastCallUtc = DateTime.UtcNow;
        }

        /// <summary>A client carrying the User-Agent MusicBrainz requires — they 403 anonymous callers.</summary>
        public static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(RemoteUserAgent);
            return http;
        }

        /// <summary>Best available front cover, or null when neither source has one. Any network hiccup
        /// is a miss, never a throw — the caller stamps the negative cache either way, and a genuinely
        /// transient failure costs one album until someone clears its ArtCheckedUtc.</summary>
        public static async Task<byte[]?> FetchAsync(HttpClient http, string artist, string album)
        {
            foreach (var mbid in await MusicBrainzReleaseIdsAsync(http, artist, album))
            {
                var bytes = await TryGetAsync(http, $"https://coverartarchive.org/release/{mbid}/front-500");
                if (bytes != null) return bytes;
            }
            return await ItunesArtworkAsync(http, artist, album);
        }

        private static async Task<List<string>> MusicBrainzReleaseIdsAsync(HttpClient http, string artist, string album)
        {
            var ids = new List<string>();
            var query = Uri.EscapeDataString($"artist:\"{Sanitize(artist)}\" AND release:\"{Sanitize(album)}\"");
            var json = await TryGetStringAsync(http, $"https://musicbrainz.org/ws/2/release/?query={query}&fmt=json&limit=5");
            if (json == null) return ids;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("releases", out var releases)) return ids;
                foreach (var release in releases.EnumerateArray())
                    if (release.TryGetProperty("id", out var id) && id.GetString() is string s)
                        ids.Add(s);
            }
            catch { /* malformed response = miss */ }
            return ids;
        }

        private static async Task<byte[]?> ItunesArtworkAsync(HttpClient http, string artist, string album)
        {
            var term = Uri.EscapeDataString($"{artist} {album}");
            var json = await TryGetStringAsync(http, $"https://itunes.apple.com/search?term={term}&entity=album&limit=1");
            if (json == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("artworkUrl100", out var art)) continue;
                    var url = art.GetString();
                    if (url == null) continue;
                    // iTunes serves any size by rewriting the dimension segment of the URL.
                    return await TryGetAsync(http, url.Replace("100x100", "600x600"));
                }
            }
            catch { /* malformed response = miss */ }
            return null;
        }

        /// <summary>Lucene special characters in an album/artist name would otherwise break the
        /// MusicBrainz query (or silently match nothing).</summary>
        private static string Sanitize(string s) =>
            new string(s.Where(c => !"\\+-!(){}[]^\"~*?:/".Contains(c)).ToArray()).Trim();

        private static async Task<byte[]?> TryGetAsync(HttpClient http, string url)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch { return null; }
        }

        private static async Task<string?> TryGetStringAsync(HttpClient http, string url)
        {
            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }
    }
}
