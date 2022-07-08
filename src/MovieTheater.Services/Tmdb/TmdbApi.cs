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
    }
}
