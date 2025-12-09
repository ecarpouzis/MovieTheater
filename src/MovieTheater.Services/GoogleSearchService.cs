using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;

namespace MovieTheater.Services
{
    public class GoogleSearchService
    {
        public async Task<string> FindImdbIdFromMovieName(string movieName)
        {
            if (string.IsNullOrWhiteSpace(movieName))
            {
                return string.Empty;
            }

            // First remove any quality (720p) designation from the final portion of the folder name
            string[] parts = movieName.Split(' ');
            string qual = parts.Last().ToLowerInvariant();
            if (qual.Length > 0 && qual.Last() == 'p')
            {
                // Remove only the last occurrence (token) to avoid accidental removals elsewhere
                int lastIndex = movieName.LastIndexOf(qual, StringComparison.OrdinalIgnoreCase);
                if (lastIndex >= 0)
                {
                    movieName = movieName.Remove(lastIndex, qual.Length).Trim();
                }
            }

            // Next remove any tags between square brackets in movie name
            movieName = Regex.Replace(movieName, @"\[[^\]]*?\]", string.Empty);
            movieName = movieName.Trim();

            try
            {
                using HttpClient httpClient = new HttpClient();
                // Use a common browser user-agent to improve chance of acceptance by endpoints
                string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.102 Safari/537.36";
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

                string encodedQuery = HttpUtility.UrlEncode(movieName);
                string requestUri = "https://api.imdbapi.dev/search/titles?query=" + encodedQuery;

                HttpResponseMessage response = await httpClient.GetAsync(requestUri).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return string.Empty;
                }

                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("titles", out JsonElement results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                {
                    JsonElement first = results[0];
                    if (first.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                    {
                        string id = idElement.GetString() ?? string.Empty;
                        return id;
                    }
                }

                return string.Empty;
            }
            catch
            {
                // On any error (network, parse, etc.) return empty string to indicate not found.
                return string.Empty;
            }
        }
    }
}
