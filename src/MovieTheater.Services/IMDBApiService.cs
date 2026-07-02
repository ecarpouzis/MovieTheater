using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;

namespace MovieTheater.Services
{
    public class IMDBApiService
    {
        private readonly HttpClient httpClient;

        // Injected as a typed client (IHttpClientFactory) so it reuses pooled connections instead of
        // spinning up — and leaking — a new HttpClient per lookup (the classic socket-exhaustion trap).
        public IMDBApiService(HttpClient httpClient)
        {
            this.httpClient = httpClient;
        }

        public async Task<string> FindImdbIdFromMovieName(string movieName)
        {
            ServicesUtil.CleanTitle(movieName);

            try
            {
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
