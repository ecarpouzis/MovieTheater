using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.ImdbApi
{
    public static class ImdbServiceExtensions
    {
        public static IServiceCollection AddImdbServices(this IServiceCollection services, string imdbApiKey)
        {
            if (imdbApiKey == null)
                throw new ArgumentNullException(nameof(imdbApiKey));

            services.AddTransient<ImdbApiClient>();
            services.Configure<ImdbApiOptions>(options => options.ApiKey = imdbApiKey);

            return services;
        }
    }
}
