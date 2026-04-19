using System.Text.Json;
using MovieTheater.Db;
using MovieTheater.Services.Google;

namespace MovieTheater.Services.Bgg
{
    public class BoardgameRulesService
    {
        private readonly BoardGameGeekApi bggApi;
        private readonly GoogleSearchService googleSearchService;

        public BoardgameRulesService(BoardGameGeekApi bggApi, GoogleSearchService googleSearchService)
        {
            this.bggApi = bggApi;
            this.googleSearchService = googleSearchService;
        }

        public async Task<(string? PdfCandidateUrl, List<string> VideoUrls)> DiscoverAsync(Boardgame game)
        {
            var pdfTask = FindRulesPdfAsync(game);
            var videoTask = FindHowToPlayVideosAsync(game);
            await Task.WhenAll(pdfTask, videoTask);
            return (pdfTask.Result, videoTask.Result);
        }

        private async Task<string?> FindRulesPdfAsync(Boardgame game)
        {
            var bggUrl = await FindPdfFromBggFilesAsync(game.BggThingId);
            if (bggUrl != null) return bggUrl;

            if (!string.IsNullOrWhiteSpace(game.Name))
            {
                var query = $"\"{game.Name}\"{(game.YearPublished.HasValue ? $" \"{game.YearPublished}\"" : "")} rulebook filetype:pdf";
                return await googleSearchService.SearchForPdfUrl(query);
            }

            return null;
        }

        private async Task<string?> FindPdfFromBggFilesAsync(int bggThingId)
        {
            try
            {
                var xml = await bggApi.GetThingFilesXmlAsync(bggThingId);
                if (string.IsNullOrWhiteSpace(xml)) return null;

                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var files = doc.Root?.Element("item")?.Elements("file") ?? [];

                foreach (var file in files)
                {
                    var category = ((string?)file.Attribute("category"))?.ToLowerInvariant() ?? "";
                    var name = ((string?)file.Attribute("name"))?.ToLowerInvariant() ?? "";
                    var href = (string?)file.Attribute("href");

                    if (string.IsNullOrWhiteSpace(href)) continue;

                    if (category.Contains("rule") || name.Contains("rule") || name.Contains("manual") || name.Contains("instructions"))
                        return href;
                }
            }
            catch
            {
                // BGG files API may not be available; fall through to web search
            }

            return null;
        }

        private async Task<List<string>> FindHowToPlayVideosAsync(Boardgame game)
        {
            var urls = new List<string>();

            if (!string.IsNullOrWhiteSpace(game.VideosJson))
                urls.AddRange(FindVideosInBggJson(game.VideosJson));

            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(game.Name))
            {
                var query = $"\"{game.Name}\" \"how to play\" site:youtube.com";
                urls.AddRange(await googleSearchService.SearchForUrls(query));
            }

            return urls.Distinct().ToList();
        }

        private static List<string> FindVideosInBggJson(string videosJson)
        {
            var result = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(videosJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                foreach (var video in doc.RootElement.EnumerateArray())
                {
                    var category = video.TryGetProperty("category", out var cat) ? cat.GetString()?.ToLowerInvariant() : null;
                    var title = video.TryGetProperty("title", out var t) ? t.GetString()?.ToLowerInvariant() : null;
                    var link = video.TryGetProperty("link", out var l) ? l.GetString() : null;

                    if (string.IsNullOrWhiteSpace(link)) continue;

                    var isHowToPlay = category == "instructional"
                        || (title != null && (title.Contains("how to play") || title.Contains("learn to play") || title.Contains("how to set up")));

                    if (isHowToPlay) result.Add(link);
                }
            }
            catch { }
            return result;
        }
    }
}
