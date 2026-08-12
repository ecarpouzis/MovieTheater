using System;

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

        public static string Mint(string secret, Payload payload) =>
            CapabilityEnvelope.Mint(secret,
                payload.UserId.ToString(), payload.MovieId.ToString(), payload.PlaySessionId,
                payload.ItemId, payload.ExpiresUnixSeconds.ToString());

        /// <summary>Validates signature and expiry. Returns false (payload null) on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload)
        {
            payload = null;
            if (!CapabilityEnvelope.TryOpen(secret, token, 5, TimeSpan.Zero, out var parts, out var expires))
                return false;
            if (!int.TryParse(parts![0], out var userId) || !int.TryParse(parts[1], out var movieId))
                return false;

            payload = new Payload(userId, movieId, parts[2], parts[3], expires);
            return true;
        }
    }
}
