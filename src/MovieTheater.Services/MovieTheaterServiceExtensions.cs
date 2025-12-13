using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core.Logging;
using MovieTheater.Db;
using MovieTheater.Services.ImdbApi;
using MovieTheater.Services.Poster;
using MovieTheater.Services.Python;
using MovieTheater.Services.Tmdb;
using MovieTheater.Services.Omdb;
using MovieTheater.Services.Google;

namespace MovieTheater.Services
{
    public static class MovieTheaterServiceExtensions
    {
        public static IServiceCollection AddMovieTheaterServices(this IServiceCollection services, MovieTheaterConfiguration config)
        {
            services.AddMovieTheaterLogging();
            services.AddMovieTheaterDb(config.DbConnectionString);
            services.AddPosterImageServices(config.MoviePostersDir, config.Environment);
            services.AddPythonService(config.PyPath);
            services.AddImdbServices(config.ImdbApiKey);
            services.AddTmdbServices(config.TmdbApiKey);
            services.AddOmdbServices(config.OmdbApiKey);
            services.AddGoogleServices(config.GoogleSearchApiKey, config.GoogleSearchEngineId);
            services.AddTransient<IMDBApiService>();
            return services;
        }
    }
}
