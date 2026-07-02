using Microsoft.Extensions.Configuration;
using MovieTheater.Core;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Services
{
    public class MovieTheaterConfiguration
    {
        public string? MoviePostersDir { get; set; }

        public string? BoardgameImagesDir { get; set; }

        /// <summary>
        /// Where the ASP.NET Core Data Protection key ring is persisted. These keys encrypt the auth
        /// cookie, so they MUST live on storage that survives a redeploy/pod restart — otherwise every
        /// deploy generates new keys and signs every user out. When null we derive a folder on the same
        /// mount as <see cref="MoviePostersDir"/> (which is already persistent in prod).
        /// </summary>
        public string? DataProtectionKeysDir { get; set; }

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

        // ── Arcade (arcade-plan.md §5). There is deliberately NO ArcadeCoordinatorBaseUrl/
        // ArcadeTunnelKey here: the pod never calls Ziggy for arcade — the creator's browser drives
        // room lifecycle (§3 asymmetry). Arcade endpoints hide/503 when this is unconfigured. ──

        /// <summary>Public base of the ArcadeGateway (Appendix C) — what join descriptors point browsers at.</summary>
        public string? ArcadeGatewayBaseUrl { get; set; }

        /// <summary>HMAC secret shared with the ArcadeGateway; signs the WS join capability tokens (Appendix D1).</summary>
        public string? ArcadeTokenSecret { get; set; }

        /// <summary>Best-effort room cap; MUST equal the deployed CloudRetro worker count (§2 box) — CloudRetro's
        /// t=112 "no free slots" is the authoritative backstop.</summary>
        public int ArcadeMaxConcurrentRooms { get; set; }

        /// <summary>TTL of a minted join token; covers the WS *connect*, not the session length.</summary>
        public int ArcadeJoinTokenTtlSeconds { get; set; }

        /// <summary>STUN servers echoed to the client shim's iceConfig (no TURN in v1).</summary>
        public List<string> ArcadeStunServers { get; set; } = new();

        public string? ImdbApiKey { get; set; }

        public string? TmdbApiKey { get; set; }

        public string? OmdbApiKey { get; set; }
        public string? GoogleSearchApiKey { get; set; }
        public string? GoogleSearchEngineId { get; set; }

        public string? BggApiToken { get; set; }

        /// <summary>OpenSubtitles.com REST API consumer key (opensubtitles.com/en/consumers); enables the
        /// direct subtitle search/download that replaces the rate-limited Jellyfin plugin.</summary>
        public string? OpenSubtitlesApiKey { get; set; }
        /// <summary>OpenSubtitles account login — needed for the download token / daily quota (search needs only the key).</summary>
        public string? OpenSubtitlesUsername { get; set; }
        public string? OpenSubtitlesPassword { get; set; }

        public string? PyPath { get; set; }

        /// <summary>
        /// Usernames granted administrator rights (case-insensitive). This is the root of trust for
        /// the admin tools — it can only be changed in server config, never through the app, so admin
        /// rights can't be escalated in-band. Because login is passwordless, being a config admin is
        /// not enough on its own: the admin endpoints also require a password-verified session, so an
        /// admin account must have a password set before it can administer anything.
        /// </summary>
        public List<string> AdminUsernames { get; set; } = new();

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
