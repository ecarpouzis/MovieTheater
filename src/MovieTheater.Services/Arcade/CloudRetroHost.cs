using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Core;

namespace MovieTheater.Services.Arcade
{
    /// <summary>
    /// The v1 <see cref="IArcadeHost"/> — CloudRetro. It mints the <see cref="ArcadeCapabilityToken"/>
    /// the ArcadeGateway validates and assembles the browser's join descriptor. It never talks to the
    /// coordinator (§3 asymmetry): the browser is the intermediary. Tolerates missing config so the
    /// site boots and runs with the arcade simply switched off, mirroring the Jellyfin registration.
    /// </summary>
    public sealed class CloudRetroHost : IArcadeHost
    {
        private readonly MovieTheaterConfiguration config;

        public CloudRetroHost(MovieTheaterConfiguration config)
        {
            this.config = config;
        }

        public bool IsConfigured =>
            !string.IsNullOrEmpty(config.ArcadeGatewayBaseUrl) && !string.IsNullOrEmpty(config.ArcadeTokenSecret);

        public int MaxConcurrentRooms => config.ArcadeMaxConcurrentRooms;

        /// <summary>
        /// Heavy titles that ALSO play in the browser via the capture lane (H5,
        /// docs/arcade-capture-worker-plan.md). Gated by an explicit allowlist — NOT by
        /// CloudRetroGameKey, because every heavy row already carries that (it is the title's
        /// heavy descriptor id / launch key), so it can't distinguish "has a capture worker stub".
        /// Each key here MUST have a matching <c>&lt;key&gt;.capture</c> stub on the capture worker
        /// (D:\ArcadeStorage\heavy\capture-stubs) and a heavy descriptor — add both together.
        /// The Artemis/Moonlight launch is unaffected either way (both lanes coexist).
        /// </summary>
        public static readonly IReadOnlySet<string> CaptureEnabledKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "switch-kirby-forgotten-land",
            };

        /// <summary>True when a heavy title's key is capture-enabled (has a browser capture stub).</summary>
        public static bool IsCaptureEnabled(string? cloudRetroGameKey) =>
            !string.IsNullOrEmpty(cloudRetroGameKey) && CaptureEnabledKeys.Contains(cloudRetroGameKey);

