using Microsoft.Extensions.Options;
using MovieTheater.Db;
using MovieTheater.Services.Tmdb;
using Newtonsoft.Json;
using System.IO;

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

        public Movie? OmdbToMovie(OmdbMovieDto omdbMovie)
        {
            if (string.IsNullOrEmpty(omdbMovie.Title))
            {
                return null;
            }
            DateTime releaseDate;
            DateTime.TryParse(omdbMovie.Released, out releaseDate);
            string? tomatoRatingString = omdbMovie.Ratings?.FirstOrDefault(r => r.Source == "Rotten Tomatoes")?.Value;

            int? tomatoRating = tomatoRatingString != null ?
                                int.Parse(tomatoRatingString.Replace("%", ""))
                                : null;

            decimal? imdbRating = decimal.TryParse(omdbMovie.imdbRating, out decimal parsedRating) ?
                                  parsedRating 
                                  : null;

            return new Movie()
            {
                Title = omdbMovie.Title,
                SimpleTitle = omdbMovie.Title,
                Rating = omdbMovie.Rated,
                ReleaseDate = releaseDate,
                Runtime = omdbMovie.Runtime,
                Genre = omdbMovie.Genre,
                Director = omdbMovie.Director,
                Writer = omdbMovie.Writer,
                Actors = omdbMovie.Actors,
                Plot = omdbMovie.Plot,
                PosterLink = omdbMovie.Poster,
                imdbRating = imdbRating,
                imdbID = omdbMovie.imdbID,
                tomatoRating = tomatoRating,
                UploadedDate = DateTime.Now,
                RemoveFromRandom = false
            };

        }

        public async Task<Movie> GetMovie(string imdbID)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"?apikey={_options.ApiKey}&i={imdbID}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root = JsonConvert.DeserializeObject<OmdbMovieDto>(responseContent);
            var movie = OmdbToMovie(root);
            if (movie == null)
            {
                return await Task.FromResult<Movie>(null);
            }
            return movie;
        }


        public async Task<Movie> GetMovieByName(string name)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"?apikey={_options.ApiKey}&t={name}", UriKind.Relative));
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            var root = JsonConvert.DeserializeObject<OmdbMovieDto>(responseContent);
            var movie = OmdbToMovie(root);
            if (movie == null)
            {
                return await Task.FromResult<Movie>(null);
            }
            return movie;
        }
    }
}
