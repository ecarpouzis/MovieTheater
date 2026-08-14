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

        /// <summary>
        /// Resolves an IMDB id to its TMDB <em>TV</em> id via <c>/find</c> (<c>tv_results</c>), returning
        /// null when there is no TV match. The series-trailer backfill uses this to bridge from our stored
        /// IMDB id to TMDB's TV record, the same way <see cref="TryGetMovie"/> does for films.
        /// </summary>
        public async Task<TmdbTvResultDto?> TryGetTvId(string imdbID)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/find/{imdbID}?api_key={_options.ApiKey}&external_source=imdb_id", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            string responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                var root = JsonConvert.DeserializeObject<Root>(responseContent);
                return root?.TvResults?.FirstOrDefault();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Fetches the full TMDB TV detail (with embedded videos) for a known TMDB TV id — the source of
        /// the series trailer key. Returns null on a non-success status.
        /// </summary>
        public async Task<TmdbTvDetailDto?> GetTvDetail(int tvId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/3/tv/{tvId}?api_key={_options.ApiKey}&append_to_response=videos", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            string responseContent = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<TmdbTvDetailDto>(responseContent);
        }

        /// <summary>
        /// Searches TMDB's FILM index by name — the mirror of <see cref="SearchTv"/>, and needed for
        /// the same reason in reverse: a general title search can answer a movie query with a
        /// same-named television show, and asking the film index removes the ambiguity instead of
        /// guessing which of the two the shelf meant.
        /// </summary>
        public async Task<List<MovieDto>> SearchMovie(string query, int? year = null)
        {
            var url = $"/3/search/movie?api_key={_options.ApiKey}&query={Uri.EscapeDataString(query)}"
                      + (year != null ? $"&year={year}" : "");
            var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative)));
            if (!response.IsSuccessStatusCode) return new List<MovieDto>();

            var content = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<TmdbMovieSearchDto>(content)?.Results ?? new List<MovieDto>();
            }
            catch (JsonException)
            {
                return new List<MovieDto>();
            }
        }

        /// <summary>
        /// Searches TMDB's TV index by name. The point is the index, not the ranking: a plain title
        /// search cannot tell "The Muppet Show" from "The Muppet Movie" and will often prefer the
        /// film, because a show and its movies share a name and a shelf. Asking the TV index instead
        /// removes the ambiguity at the source rather than second-guessing a film match afterwards.
        /// </summary>
        public async Task<List<TmdbTvResultDto>> SearchTv(string query, int? firstAirYear = null)
        {
            var url = $"/3/search/tv?api_key={_options.ApiKey}&query={Uri.EscapeDataString(query)}"
                      + (firstAirYear != null ? $"&first_air_date_year={firstAirYear}" : "");
            var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative)));
            if (!response.IsSuccessStatusCode) return new List<TmdbTvResultDto>();

            var content = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<TmdbTvSearchDto>(content)?.Results ?? new List<TmdbTvResultDto>();
            }
            catch (JsonException)
            {
                return new List<TmdbTvResultDto>();
            }
        }

        /// <summary>A TV show's IMDb id, so a TMDB-side match can be carried back into our tt-keyed
        /// world. Null when TMDB holds no IMDb id for it.</summary>
        public async Task<string?> GetTvImdbId(int tvId)
        {
            var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                new Uri($"/3/tv/{tvId}/external_ids?api_key={_options.ApiKey}", UriKind.Relative)));
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            try
            {
                var id = JsonConvert.DeserializeObject<TmdbExternalIdsDto>(content)?.ImdbId;
                return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// One season's episode list. This is the HTTP-only alternative to scraping IMDb's episode
        /// pages with Playwright — the API pod has no browser, and the review tool's series resolution
        /// has to run there. Returns null on a non-success status (a season TMDB doesn't have).
        /// </summary>
        public async Task<TmdbSeasonDetailDto?> GetTvSeason(int tvId, int seasonNumber)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri($"/3/tv/{tvId}/season/{seasonNumber}?api_key={_options.ApiKey}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return null;

            string responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<TmdbSeasonDetailDto>(responseContent);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
