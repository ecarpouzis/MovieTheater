using System.Security.Cryptography;
using System.Text;

namespace MovieTheater.Core
{
    /// <summary>
    /// The signed capability that authorizes audio playback on the data plane (music-plan.md §2.1).
    /// Minted by the site's /API/Music/Stream/Start after the StreamingUser policy passes; validated
    /// by the StreamGateway's /s/{token}/MusicFile route. Lives in Core so both ends share one
    /// implementation and cannot drift (same stance as <see cref="StreamCapabilityToken"/>).
    ///
    /// Shape: base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload)) where
    /// payload = userId|trackId|relativePath|expiresUnixSeconds. The relative path (music-root-
    /// relative, forward slashes) is what the gateway serves — it holds no DB, so the capability
    /// carries everything, and the gateway only confines the resolved path to its music root.
    /// A '|' cannot appear in the path (illegal in Windows file names), so the delimiter is safe.
    /// </summary>
    public static class MusicCapabilityToken
    {
        public sealed record Payload(int UserId, int TrackId, string RelativePath, long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload)
        {
            var data = $"{payload.UserId}|{payload.TrackId}|{payload.RelativePath}|{payload.ExpiresUnixSeconds}";
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
            if (parts.Length != 4
                || !int.TryParse(parts[0], out var userId)
                || !int.TryParse(parts[1], out var trackId)
                || !long.TryParse(parts[3], out var expires))
                return false;

            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
                return false;

            payload = new Payload(userId, trackId, parts[2], expires);
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
