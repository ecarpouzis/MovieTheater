using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Google
{
    public static class GoogleServiceExtensions
    {
        public static IServiceCollection AddGoogleServices(this IServiceCollection services, string? googleApiKey, string? googleEngineId)
        {
            if (googleApiKey == null)
                throw new ArgumentNullException(nameof(googleApiKey));

            if (googleEngineId == null)
                throw new ArgumentNullException(nameof(googleEngineId));

            services.Configure<GoogleApiOptions>(options => { options.ApiKey = googleApiKey; options.EngineId = googleEngineId; });
            services.AddTransient<GoogleSearchService>();

            return services;
        }
    }
}
