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

            //If the release date is null, try the first of the movie's year. If all else fails, return null.
            bool dateReleaseParseSuccess = DateTime.TryParse(movieData.ReleaseDate, out var dateReleaseDateParsed);
            bool yearReleaseParseSuccess = DateTime.TryParse(movieData.Year+"-1-1", out var yearReleaseDateParsed);
            DateTime? releaseDate = null;
            if (dateReleaseParseSuccess)
                releaseDate = dateReleaseDateParsed;
            else if (yearReleaseParseSuccess)
                releaseDate = yearReleaseDateParsed;

            return new Movie()
            {
                imdbID = imdbID,
                Title = movieData.Title,
                SimpleTitle = movieData.Title,
                Rating = movieData.ContentRating,
                ReleaseDate = releaseDate,
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
