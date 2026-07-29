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

        /// <summary>STUN servers echoed to the client shim's iceConfig.</summary>
        public List<string> ArcadeStunServers { get; set; } = new();

        /// <summary>
        /// TURN relay URLs echoed to the client shim's iceConfig — the last-resort ICE path (relay
        /// candidates rank lowest, so LAN/cellular clients that connect directly never touch it). This is
        /// the ONLY route that works from a guest/isolated SSID or a hostile remote network, where the
        /// client can reach Ziggy on the public-IP TCP hairpin but not via direct/hairpinned UDP. Use
        /// <c>turns:</c> over TCP for exactly that reason — a UDP TURN listener would hit the same
        /// UDP-hairpin wall those clients already fail on. Example:
        /// <c>["turns:turn.carpouzis.com:5349?transport=tcp"]</c>. Empty = STUN-only. See docs/arcade/turn-relay.md.
        /// </summary>
        public List<string> ArcadeTurnUrls { get; set; } = new();

        /// <summary>Shared secret for minting ephemeral TURN credentials (coturn <c>static-auth-secret</c> /
        /// the TURN REST API scheme). MUST be byte-identical to the TURN server's secret. When empty, no
        /// credential is minted and <see cref="ArcadeTurnUrls"/> is ignored (an unauthenticated TURN entry
        /// is useless), so the client falls back to STUN-only.</summary>
        public string? ArcadeTurnSecret { get; set; }

        /// <summary>TTL (seconds) of a minted TURN credential. It must outlast the longest single session,
        /// because the TURN allocation is refreshed with the SAME credential for the room's lifetime.
        /// Default 12h. The credential is low-value — the relay is peer-locked to the arcade worker — so a
        /// generous window is safe.</summary>
        public int ArcadeTurnCredentialTtlSeconds { get; set; } = 43200;

        /// <summary>
        /// Multi-zone worker routing (roadmap WS-B). OFF by default = the v1 single-pool behavior
        /// (join descriptors carry an empty <c>zone=</c>, which the coordinator's <c>Worker.In</c>
        /// treats as a wildcard matching every worker). When ON, the descriptor carries a per-system
        /// zone (GL 3D systems → <c>gl</c>, everything else → <c>main</c>) so the Windows-native GL
        /// worker pool is isolated from the WSL 2D pool. DO NOT enable until BOTH pools are explicitly
        /// zoned (WSL workers <c>CLOUD_GAME_WORKER_NETWORK_ZONE=main</c>, gl worker <c>zone=gl</c>) —
        /// an empty-zoned worker fails <c>In("main")</c>, so flipping this first would break every 2D room.
        /// </summary>
        public bool ArcadeZoningEnabled { get; set; }

        /// <summary>RetroAchievements Web API credentials (the site service account's — retroachievements.org
        /// → Settings → Web API Key). Used ONLY for read-only PULLs of a linked user's public RA profile /
        /// recent unlocks to show on the site (arcade-ra-sync-plan.md). Distinct from the worker's connect
        /// token (which drives the in-room scoring engine). Empty = the pull endpoint degrades to
        /// "not configured", like every other arcade feature gates on its config.</summary>
        public string? ArcadeRaWebApiUser { get; set; }
        public string? ArcadeRaWebApiKey { get; set; }

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

        /// <summary>IGDB (Internet Game Database) via Twitch OAuth client-credentials — the source for arcade/
        /// game-card review scores AND fallback box art (covers) for titles libretro-thumbnails lacks or
        /// mis-formats. Register an app at dev.twitch.tv/console; caching IGDB data locally is permitted.</summary>
        public string? IgdbClientId { get; set; }
        public string? IgdbClientSecret { get; set; }

        /// <summary>SteamGridDB API key (steamgriddb.com → profile → preferences → API) — the community
        /// cover source that fills the box-art tail libretro-thumbnails and IGDB miss (homebrew, multicarts,
        /// obscure/digital titles). Used as the last step of the box-art cascade.</summary>
        public string? SteamGridDbApiKey { get; set; }

        /// <summary>A SECOND, web-wide Google Custom Search Engine id (distinct from the imdb-locked
        /// <see cref="GoogleSearchEngineId"/>) used only for box-art image search — the final cascade step
        /// that finds a cover for anything even SteamGridDB lacks. Reuses <see cref="GoogleSearchApiKey"/>.
        /// Create one at programmablesearchengine.google.com set to "Search the entire web" + Image search on.</summary>
        public string? BoxArtImageSearchEngineId { get; set; }

        /// <summary>
        /// Usernames granted administrator rights (case-insensitive). This is the root of trust for
        /// the admin tools — it can only be changed in server config, never through the app, so admin
        /// rights can't be escalated in-band. Because login is passwordless, being a config admin is
        /// not enough on its own: the admin endpoints also require a password-verified session, so an
        /// admin account must have a password set before it can administer anything.
        /// </summary>
        public List<string> AdminUsernames { get; set; } = new();

        /// <summary>IANA/Windows timezone id for the household — used to bucket channel-viewing
        /// telemetry into local days (a movie night must not straddle two UTC dates). Default
        /// America/New_York.</summary>
        public string? TelemetryTimeZone { get; set; }

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
