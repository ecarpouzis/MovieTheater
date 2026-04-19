using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using MovieTheater.Db;

namespace MovieTheater.Services.Bgg
{
    public record BoardgameBggResult(Boardgame Boardgame, string? ImageUrl, string? ThumbnailUrl);

    public class BoardGameGeekApi
    {
        // Use boardgamegeek.com WITHOUT www prefix per BGG API documentation
        private const string BaseUrl = "https://boardgamegeek.com";

        private readonly HttpClient httpClient;
        private readonly BggApiOptions options;
        private readonly SemaphoreSlim rateLimitSemaphore = new(1, 1);
        private DateTime lastRequestTime = DateTime.MinValue;

        public BoardGameGeekApi(HttpClient httpClient, IOptions<BggApiOptions> options)
        {
            this.httpClient = httpClient;
            this.options = options.Value;
        }

        public async Task<BoardgameBggResult?> GetBoardgameByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var encoded = Uri.EscapeDataString(title.Trim());
            var xml = await SendBggGetAsync($"/xmlapi2/search?query={encoded}&type=boardgame&exact=1");
            var doc = XDocument.Parse(xml);

            var item = doc.Root?.Elements("item")
                .FirstOrDefault(x => int.TryParse((string?)x.Attribute("id"), out _));

            if (item == null)
            {
                xml = await SendBggGetAsync($"/xmlapi2/search?query={encoded}&type=boardgame");
                doc = XDocument.Parse(xml);
                item = doc.Root?.Elements("item")
                    .FirstOrDefault(x => int.TryParse((string?)x.Attribute("id"), out _));
            }

            if (item == null)
            {
                return null;
            }

            var idText = (string?)item.Attribute("id");
            if (!int.TryParse(idText, out var bggThingId))
            {
                return null;
            }

            return await GetBoardgame(bggThingId);
        }

        public async Task<BoardgameBggResult?> GetBoardgame(int bggThingId)
        {
            var xml = await SendBggGetAsync($"/xmlapi2/thing?id={bggThingId}&type=boardgame&stats=1&versions=1&videos=1&marketplace=1");
            var parsed = ParseBoardgame(xml, bggThingId);

            if (parsed == null)
            {
                // Fallback: some BGG entries are not returned with type=boardgame.
                xml = await SendBggGetAsync($"/xmlapi2/thing?id={bggThingId}&stats=1&versions=1&videos=1&marketplace=1");
                parsed = ParseBoardgame(xml, bggThingId);
            }

            if (parsed == null)
            {
                return null;
            }

            parsed.Boardgame.RawXml = xml;
            parsed.Boardgame.LastSyncedUtc = DateTime.UtcNow;
            return parsed;
        }

