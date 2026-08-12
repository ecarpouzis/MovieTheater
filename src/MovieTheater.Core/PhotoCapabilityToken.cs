using System;

namespace MovieTheater.Core
{
    /// <summary>
    /// The signed capability that authorizes ONE photo derivative or original on the data plane
    /// (photos-plan.md §2.2). Minted by the site only for a session that passed
    /// <c>RequireFamilyAlbum</c>; validated by the StreamGateway's PhotoThumb/PhotoOriginal routes.
    /// Lives in Core so both ends share one implementation and cannot drift — the
    /// <see cref="MusicCapabilityToken"/> stance.
    ///
    /// <para>Shape: base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload)) where
    /// payload = userId|assetId|relativePath|size|expiresUnixSeconds. The relative path is
    /// root-relative with forward slashes — collection-root-relative for
    /// <see cref="PhotoStreamRoutes.SizeOriginal"/>, thumb-cache-relative for every derivative — so
    /// nothing absolute is ever on the wire and the gateway joins it onto its OWN mount. A '|' cannot
    /// appear in a Windows file name, so the delimiter is safe.</para>
    ///
    /// <para><b>Photo bytes are gated, not just metadata</b> (§2.1): unlike movie posters, which /Image
    /// serves openly, every pixel here needs a live capability. Tokens are therefore short-lived, and
    /// validation is signature THEN expiry THEN the caller's own root confinement — the signature
    /// proves the site minted it, the confinement check is what makes a forged-path bug non-fatal.</para>
    /// </summary>
    public static class PhotoCapabilityToken
    {
        public sealed record Payload(int UserId, int AssetId, string RelativePath, string Size, long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload) =>
            CapabilityEnvelope.Mint(secret,
                payload.UserId.ToString(), payload.AssetId.ToString(), payload.RelativePath,
                payload.Size, payload.ExpiresUnixSeconds.ToString());

        /// <summary>Validates signature and expiry. Returns false (payload null) on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload)
        {
            payload = null;
            if (!CapabilityEnvelope.TryOpen(secret, token, 5, TimeSpan.Zero, out var parts, out var expires))
                return false;
            if (!int.TryParse(parts![0], out var userId) || !int.TryParse(parts[1], out var assetId))
                return false;

            payload = new Payload(userId, assetId, parts[2], parts[3], expires);
            return true;
        }
    }
}
