using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Jellyfin
{
    public static class JellyfinServiceExtensions
    {
        /// <summary>
        /// Registers the Jellyfin client. Unlike the other API registrations this tolerates
        /// missing config (Jellyfin is optional until streaming ships everywhere) — the
        /// client validates lazily on first use instead.
        /// </summary>
        public static IServiceCollection AddJellyfinServices(this IServiceCollection services, MovieTheaterConfiguration config)
        {
            services.Configure<JellyfinApiOptions>(options =>
            {
                options.BaseUrl = config.JellyfinBaseUrl;
                options.ApiKey = config.JellyfinApiKey;
                options.TunnelKey = config.JellyfinTunnelKey;
            });

            void Configure(HttpClient httpClient, TimeSpan timeout)
            {
                httpClient.BaseAddress = new Uri(config.JellyfinBaseUrl ?? "http://jellyfin-not-configured.invalid");
                httpClient.Timeout = timeout;
                if (!string.IsNullOrEmpty(config.JellyfinApiKey))
                {
                    httpClient.DefaultRequestHeaders.Add("X-Emby-Token", config.JellyfinApiKey);
                    // The full MediaBrowser header also declares a DeviceId, which Jellyfin
                    // stamps onto transcode jobs — Stream/Stop kills encodings by it.
                    httpClient.DefaultRequestHeaders.Add("X-Emby-Authorization",
                        $"MediaBrowser Client=\"MovieTheater\", Device=\"site\", DeviceId=\"{JellyfinApi.DeviceId}\", Version=\"1.0\", Token=\"{config.JellyfinApiKey}\"");
                }
                if (!string.IsNullOrEmpty(config.JellyfinTunnelKey))
                    httpClient.DefaultRequestHeaders.Add("X-Tunnel-Key", config.JellyfinTunnelKey);
            }

            services.AddHttpClient<JellyfinApi>(c => Configure(c, TimeSpan.FromMinutes(2)));

            // Same auth/base, far longer ceiling — for admin calls that block server-side for minutes
            // (keyframe extraction walks every packet of a file over SMB). The default client's 2 minutes
            // is deliberately tight because user-facing PlaybackInfo rides it, so raising that instead
            // would let a hung Jellyfin stall a page request for a quarter of an hour.
            services.AddHttpClient(JellyfinApi.LongRunningClientName, c => Configure(c, TimeSpan.FromMinutes(15)));

            return services;
        }
    }
}
