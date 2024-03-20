using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Omdb
{
    public static class OmdbServiceExtensions
    {
        public static IServiceCollection AddOmdbServices(this IServiceCollection services, string? omdbApiKey)
        {
            if (omdbApiKey == null)
                throw new ArgumentNullException(nameof(omdbApiKey));

            services.Configure<OmdbApiOptions>(options => options.ApiKey = omdbApiKey);
            services.AddTransient<OmdbApi>();
            services.AddHttpClient<OmdbApi>((httpClient) => { httpClient.BaseAddress = new Uri("http://www.omdbapi.com/"); });

            return services;
        }
    }
}
