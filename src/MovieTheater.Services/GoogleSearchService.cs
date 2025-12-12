using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text.Json;

namespace MovieTheater.Services
{
    // Uses Google Custom Search JSON API (requires API key and Search Engine ID configured).
    // Configure keys in appsettings.json under "GoogleCustomSearch:ApiKey" and "GoogleCustomSearch:SearchEngineId".
    public class GoogleSearchService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string? _apiKey;
        private readonly string? _searchEngineId;
        private static readonly Regex ImdbRegex = new(@"https?://(?:www\.)?imdb\.com/title/(tt\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public GoogleSearchService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _apiKey = configuration["GoogleCustomSearch:ApiKey"];
            _searchEngineId = configuration["GoogleCustomSearch:SearchEngineId"];
        }

        public async Task<string?> FindImdbIdFromMovieName(string movieName)
        {
            if (string.IsNullOrWhiteSpace(movieName))
                return null;

            // sanitize similar to previous logic
            var parts = movieName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var qual = parts.LastOrDefault()?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(qual) && qual.EndsWith('p') && qual[..^1].All(char.IsDigit))
            {
                movieName = string.Join(' ', parts.Take(parts.Length - 1));
            }

            movieName = Regex.Replace(movieName, "\\[.*?\\]", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(movieName))
                return null;

            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_searchEngineId))
                throw new InvalidOperationException("Google Custom Search API key or Search Engine ID is not configured. Set GoogleCustomSearch:ApiKey and GoogleCustomSearch:SearchEngineId.");

            var client = _httpClientFactory.CreateClient();

            // Build request URL
            var q = HttpUtility.UrlEncode("site:imdb.com " + movieName);
            var requestUrl = $"https://www.googleapis.com/customsearch/v1?key={_apiKey}&cx={_searchEngineId}&q={q}&num=5";

            using var resp = await client.GetAsync(requestUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("link", out var linkProp) && linkProp.ValueKind == JsonValueKind.String)
                {
                    var link = linkProp.GetString()!;
                    var m = ImdbRegex.Match(link);
                    if (m.Success && m.Groups.Count > 1)
                        return m.Groups[1].Value;
                }

                // Some results place URL inside pagemap or displayLink; try 'formattedUrl' or 'displayLink' fallback
                if (item.TryGetProperty("formattedUrl", out var formatted) && formatted.ValueKind == JsonValueKind.String)
                {
                    var formattedUrl = formatted.GetString()!;
                    var m2 = ImdbRegex.Match(formattedUrl);
                    if (m2.Success && m2.Groups.Count > 1)
                        return m2.Groups[1].Value;
                }
            }

            return null;
        }
    }
}