        /// <summary>
        /// Systems whose worker config declares a per-core <c>hwContext</c> (GL vs Vulkan render
        /// path) — i.e. the ones where a per-launch hw-context override is meaningful at all. Must be
        /// kept in lockstep with every <c>hwContext:</c> core entry in
        /// docker/arcade/config.worker-gl.yaml (currently pcsx/ps1, psp, dc, naomi, atomiswave, ps2,
        /// gc, wii, n64). 2D systems and heavy/capture titles have no such choice.
        /// </summary>
        public static readonly IReadOnlySet<string> HwToggleSystems =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ps1", "ps2", "psp", "n64", "gc", "wii", "dc", "naomi", "atomiswave",
            };

        /// <summary>True when a system supports a per-launch GL/Vulkan hw-context override.</summary>
        public static bool SupportsHwToggle(string? system) =>
            !string.IsNullOrEmpty(system) && HwToggleSystems.Contains(system);

        /// <summary>The GC-controller-native Wii SD-loader BrawlEx mods (like real Super Smash Bros
        /// Brawl, these are traditionally played with a GameCube controller, not Wiimote+Nunchuk) —
        /// keyed by title, since each currently has exactly one version/ROM. Must be kept in sync
        /// with docker/arcade/config.worker-gl.yaml's <c>hid4rom</c> entries AND
        /// cloudRetroClient.js's <c>GC_ON_WII_GAME_KEYS</c> (the client's input-profile mirror of
        /// the same list) — a title added to one but not the others either offers a scheme picker
        /// that does nothing, or applies a scheme the client never sends the right button bits for.
        /// </summary>
        public static readonly IReadOnlySet<string> GcOnWiiGameTitles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Project REX", "Super Smash Bros Infinite",
            };

        /// <summary>True when a game supports a per-room Wii controller-scheme override (GameCube
        /// vs Wiimote+Nunchuk) — lets a room creator opt a GC-native BrawlEx mod back into
        /// Wiimote+Nunchuk for players with real Wii Remotes/Nunchuks instead of a GameCube
        /// controller/adapter.</summary>
        public static bool SupportsControllerScheme(string? title) =>
            !string.IsNullOrEmpty(title) && GcOnWiiGameTitles.Contains(title);

        public ArcadeJoinDescriptor BuildJoinDescriptor(
            int userId, ArcadeGameDescriptor game, string roomCode, string cloudRetroRoomId, int playerSlot, bool isCreator)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("Arcade is not configured on this server.");

            var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(1, config.ArcadeJoinTokenTtlSeconds);
            var token = ArcadeCapabilityToken.Mint(config.ArcadeTokenSecret!, new ArcadeCapabilityToken.Payload(
                userId, game.Id, roomCode, cloudRetroRoomId ?? string.Empty, playerSlot, expires));

            // Gateway base is https/http; the browser opens a WebSocket, so advertise wss/ws.
            var wsBase = ToWebSocketScheme(config.ArcadeGatewayBaseUrl!.TrimEnd('/'));
            // The room id embeds '___' and spaces — URL-encode it. Empty for a creator (⇒ "create").
            var roomIdParam = Uri.EscapeDataString(cloudRetroRoomId ?? string.Empty);
            // ALWAYS send a zone. It used to be gated on ArcadeZoningEnabled (default off), which sent
            // zone="" — a WILDCARD that matches any worker. With two pools that is a live misrouting bug,
            // not a latent one: the coordinator hands the room to whatever worker is free FIRST, so a
            // GameCube room lands on the CAPTURE worker (library = capture stubs), which answers
            // "couldn't find game info" and the player is told "the arcade is full" while both GL workers
            // sit idle. Seen 2026-07-13, repeatedly. It misroutes the other way too — a capture title onto
            // a retro worker fails identically.
            var zone = Uri.EscapeDataString(ZoneForSystem(game.System));
            var wsUrl = $"{wsBase}/w/{token}?room_id={roomIdParam}&zone={zone}";

            var ice = (config.ArcadeStunServers ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => new ArcadeIceServer(s))
                .ToList();

            return new ArcadeJoinDescriptor(roomCode, wsUrl, playerSlot, game.CloudRetroGameKey, ice, isCreator, game.System);
        }

        /// <summary>
        /// Worker-pool zone for a system (roadmap WS-B). The GL 3D cores (flycast: dc/naomi/atomiswave,
        /// ppsspp: psp) need real desktop OpenGL, which only the Windows-native worker provides — they
        /// route to "gl". Everything 2D + N64 (mupen runs fine on the WSL D3D12 path) stays on "main".
        /// Keep this list in lockstep with the gl worker's config.yaml core entries and the WSL workers'
        /// CLOUD_GAME_WORKER_NETWORK_ZONE=main.
        /// </summary>
        /// The old "gl" zone is GONE, and its removal is the point. Since the docker/WSL pool was retired
        /// every retro worker registers <c>CLOUD_GAME_WORKER_NETWORK_ZONE=main</c> (run-arcade-glworker.ps1)
        /// and the capture worker registers <c>capture</c> — so the only two zones that EXIST are "main"
        /// and "capture". Routing psp/dc/naomi/atomiswave at a "gl" pool would send them at nothing.
        ///
        /// The previous comment called this a latent trap, harmless because zoning was off. It was not:
        /// zoning-off sends zone="" (a wildcard), and the coordinator then picks whatever worker is free
        /// first — which is how a GameCube room ended up on the CAPTURE worker, failed with "couldn't find
        /// game info", and told the player "the arcade is full" with both retro workers idle.
        ///
        /// Two zones, two pools, no wildcard. Keep in lockstep with the workers' registered zones.
        internal static string ZoneForSystem(string? system) =>
            string.Equals(system, "capture", StringComparison.OrdinalIgnoreCase) ? "capture" : "main";

        /// <summary>
        /// Default video bitrate (kbps) for a system when the creator leaves stream quality on "Auto"
        /// (docs/arcade-quality-plan.md Phase 5). There is one encoder per room, so this is what everyone
        /// in the room gets; an explicit lobby choice always overrides it.
        ///
        /// Bitrate has to track ENCODED RESOLUTION, which differs ~4.6x across systems. Before this, every
        /// room got a flat 5 Mbps whether it carried a 912x672 arcade board or a 1280x1056 GameCube frame —
        /// the latter at ~0.06 bits/pixel/frame, which is starved. The resolutions below were MEASURED live
        /// (2026-07-08); keep them in step with the cores' options in docker/arcade/config.worker-gl.yaml.
        ///
        /// This is a CEILING, not a fixed rate. Worker patch 0021 (ABR) drives the encoder between a floor
        /// and this value from the worst peer's send-side bandwidth estimate, so a generous ceiling costs a
        /// remote friend on a thin uplink nothing — ABR backs off within a second. That is what allows the
        /// cap below to sit above the lobby's "Max" preset; before ABR it could not.
        ///
        ///   floor 5000 — the old flat default, so "Auto" is never WORSE than what shipped before.
        ///   cap  14000 — what the biggest frame we encode actually deserves (~0.17 bits/pixel/frame).
        ///
        /// 2D cores are cheap despite large frames: their `scale:` upscale is integer nearest-neighbour and
        /// adds no high-frequency detail — the encoder sees flat NxN blocks. Native-3D frames are not.
        /// </summary>
        public static int DefaultVideoBitrateKbps(string? system) => system?.ToLowerInvariant() switch
        {
            // Capture lane (H5): full 1080p desktop of a native heavy title (yuzu/RPCS3). ~2.07 Mpx, the
            // largest frame we encode — 12 Mbps ≈ 0.09 bpp, with ABR (0021) backing off for thin uplinks.
            "capture" => 12000,
            // Real 3D detail, ordered by encoded pixels per frame. All measured live 2026-07-09.
            "gc" => 14000,  // 1280x1056 = 1.35 Mpx; at 5 Mbps it ran ~0.06 bpp
            "ps2" => 12000, // 1280x896  = 1.15 Mpx after the 2x upscale. NOT optional: at the old 6 Mbps
                            // ceiling God of War's jitter buffer sat at ~95 ms with erratic fps; at 12 Mbps
                            // it is 13 ms and a locked 60. Upscale and bitrate ship together or not at all.
            "n64" => 11000, // 1280x960 = 1.23 Mpx (rendered 1920x1440, supersampled down)
            "psp" => 7000,  //  960x544  = 0.52 Mpx (much of it 30fps content)
            "dc" => 11000,  // 1280x960 = 1.23 Mpx (rendered 1920x1440, supersampled down). flycast always
                            // rendered big; CloudRetro nearest-downscaled it to 640x480 until 2026-07-09.
            // naomi/atomiswave are the same flycast pipeline as dc (same render, same delivery) — kept in
            // lockstep so enabling their catalogs doesn't ship 3D at the 2D default.
            "naomi" or "atomiswave" => 11000,
            // PS1 encodes swing 1024x960 <-> 2048x960 mid-game since the enhanced-resolution renderer
            // (2026-07-09): REAL detail is the pre-doubling half of that (the encode is its nearest 2x,
            // cheap bits), so ~0.3-0.5 Mpx of true 3D — 6000 covers it with margin.
            "ps1" => 6000,
            // Everything 2D: nearest-upscaled flat blocks. 5 Mbps is already generous.
            _ => 5000,
        };

        private static string ToWebSocketScheme(string url)
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "wss://" + url["https://".Length..];
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "ws://" + url["http://".Length..];
            return url; // already ws/wss, or scheme-relative — leave as-is
        }
    }
}
