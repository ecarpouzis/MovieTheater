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

        // ── Music (docs/music-plan.md §3). Endpoints degrade to 501 when unconfigured, like every
        // other optional vertical. ──

        /// <summary>Root of the music library as this host mounts it. Used by the music-ingest CLI
        /// (walks it, bounded per run) and by Stream/Start only to expose which relative paths exist —
        /// the StreamGateway resolves the same relative paths against its own mount.</summary>
        public string? MusicLibraryDir { get; set; }

        /// <summary>Album art directory (music-plan.md §2.5); null = reuse <see cref="MoviePostersDir"/>
        /// with the "music_" filename bucket.</summary>
        public string? MusicImagesDir { get; set; }

        /// <summary>Hand the player the gateway's ffmpeg transcode URL for formats no browser decodes
        /// (.wma/.ape/…, music-plan.md §Phase 7) instead of refusing them. Off by default: it only
        /// works when the StreamGateway host also has <c>FfmpegPath</c> set, and the two configs are
        /// separate hosts — turning this on without that yields a 404 from the gateway.</summary>
        public bool MusicTranscodeEnabled { get; set; }

        // ── Family photo album (docs/photos-plan.md §2.2/§2.5). Every surface degrades when these are
        // unset — the CLI refuses to run and the token minter answers "not configured" — so a host that
        // is not photo-adjacent simply does not offer the vertical. NO path here is ever hardcoded:
        // the collection root differs per host and never appears in code (§6). ──

        /// <summary>Root of the photo collection as THIS host mounts it. The ingest CLI walks it
        /// (bounded per call, read-only) and stores every path root-relative, so the StreamGateway
        /// resolves the same relative paths against its own mount.</summary>
        public string? PhotosLibraryDir { get; set; }

        /// <summary>Where the ingest WRITES its pre-generated derivatives (§2.2) — the same directory
        /// the gateway serves as <c>PhotoThumbCacheDir</c>, as this host mounts it. Derived data:
        /// deletable and rebuildable, sized for tens of GB at 100k photos.</summary>
        public string? PhotosThumbCacheDir { get; set; }

        /// <summary>IANA/Windows timezone id used to convert a source that supplies TRUE UTC (GPS
        /// timestamps now; Takeout's photoTakenTime and video containers later) into the naive local
        /// wall-clock <c>PhotoAsset.TakenAt</c> is defined as (§2.7). EXIF itself carries no timezone
        /// and is taken as wall-clock directly. Default America/New_York.</summary>
        public string? PhotosHomeTimeZone { get; set; }

        /// <summary>Where the ingest drops its review artifacts — currently the ambiguous move/re-pair
        /// list the walk refuses to auto-apply (§2.5). Kept with the other pipeline data under the
        /// repo's <c>data/</c> convention and NEVER on the NAS (§2.11). Default <c>data/photos</c>.</summary>
        public string? PhotosReportDir { get; set; }

        /// <summary>
        /// Item id of the DEDICATED family Jellyfin library (§2.3) — the one whose folders are the
        /// video-bearing subtrees of the photo collection.
        ///
        /// <para>Two independent uses. <c>photos-sync-jellyfin</c> scopes its item sweep to it, so the
        /// family sync never enumerates the movie library. And the MOVIE-side <c>sync-jellyfin</c> asks
        /// Jellyfin for that library's on-disk locations and adds them to its exclusion prefixes — the
        /// belt to the braces of <see cref="PhotosLibraryDir"/>, which already excludes the collection by
        /// PATH. <b>The exclusion works with this unset</b>; the id only widens it to library roots the
        /// configured photo root does not cover.</para>
        /// </summary>
        public string? PhotosJellyfinLibraryId { get; set; }

        /// <summary>
        /// The blast-radius ceiling on the movie-side <c>sync-jellyfin</c>, as a FRACTION of what the
        /// server reported (0–1). A run that would exclude more than this share of the library as
        /// "family", or stamp more than this share of the existing <c>MediaFile</c> rows as missing,
        /// ABORTS and reports instead of writing.
        ///
        /// <para><b>Why a ceiling exists at all.</b> Both numbers are supposed to be small: the family
        /// collection is a corner of the disk, and a healthy sync finds nearly every row it already has.
        /// A misconfiguration makes them enormous rather than slightly wrong — a <c>PhotosLibraryDir</c>
        /// that expands to a volume root excludes the whole library, and an unmounted share makes every
        /// file on the NAS look deleted. Both then write across the entire table in one pass, and both
        /// look like a successful sync in the log. <see cref="JellyfinFamilyExclusion.IsMeaningfulRoot"/>
        /// refuses the specific volume-root shape; this is the outcome-shaped backstop for the shapes
        /// nobody predicted.</para>
        ///
        /// <para>Deliberately conservative: half the library is far past anything a normal run does, so
        /// the guard costs nothing on a healthy sync and cannot be tripped by ordinary churn. 0 or a
        /// negative value disables it, for the deliberate case where an operator really is retiring most
        /// of the catalogue and has said so.</para>
        /// </summary>
        public double JellyfinSyncMaxWriteFraction { get; set; } = 0.5;

        /// <summary>
        /// The floor beneath which <see cref="JellyfinSyncMaxWriteFraction"/> is not applied — a fraction
        /// of a handful of rows means nothing, and a fresh or tiny library must not be un-syncable.
        /// </summary>
        public int JellyfinSyncGuardMinRows { get; set; } = 25;

        /// <summary>
        /// Root of an EXTRACTED Google Takeout archive for <c>photos-google-mesh</c> (§2.10). The Photos
        /// Library API lost third-party read access in 2025, so a downloaded, unzipped archive is the
        /// only lane left; this points at the directory it was extracted into.
        ///
        /// <para>Read-only, and unset by default like every other path here — no host is required to
        /// have an archive staged, and the command refuses rather than guessing where one might be.</para>
        /// </summary>
        public string? PhotosGoogleTakeoutDir { get; set; }

        /// <summary>
        /// Destination for the download lane — <b>the one additive NAS write in this whole vertical</b>
        /// (§2.10), and the only setting in this block that names a directory the pipeline WRITES INTO.
        ///
        /// <para><b>It has no default and never will.</b> <c>photos-google-mesh --download</c> refuses to
        /// run when it is unset, refuses to run before the archive's match pass has fully drained, and
        /// refuses to overwrite any path that already exists. Everything else in the vertical is a
        /// database row; this is the single exception, it is opt-in per run, and it is separately
        /// approved (§6).</para>
        /// </summary>
        public string? PhotosGoogleSyncDir { get; set; }

        /// <summary>
        /// Absolute path to <c>ffprobe</c> on the host that runs <c>photos-ingest --pass video</c>
        /// (§2.3/§2.5 phase 2: "videos via ffprobe"). Unset means the video pass says so and does
        /// nothing, exactly like an unconfigured thumb cache — no host is required to have it.
        /// </summary>
        /// <remarks>Names match the StreamGateway's existing <c>FfmpegPath</c> convention. Both binaries
        /// are only ever run READ-ONLY against a collection file, with a bounded runtime and a kill on
        /// timeout, and their stdout is parsed defensively rather than trusted (§6).</remarks>
        public string? FfprobePath { get; set; }

        /// <summary>Absolute path to <c>ffmpeg</c>, used only to grab a single poster frame per video
        /// into the derivative cache (§2.3). See <see cref="FfprobePath"/>.</summary>
        public string? FfmpegPath { get; set; }

        // ── The Immich enrichment sidecar (§2.4). Headless, LAN-only, DISPOSABLE: it proposes, our DB
        // decides. Both keys unset is the normal state on every host except the gateway-adjacent one
        // that runs `photos-sync-immich`, and every surface degrades to fully-manual with them unset —
        // no dead buttons, no errors, hand-tagging unchanged. ──

        /// <summary>Base URL of the Immich API as THIS host reaches it (e.g. <c>http://immich-host:2283</c>).
        /// Never internet-exposed and never surfaced to a browser: the site fetches face crops
        /// server-side and caches them into the thumb cache, so a client never learns Immich exists.</summary>
        public string? ImmichBaseUrl { get; set; }

        /// <summary>API key for the single Immich user that owns the external library (§2.4). Sent as
        /// the <c>x-api-key</c> header. Read-only in practice — the sync only ever GETs.</summary>
        public string? ImmichApiKey { get; set; }

        /// <summary>Optional: the Immich external-library id to restrict the asset sweep to. Unset means
        /// "every asset the key can see", which is correct for the single-user, single-library
        /// deployment the runbook describes (docs/photos-immich-setup.md).</summary>
        public string? ImmichLibraryId { get; set; }

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
