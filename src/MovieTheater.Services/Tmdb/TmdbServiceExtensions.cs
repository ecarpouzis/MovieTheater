using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Tmdb
{
    public static class TmdbServiceExtensions
    {
        public static IServiceCollection AddTmdbServices(this IServiceCollection services, string? tmdbApiKey)
        {
            if (tmdbApiKey == null)
                throw new ArgumentNullException(nameof(tmdbApiKey));

            services.Configure<TmdbApiOptions>(options => options.ApiKey = tmdbApiKey);
            services.AddTransient<TmdbApi>();
            services.AddHttpClient<TmdbApi>((httpClient) => { httpClient.BaseAddress = new Uri("https://api.themoviedb.org"); });


            return services;
        }
    }
}
