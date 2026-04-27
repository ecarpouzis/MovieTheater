using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Configuration;
using MovieTheater.Db;

namespace MovieTheater.Services.Google
{
    // Fetches YouTube video metadata (title, duration) and stores it directly on HowToPlayVideoEntry.
    // Per YouTube Developer Policies §4.D, cached data must be refreshed at least every 30 days.
    public class YouTubeService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string? _apiKey;

        public static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(30);

        private static readonly Regex VideoIdFromWatch = new(@"[?&]v=([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
        private static readonly Regex VideoIdFromShort = new(@"youtu\.be/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
        private static readonly Regex VideoIdFromEmbed = new(@"/embed/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);

        public YouTubeService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _apiKey = configuration["GoogleSearchApiKey"];
        }

        private static string? ExtractVideoId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var m = VideoIdFromWatch.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = VideoIdFromShort.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = VideoIdFromEmbed.Match(url);
            if (m.Success) return m.Groups[1].Value;
            return null;
        }

        // Updates entries in-place with title/duration for any that are missing metadata or stale.
        // Returns true if any entries were changed (caller should persist the boardgame).
        public async Task<bool> RefreshEntriesAsync(List<HowToPlayVideoEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return false;

            var cutoff = DateTime.UtcNow - CacheMaxAge;
            var byId = entries
                .Select(e => (Id: ExtractVideoId(e.Url), Entry: e))
                .Where(x => x.Id != null && (x.Entry.FetchedAtUtc == null || x.Entry.FetchedAtUtc.Value < cutoff))
                .GroupBy(x => x.Id!)
                .ToDictionary(g => g.Key, g => g.First().Entry);

            if (byId.Count == 0) return false;

            foreach (var batch in byId.Keys.Chunk(50))
                await FetchAndApplyBatchAsync(batch, byId);

            return true;
        }

        private async Task FetchAndApplyBatchAsync(IEnumerable<string> ids, Dictionary<string, HowToPlayVideoEntry> byId)
        {
            var idList = ids.ToList();
            var url = $"https://www.googleapis.com/youtube/v3/videos?id={HttpUtility.UrlEncode(string.Join(",", idList))}&part=snippet,contentDetails&key={_apiKey}";
            var client = _httpClientFactory.CreateClient();

            var now = DateTime.UtcNow;
            var returned = new HashSet<string>();

            using var resp = await client.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var videoId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(videoId) || !byId.TryGetValue(videoId, out var entry)) continue;

                        entry.Title = item.TryGetProperty("snippet", out var snippet) && snippet.TryGetProperty("title", out var titleProp)
                            ? titleProp.GetString() : null;
                        entry.Duration = item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("duration", out var durProp)
                            ? FormatDuration(durProp.GetString()) : null;
                        entry.FetchedAtUtc = now;
                        returned.Add(videoId);
                    }
                }
            }

            // Mark unavailable videos (private/deleted) so we don't retry for 30 days
            foreach (var id in idList.Where(id => !returned.Contains(id)))
            {
                if (byId.TryGetValue(id, out var entry))
                    entry.FetchedAtUtc = now;
            }
        }

        private static string? FormatDuration(string? iso)
        {
            if (iso == null) return null;
            try
            {
                var ts = System.Xml.XmlConvert.ToTimeSpan(iso);
                return ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
            }
            catch { return null; }
        }
    }
}