        private async Task<string> SendBggGetAsync(string pathAndQuery)
        {
            await rateLimitSemaphore.WaitAsync();
            try
            {
                // Enforce rate limiting - wait if needed to respect BGG's guidelines
                var timeSinceLastRequest = DateTime.UtcNow - lastRequestTime;
                var requiredDelay = TimeSpan.FromMilliseconds(options.RateLimitDelayMs);
                if (timeSinceLastRequest < requiredDelay)
                {
                    var waitTime = requiredDelay - timeSinceLastRequest;
                    await Task.Delay(waitTime);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{BaseUrl}{pathAndQuery}"));

                // Use Bearer token authentication per BGG API documentation
                if (!string.IsNullOrWhiteSpace(options.ApiToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken.Trim());
                }

                var response = await httpClient.SendAsync(request);
                lastRequestTime = DateTime.UtcNow;

                // Handle BGG-specific status codes
                if (response.StatusCode == HttpStatusCode.Accepted) // 202 - queued, need to retry
                {
                    // BGG returns 202 when request is queued; retry after delay
                    await Task.Delay(options.RateLimitDelayMs);
                    return await SendBggGetAsync(pathAndQuery);
                }

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable || 
                    response.StatusCode == HttpStatusCode.InternalServerError) // 503 or 500 - too busy
                {
                    throw new HttpRequestException($"BGG server too busy. Status: {(int)response.StatusCode}. Try again later.");
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            finally
            {
                rateLimitSemaphore.Release();
            }
        }

        private static BoardgameBggResult? ParseBoardgame(string rawXml, int bggThingId)
        {
            var doc = XDocument.Parse(rawXml);
            var item = doc.Root?.Element("item");
            if (item == null)
            {
                return null;
            }

            var names = item.Elements("name")
                .Select(x => new
                {
                    type = (string?)x.Attribute("type"),
                    value = (string?)x.Attribute("value"),
                    sortIndex = ParseInt((string?)x.Attribute("sortindex"))
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.value))
                .ToList();

            var primaryName = names.FirstOrDefault(x => string.Equals(x.type, "primary", StringComparison.OrdinalIgnoreCase))?.value
                ?? names.FirstOrDefault()?.value;

            var alternateNames = names
                .Where(x => !string.Equals(x.type, "primary", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            var stats = item.Element("statistics")?.Element("ratings");

            var links = item.Elements("link")
                .Select(x => new
                {
                    type = (string?)x.Attribute("type"),
                    id = ParseInt((string?)x.Attribute("id")),
                    value = (string?)x.Attribute("value"),
                    inbound = ParseBool((string?)x.Attribute("inbound"))
                })
                .ToList();

            var polls = item.Elements("poll")
                .Select(x => new
                {
                    name = (string?)x.Attribute("name"),
                    title = (string?)x.Attribute("title"),
                    totalVotes = ParseInt((string?)x.Attribute("totalvotes")),
                    xml = x.ToString(SaveOptions.DisableFormatting)
                })
                .ToList();

            var videos = item.Element("videos")?.Elements("video")
                .Select(x => new
                {
                    id = ParseInt((string?)x.Attribute("id")),
                    title = (string?)x.Attribute("title"),
                    category = (string?)x.Attribute("category"),
                    language = (string?)x.Attribute("language"),
                    link = (string?)x.Attribute("link"),
                    username = (string?)x.Attribute("username"),
                    postDate = (string?)x.Attribute("postdate")
                })
                .ToList();

            var ranks = stats?.Element("ranks")?.Elements("rank")
                .Select(x => new
                {
                    type = (string?)x.Attribute("type"),
                    id = ParseInt((string?)x.Attribute("id")),
                    name = (string?)x.Attribute("name"),
                    friendlyName = (string?)x.Attribute("friendlyname"),
                    value = (string?)x.Attribute("value"),
                    bayesAverage = ParseDecimal((string?)x.Attribute("bayesaverage"))
                })
                .ToList();

            var boardgame = new Boardgame
            {
                BggThingId = bggThingId,
                ThingType = (string?)item.Attribute("type"),
                Name = string.IsNullOrWhiteSpace(primaryName) ? null : primaryName.Trim(),
                AlternateNamesJson = JsonSerializer.Serialize(alternateNames),
                YearPublished = ParseIntAttribute(item.Element("yearpublished"), "value"),
                MinPlayers = ParseIntAttribute(item.Element("minplayers"), "value"),
                MaxPlayers = ParseIntAttribute(item.Element("maxplayers"), "value"),
                PlayingTime = ParseIntAttribute(item.Element("playingtime"), "value"),
                MinPlayTime = ParseIntAttribute(item.Element("minplaytime"), "value"),
                MaxPlayTime = ParseIntAttribute(item.Element("maxplaytime"), "value"),
                MinAge = ParseIntAttribute(item.Element("minage"), "value"),
                Description = item.Element("description")?.Value,
                UsersRated = ParseIntAttribute(stats?.Element("usersrated"), "value"),
                AverageRating = ParseDecimalAttribute(stats?.Element("average"), "value"),
                BayesAverageRating = ParseDecimalAttribute(stats?.Element("bayesaverage"), "value"),
                StdDev = ParseDecimalAttribute(stats?.Element("stddev"), "value"),
                Median = ParseDecimalAttribute(stats?.Element("median"), "value"),
                Owned = ParseIntAttribute(stats?.Element("owned"), "value"),
                Trading = ParseIntAttribute(stats?.Element("trading"), "value"),
                Wanting = ParseIntAttribute(stats?.Element("wanting"), "value"),
                Wishing = ParseIntAttribute(stats?.Element("wishing"), "value"),
                NumComments = ParseIntAttribute(stats?.Element("numcomments"), "value"),
                NumWeights = ParseIntAttribute(stats?.Element("numweights"), "value"),
                AverageWeight = ParseDecimalAttribute(stats?.Element("averageweight"), "value"),
                RanksJson = JsonSerializer.Serialize(ranks),
                LinksJson = JsonSerializer.Serialize(links),
                PollsJson = JsonSerializer.Serialize(polls),
                VersionsXml = item.Element("versions")?.ToString(SaveOptions.DisableFormatting),
                VideosJson = JsonSerializer.Serialize(videos),
                MarketplaceXml = item.Element("marketplacelistings")?.ToString(SaveOptions.DisableFormatting)
            };

            var imageUrl = NullIfWhiteSpace(item.Element("image")?.Value);
            var thumbnailUrl = NullIfWhiteSpace(item.Element("thumbnail")?.Value);
            return new BoardgameBggResult(boardgame, imageUrl, thumbnailUrl);
        }

        private static int? ParseIntAttribute(XElement? element, string attributeName)
        {
            return ParseInt((string?)element?.Attribute(attributeName));
        }

        private static decimal? ParseDecimalAttribute(XElement? element, string attributeName)
        {
            return ParseDecimal((string?)element?.Attribute(attributeName));
        }

        private static int? ParseInt(string? value)
        {
            if (int.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static decimal? ParseDecimal(string? value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool? ParseBool(string? value)
        {
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
