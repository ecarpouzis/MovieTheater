using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace MovieTheater.Services.Bgg
{
    public static class BggServiceExtensions
    {
        /// <summary>
        /// Registers BoardGameGeek API services with Bearer token authentication.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="apiToken">Bearer token from https://boardgamegeek.com/applications</param>
        /// <param name="rateLimitDelayMs">Minimum delay between requests in milliseconds (default: 5000ms per BGG guidelines)</param>
        public static IServiceCollection AddBoardGameGeekServices(this IServiceCollection services, string? apiToken = null, int rateLimitDelayMs = 5000)
        {
            services.Configure<BggApiOptions>(options =>
            {
                options.ApiToken = apiToken;
                options.RateLimitDelayMs = rateLimitDelayMs;
            });

            // Typed-client registration resolves transient; the rate limiter inside
            // BoardGameGeekApi is static, so no shared lifetime is needed. (A plain
            // AddSingleton here would be shadowed by this registration anyway.)
            services.AddHttpClient<BoardGameGeekApi>((httpClient) =>
            {
                httpClient.Timeout = TimeSpan.FromSeconds(60);
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater/1.0");
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
                httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            });

            return services;
        }
    }
}
