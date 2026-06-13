using System.Security.Cryptography;
using System.Text;

namespace MovieTheater.Core
{
    /// <summary>
    /// The signed capability that authorizes the public data plane (streaming-plan.md
    /// §3.3). Minted by the site after the StreamingUser policy + age gate pass;
    /// validated by the StreamGateway on every playlist/segment request. Lives in Core
    /// so both ends share one implementation and cannot drift.
    ///
    /// Shape: base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload))
    /// where payload = userId|movieId|playSessionId|itemId|expiresUnixSeconds.
    /// The token rides the URL *path* (/s/{token}/Videos/…) so Jellyfin's relative
    /// segment URIs inherit it — no playlist rewriting, no custom headers.
    /// </summary>
    public static class StreamCapabilityToken
    {
        public sealed record Payload(int UserId, int MovieId, string PlaySessionId, string ItemId, long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload)
        {
            var data = $"{payload.UserId}|{payload.MovieId}|{payload.PlaySessionId}|{payload.ItemId}|{payload.ExpiresUnixSeconds}";
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = Sign(secret, dataBytes);
            return $"{Base64UrlEncode(dataBytes)}.{Base64UrlEncode(signature)}";
        }

        /// <summary>Validates signature and expiry. Returns false (payload null) on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload)
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
            if (parts.Length != 5
                || !int.TryParse(parts[0], out var userId)
                || !int.TryParse(parts[1], out var movieId)
                || !long.TryParse(parts[4], out var expires))
                return false;

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
                return false;

            payload = new Payload(userId, movieId, parts[2], parts[3], expires);
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
