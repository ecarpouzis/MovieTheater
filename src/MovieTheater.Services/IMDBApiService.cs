using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;

namespace MovieTheater.Services
{
    public class IMDBApiService
    {
        public async Task<string> FindImdbIdFromMovieName(string movieName)
        {
            ServicesUtil.CleanTitle(movieName);

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
