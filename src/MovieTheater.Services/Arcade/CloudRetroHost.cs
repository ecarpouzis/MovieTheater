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
            // zone is empty in single-box v1 (no multi-zone worker selection).
            var roomIdParam = Uri.EscapeDataString(cloudRetroRoomId ?? string.Empty);
            var wsUrl = $"{wsBase}/w/{token}?room_id={roomIdParam}&zone=";

            var ice = (config.ArcadeStunServers ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => new ArcadeIceServer(s))
                .ToList();

            return new ArcadeJoinDescriptor(roomCode, wsUrl, playerSlot, game.CloudRetroGameKey, ice, isCreator);
        }

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
