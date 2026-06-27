using System;
using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.OpenSubtitles
{
    public static class OpenSubtitlesServiceExtensions
    {
        public static IServiceCollection AddOpenSubtitlesServices(this IServiceCollection services, string? apiKey, string? username, string? password)
        {
            services.Configure<OpenSubtitlesOptions>(o => { o.ApiKey = apiKey; o.Username = username; o.Password = password; });
            services.AddHttpClient<OpenSubtitlesApi>(c =>
            {
                c.BaseAddress = new Uri("https://api.opensubtitles.com/api/v1/");
                // OpenSubtitles rejects requests without an identifying User-Agent, and wants the Api-Key
                // on every call — set both as defaults so each request carries them.
                c.DefaultRequestHeaders.UserAgent.ParseAdd("MovieTheater v1.0");
                c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                if (!string.IsNullOrWhiteSpace(apiKey))
                    c.DefaultRequestHeaders.Add("Api-Key", apiKey);
            });
            return services;
        }
    }
}
