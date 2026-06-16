using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core.Logging;
using MovieTheater.Db;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.BoardgameImage;
using MovieTheater.Services.Python;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;
using MovieTheater.Services.Bgg;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Services
{
    public static class MovieTheaterServiceExtensions
    {
        public static IServiceCollection AddMovieTheaterServices(this IServiceCollection services, MovieTheaterConfiguration config)
        {
            // Make the bound config resolvable so controllers/services can inject it
            // (StreamController needs the Jellyfin + gateway settings).
            services.AddSingleton(config);
            services.AddMovieTheaterLogging();
            services.AddMovieTheaterDb(config.DbConnectionString);
            services.AddPosterImageServices(config.MoviePostersDir, config.Environment);
            services.AddBoardgameImageServices(config.BoardgameImagesDir, config.Environment);
            services.AddPythonService(config.PyPath);
            services.AddImdbServices(config.ImdbApiKey);
            services.AddTmdbServices(config.TmdbApiKey);
            services.AddOmdbServices(config.OmdbApiKey);
            services.AddGoogleServices(config.GoogleSearchApiKey, config.GoogleSearchEngineId);
            services.AddBoardGameGeekServices(config.BggApiToken);
            services.AddJellyfinServices(config);
            services.AddTransient<BoardgameRulesService>();
            services.AddTransient<Poster.PosterFetchService>();
            services.AddTransient<TitleEnrichService>();
            services.AddTransient<IMDBApiService>();
            services.AddSingleton<PosterMosaicService>();
            services.AddSingleton<BoardgameSimilarityService>();
            return services;
        }
    }
}
