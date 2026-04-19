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

        public async Task<(string? PdfCandidateUrl, string? VideoUrl)> DiscoverAsync(Boardgame game)
        {
            var pdfTask = FindRulesPdfAsync(game);
            var videoTask = FindHowToPlayVideoAsync(game);
            await Task.WhenAll(pdfTask, videoTask);
            return (pdfTask.Result, videoTask.Result);
        }

        private async Task<string?> FindRulesPdfAsync(Boardgame game)
        {
            // Try BGG files API first
            var bggUrl = await FindPdfFromBggFilesAsync(game.BggThingId);
            if (bggUrl != null) return bggUrl;

            // Fallback to Google Search
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

        private async Task<string?> FindHowToPlayVideoAsync(Boardgame game)
        {
            // Check existing BGG VideosJson first
            if (!string.IsNullOrWhiteSpace(game.VideosJson))
            {
                var url = FindVideoInBggJson(game.VideosJson);
                if (url != null) return url;
            }

            // Fallback to Google Search targeting YouTube
            if (!string.IsNullOrWhiteSpace(game.Name))
            {
                var query = $"\"{game.Name}\" \"how to play\" site:youtube.com";
                return await googleSearchService.SearchForUrl(query);
            }

            return null;
        }

        private static string? FindVideoInBggJson(string videosJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(videosJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                foreach (var video in doc.RootElement.EnumerateArray())
                {
                    var category = video.TryGetProperty("category", out var cat) ? cat.GetString()?.ToLowerInvariant() : null;
                    var title = video.TryGetProperty("title", out var t) ? t.GetString()?.ToLowerInvariant() : null;
                    var link = video.TryGetProperty("link", out var l) ? l.GetString() : null;

                    if (string.IsNullOrWhiteSpace(link)) continue;

                    var isHowToPlay = category == "instructional"
                        || (title != null && (title.Contains("how to play") || title.Contains("learn to play") || title.Contains("how to set up")));

                    if (isHowToPlay) return link;
                }
            }
            catch { }

            return null;
        }
    }
}
