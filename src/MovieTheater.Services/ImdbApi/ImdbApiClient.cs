using Microsoft.Extensions.Options;
using MovieTheater.Db;

namespace MovieTheater.Services.ImdbApi
{
    public class ImdbApiClient
    {
        private readonly string imdbApiKey;
        // One ApiLib per client instead of newing one per lookup (each new instance stands up its own
        // HttpClient). The key is fixed for the client's lifetime, so a single instance is reused.
        private readonly Lazy<IMDbApiLib.ApiLib> apiLib;

        public ImdbApiClient(IOptions<ImdbApiOptions> options)
        {
            imdbApiKey = options.Value.ApiKey;
            apiLib = new Lazy<IMDbApiLib.ApiLib>(() => new IMDbApiLib.ApiLib(imdbApiKey));
        }

        public async Task<Movie> ImdbApiLookupImdbID(string imdbID)
        {
            if (String.IsNullOrEmpty(imdbID))
                return null;

            var movieData = await apiLib.Value.TitleAsync(imdbID);

            if (movieData.Id == null)
                return null;

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
            var searchData = await apiLib.Value.SearchTitleAsync(name);
            return await ImdbApiLookupImdbID(searchData.Results[0].Id);
        }
    }
}
