using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace MovieTheater.Services.Omdb
{
    public class OmdbApi
    {
        private HttpClient _httpClient;
        private OmdbApiOptions _options;

        //Because we use IOptions we can hotswap options at runtime and similar benefits
        //it is the proper way to configure an httpclient in .net
        
        //AspNetCore typically includes this by default, but as some of my code is in a standalone DLL it includes no packages by default.
        //Because I want to use IOptions, I install Microsoft.Extensions.Options

        //This occurs wherever services.configure is called (MovieAPI.Startup) 
        public OmdbApi(HttpClient httpClient, IOptions<OmdbApiOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<OmdbMovieDto> GetMovie(string imdbID)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"?apikey={_options.ApiKey}&i={imdbID}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root =  JsonConvert.DeserializeObject<OmdbMovieDto>(responseContent);
            return root;
        }


        public async Task<OmdbMovieDto> GetMovieByName(string name)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"?apikey={_options.ApiKey}&i={name}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root = JsonConvert.DeserializeObject<OmdbMovieDto>(responseContent);
            return root;
        }
    }
}
