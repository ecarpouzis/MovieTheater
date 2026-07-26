using System.Security.Cryptography;
using System.Text;

namespace MovieTheater.Core
{
    /// <summary>
    /// The signed capability that authorizes an arcade WebSocket join (arcade-plan.md
    /// Appendix D1). CloudRetro has no auth of its own, so the coordinator is never
    /// exposed raw: the site mints one of these after the StreamingUser policy + age
    /// gate pass, and the ArcadeGateway validates the HMAC + expiry on the WS upgrade
    /// before forwarding to the coordinator. A direct clone of
    /// <see cref="StreamCapabilityToken"/> so both ends share one implementation and
    /// cannot drift.
    ///
    /// Shape: base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload))
    /// where payload = userId|gameId|roomCode|base64url(cloudRetroRoomId)|playerSlot|expiresUnixSeconds.
    ///
    /// The token rides the URL *path* (/w/{token}) so the browser client — which
    /// hardcodes the coordinator WS path to /ws — never has to carry it in a header the
    /// stock client won't send. CloudRetroRoomId is base64url'd *inside* the payload
    /// because a CloudRetro room id embeds the game title ("&lt;hex&gt;___&lt;title&gt;") and a
    /// title could in principle contain the '|' field separator; it is empty for the
    /// room creator (who is creating, not joining — Appendix C1 uses it to confine a
    /// joiner to exactly one room).
    ///
    /// (Phase-6 solo play reuses this class with the room fields empty and a ROM path in
    /// place of the room id — see docs/emulatorjs-plan.md §5; not built here.)
    /// </summary>
    public static class ArcadeCapabilityToken
    {
        public sealed record Payload(
            int UserId,
            int GameId,
            string RoomCode,
            string CloudRetroRoomId,
            int PlayerSlot,
            long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload)
        {
            var roomId = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.CloudRetroRoomId ?? string.Empty));
            var data = $"{payload.UserId}|{payload.GameId}|{payload.RoomCode}|{roomId}|{payload.PlayerSlot}|{payload.ExpiresUnixSeconds}";
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = Sign(secret, dataBytes);
            return $"{Base64UrlEncode(dataBytes)}.{Base64UrlEncode(signature)}";
        }

        /// <summary>Validates signature and expiry. Returns false (payload null) on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload) =>
            TryValidate(secret, token, TimeSpan.Zero, out payload);

        /// <summary>
        /// Validates signature and expiry, allowing the token to be <paramref name="expiryGrace"/> past
        /// its stamped expiry.
        ///
        /// The grace exists for capabilities whose real bound is not a clock but a LIVE ROOM: the in-room
        /// control calls (quicksave / snapshot / load) name one ephemeral CloudRetro room id, which stops
        /// existing when the room does. A 5-minute TTL on those buys nothing an attacker couldn't have had
        /// while the room was open, and it has broken saving repeatedly — the page holds one token for a
        /// multi-hour session, and every path that refreshes it (presence bookkeeping, the heartbeat, the
        /// site pod itself) is something that can fail while the game keeps playing perfectly.
        ///
        /// The WS connect and the ROM fetch keep the strict check (grace zero): those are the tickets that
        /// let someone INTO a room, and there the clock is the point.
        /// </summary>
        public static bool TryValidate(string secret, string token, TimeSpan expiryGrace, out Payload? payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(token))
                return false;

            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1)
                return false;

            byte[] dataBytes;
            byte[] givenSignature;
            try
            {
                dataBytes = Base64UrlDecode(token[..dot]);
                givenSignature = Base64UrlDecode(token[(dot + 1)..]);
            }
            catch (FormatException)
            {
                return false;
            }

            var expectedSignature = Sign(secret, dataBytes);
            if (!CryptographicOperations.FixedTimeEquals(givenSignature, expectedSignature))
                return false;

            var parts = Encoding.UTF8.GetString(dataBytes).Split('|');
            if (parts.Length != 6
                || !int.TryParse(parts[0], out var userId)
                || !int.TryParse(parts[1], out var gameId)
                || !int.TryParse(parts[4], out var playerSlot)
                || !long.TryParse(parts[5], out var expires))
                return false;

            string cloudRetroRoomId;
            try
            {
                cloudRetroRoomId = Encoding.UTF8.GetString(Base64UrlDecode(parts[3]));
            }
            catch (FormatException)
            {
                return false;
            }

            var graceSeconds = (long)Math.Max(0, expiryGrace.TotalSeconds);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires + graceSeconds)
                return false;

            payload = new Payload(userId, gameId, parts[2], cloudRetroRoomId, playerSlot, expires);
            return true;
        }

        private static byte[] Sign(string secret, byte[] data)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return hmac.ComputeHash(data);
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string s)
        {
            var padded = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
        }
    }
}
