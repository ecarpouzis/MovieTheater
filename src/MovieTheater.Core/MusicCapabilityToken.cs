using System;

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

        public static string Mint(string secret, Payload payload) =>
            CapabilityEnvelope.Mint(secret,
                payload.UserId.ToString(), payload.TrackId.ToString(), payload.RelativePath,
                payload.ExpiresUnixSeconds.ToString());

        /// <summary>Validates signature and expiry. Returns false (payload null) on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload)
        {
            payload = null;
            if (!CapabilityEnvelope.TryOpen(secret, token, 4, TimeSpan.Zero, out var parts, out var expires))
                return false;
            if (!int.TryParse(parts![0], out var userId) || !int.TryParse(parts[1], out var trackId))
                return false;

            payload = new Payload(userId, trackId, parts[2], expires);
            return true;
        }
    }
}
