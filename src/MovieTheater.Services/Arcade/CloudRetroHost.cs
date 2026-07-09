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
        /// Two deliberate bounds:
        ///   floor 5000 — the previous flat default, so "Auto" can never be WORSE than what shipped before.
        ///   cap  10000 — the lobby's existing "Max" preset, so "Auto" never exceeds a value a user could
        ///                already have picked. This matters because CloudRetro does no congestion control
        ///                (the encoder ignores REMB/TWCC), so an over-high bitrate only hurts remote
        ///                players. Lifting this cap is exactly what Phase 6 (ABR) is for.
        ///
        /// 2D cores are cheap despite large frames: their `scale:` upscale is integer nearest-neighbour and
        /// adds no high-frequency detail — the encoder sees flat NxN blocks. Native-3D frames are not.
        /// </summary>
        public static int DefaultVideoBitrateKbps(string? system) => system?.ToLowerInvariant() switch
        {
            // Real 3D detail, ordered by encoded pixels per frame.
            "gc" => 10000,          // 1280x1056 = 1.35 Mpx — by far the most starved at a flat 5 Mbps
            "n64" => 8000,          //  960x720  = 0.69 Mpx
            "psp" => 7000,          //  960x544  = 0.52 Mpx (much of it 30fps content)
            "ps2" or "dc" => 6000,  //  ~640x460 = 0.29 Mpx native 3D; raise when/if they upscale
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
