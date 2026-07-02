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
using MovieTheater.Services.Arcade;
using MovieTheater.Services.OpenSubtitles;

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
            services.AddArcadeServices(config);
            services.AddOpenSubtitlesServices(config.OpenSubtitlesApiKey, config.OpenSubtitlesUsername, config.OpenSubtitlesPassword);
            services.AddTransient<Jellyfin.JellyfinSyncService>();
            services.AddTransient<BoardgameRulesService>();
            services.AddTransient<Poster.PosterFetchService>();
            services.AddTransient<TitleEnrichService>();
            services.AddHttpClient<IMDBApiService>(httpClient =>
            {
                // Common browser UA to improve acceptance by the IMDb search endpoint.
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.102 Safari/537.36");
            });
            services.AddSingleton<PosterMosaicService>();
            services.AddSingleton<BoardgameSimilarityService>();
            return services;
        }
    }
}
