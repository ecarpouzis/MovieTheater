using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;

namespace MovieTheater.Services.Bgg
{
    public static class BggServiceExtensions
    {
        public static IServiceCollection AddBoardGameGeekServices(this IServiceCollection services, string? username = null, string? password = null, string? cookieHeader = null)
        {
            services.Configure<BggApiOptions>(options =>
            {
                options.Username = username;
                options.Password = password;
                options.CookieHeader = cookieHeader;
            });

            services.AddTransient<BoardGameGeekApi>();
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
