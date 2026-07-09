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
            // zone selects the worker pool (roadmap WS-B). Empty (v1 default, ArcadeZoningEnabled=false)
            // is a wildcard that matches any worker; when zoning is on, GL 3D systems go to the "gl"
            // pool (the Windows-native worker) and everything else to "main" (the WSL 2D workers).
            var roomIdParam = Uri.EscapeDataString(cloudRetroRoomId ?? string.Empty);
            var zone = config.ArcadeZoningEnabled ? Uri.EscapeDataString(ZoneForSystem(game.System)) : string.Empty;
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
        /// ⚠ STALE + LATENT TRAP (noted 2026-07-08, deliberately not changed here). Since the docker/WSL
        /// pool was retired, BOTH Windows worker tasks register <c>CLOUD_GAME_WORKER_NETWORK_ZONE=main</c>
        /// (scripts/run-arcade-glworker.ps1) — so **no worker serves the "gl" zone**. Turning on
        /// <c>ArcadeZoningEnabled</c> today would route psp/dc/naomi/atomiswave rooms at a pool that does
        /// not exist. The list is also incomplete: ps2 and gc are GL cores too and were never added.
        /// It is harmless only because zoning is off by default (the descriptor then sends zone=""), which
        /// is a wildcard. Fix by deleting zoning, or by making every system return "main", before anyone
        /// flips that flag.
        internal static string ZoneForSystem(string? system) => system?.ToLowerInvariant() switch
        {
            "psp" or "dc" or "naomi" or "atomiswave" => "gl",
            _ => "main",
        };

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
            // Real 3D detail, ordered by encoded pixels per frame. All measured live 2026-07-09.
            "gc" => 14000,  // 1280x1056 = 1.35 Mpx; at 5 Mbps it ran ~0.06 bpp
            "ps2" => 12000, // 1280x896  = 1.15 Mpx after the 2x upscale. NOT optional: at the old 6 Mbps
                            // ceiling God of War's jitter buffer sat at ~95 ms with erratic fps; at 12 Mbps
                            // it is 13 ms and a locked 60. Upscale and bitrate ship together or not at all.
            "n64" => 9000,  //  960x720  = 0.69 Mpx
            "psp" => 7000,  //  960x544  = 0.52 Mpx (much of it 30fps content)
            "dc" => 11000,  // 1280x960 = 1.23 Mpx. flycast ALWAYS rendered this; CloudRetro was nearest-
                            // downscaling it to 640x480 and discarding 3 of 4 samples until 2026-07-09.
            // PS1 swings 512x480 <-> 1280x960 mid-game, but the frame is a nearest 2x upscale (cheap bits).
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
