using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace MovieTheater.Services.Tmdb
{
    public class TmdbApi
    {
        private HttpClient _httpClient;
        private TmdbApiOptions _options;

        //Because we use IOptions we can hotswap options at runtime and similar benefits
        //it is the proper way to configure an httpclient in .net
        
        //AspNetCore typically includes this by default, but as some of my code is in a standalone DLL it includes no packages by default.
        //Because I want to use IOptions, I install Microsoft.Extensions.Options

        //This occurs wherever services.configure is called (MovieAPI.Startup) 
        public TmdbApi(HttpClient httpClient, IOptions<TmdbApiOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<MovieDto> GetMovie(string imdbID)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/find/{imdbID}?api_key={_options.ApiKey}&external_source=imdb_id", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root =  JsonConvert.DeserializeObject<Root>(responseContent);
            var movie = root.MovieResults.Single();
            return movie;
        }


        public async Task<MovieDto> GetMovieByName(string name)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/find/{name}?api_key={_options.ApiKey}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root = JsonConvert.DeserializeObject<Root>(responseContent);
            var movie = root.MovieResults.Single();
            return movie;
        }

        /// <summary>
        /// Resolves an IMDB id to its TMDB movie via <c>/find</c>, returning null instead of throwing
        /// when there is no movie match (e.g. the id is a TV series, or TMDB has no record). Used by
        /// the enrichment backfill, where a miss should skip the row, not abort the run.
        /// </summary>
        public async Task<MovieDto?> TryGetMovie(string imdbID)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/find/{imdbID}?api_key={_options.ApiKey}&external_source=imdb_id", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            string responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                var root = JsonConvert.DeserializeObject<Root>(responseContent);
                return root?.MovieResults?.FirstOrDefault();
            }
            catch (JsonException)
            {
                // A malformed/unexpected field shouldn't abort the whole backfill — treat as no match.
                return null;
            }
        }

        /// <summary>
        /// Fetches the full TMDB movie detail (with embedded videos) for a known TMDB id — the source
        /// of the enrichment fields (<c>tagline</c>, <c>budget</c>, <c>revenue</c>, trailer, …) that the
        /// flat <c>/find</c> response omits. Returns null on a non-success status.
        /// </summary>
        public async Task<TmdbMovieDetailDto?> GetMovieDetail(int tmdbId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/movie/{tmdbId}?api_key={_options.ApiKey}&append_to_response=videos", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            string responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<TmdbMovieDetailDto>(responseContent);
        }
    }
}
