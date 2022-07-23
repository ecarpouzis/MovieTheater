using Microsoft.Extensions.Options;
using MovieTheater.Db;

namespace MovieTheater.Services.ImdbApi
{
    public class ImdbApiClient
    {
        private readonly string imdbApiKey;

        public ImdbApiClient(IOptions<ImdbApiOptions> options)
        {
            imdbApiKey = options.Value.ApiKey;
        }

        public async Task<Movie> ImdbApiLookupImdbID(string imdbID)
        {
            var apiLib = new IMDbApiLib.ApiLib(imdbApiKey);
            var movieData = await apiLib.TitleAsync(imdbID);
            bool imdbParseSuccess = Decimal.TryParse(movieData.IMDbRating, out var imdbRatingParsed);
            return new Movie()
            {
                imdbID = imdbID,
                Title = movieData.Title,
                SimpleTitle = movieData.Title,
                Rating = movieData.ContentRating,
                ReleaseDate = DateTime.Parse(movieData.ReleaseDate),
                Runtime = movieData.RuntimeStr,
                Genre = movieData.Genres,
                Director = movieData.Directors,
                Writer = movieData.Writers,
                Actors = String.Join(", ", movieData.ActorList.Take(3).Select(x => x.Name)),
                Plot = movieData.Plot,
                PosterLink = movieData.Image,
                imdbRating = imdbParseSuccess? imdbRatingParsed : null
            };
        }

        public async Task<Movie> ImdbApiLookupName(string name)
        {
            var apiLib = new IMDbApiLib.ApiLib(imdbApiKey);
            var searchData = await apiLib.SearchTitleAsync(name);
            return await ImdbApiLookupImdbID(searchData.Results[0].Id);
        }
    }
}
