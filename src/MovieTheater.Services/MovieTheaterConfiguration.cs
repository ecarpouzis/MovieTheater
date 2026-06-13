using Microsoft.Extensions.Configuration;
using MovieTheater.Core;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Services
{
    public class MovieTheaterConfiguration
    {
        public string? MoviePostersDir { get; set; }

        public string? BoardgameImagesDir { get; set; }

        public string? DbConnectionString { get; set; }

        /// <summary>Jellyfin base URL — the §3.2 authenticated ingress in prod, http://localhost:8096 in dev.</summary>
        public string? JellyfinBaseUrl { get; set; }

        /// <summary>Jellyfin API key (X-Emby-Token), minted in the Jellyfin dashboard.</summary>
        public string? JellyfinApiKey { get; set; }

        /// <summary>X-Tunnel-Key value the Caddy ingress gate requires (streaming-plan.md §3.2); null when talking to Jellyfin directly.</summary>
        public string? JellyfinTunnelKey { get; set; }

        /// <summary>Prefix translations between DB paths (<see cref="Db.Movie.FilePath"/> form) and the paths Jellyfin reports.</summary>
        public List<JellyfinPathMapping> JellyfinPathMappings { get; set; } = new();

        /// <summary>Public base of the StreamGateway (§3.3) — the data plane that serves video.</summary>
        public string? StreamGatewayBaseUrl { get; set; }

        /// <summary>HMAC secret shared with the StreamGateway; signs the capability URLs (§3.3).</summary>
        public string? StreamTokenSecret { get; set; }

        /// <summary>0 = unlimited; otherwise Stream/Start returns a friendly "theater full" 503 when reached.</summary>
        public int StreamingMaxConcurrentTranscodes { get; set; }

        public string? ImdbApiKey { get; set; }

        public string? TmdbApiKey { get; set; }

        public string? OmdbApiKey { get; set; }
        public string? GoogleSearchApiKey { get; set; }
        public string? GoogleSearchEngineId { get; set; }

        public string? BggApiToken { get; set; }

        public string? PyPath { get; set; }

        public HostedEnvironment Environment { get; }

        public IConfiguration RawConfiguration { get; }

        public MovieTheaterConfiguration(IConfiguration rawConfig)
        {
            rawConfig.Bind(this);
            RawConfiguration = rawConfig;

            var aspEnv = rawConfig["ASPNETCORE_ENVIRONMENT"];
            if (aspEnv == "Production")
                Environment = HostedEnvironment.Production;
            else
                Environment = HostedEnvironment.Development;
        }
    }
}
