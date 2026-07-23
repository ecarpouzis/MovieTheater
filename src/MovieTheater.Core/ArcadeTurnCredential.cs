using System;
using System.Security.Cryptography;
using System.Text;

namespace MovieTheater.Core
{
    /// <summary>
    /// Mints ephemeral TURN credentials using the coturn <c>use-auth-secret</c> / TURN REST API scheme
    /// (draft-uberti-behave-turn-rest-00), which every mainstream TURN server (coturn, pion/turn)
    /// implements the same way:
    ///
    ///   <c>username   = "&lt;expiryUnixSeconds&gt;:&lt;userId&gt;"</c>
    ///   <c>credential = base64(HMAC-SHA1(sharedSecret, username))</c>
    ///
    /// The TURN server holds only the shared secret; it recomputes the same HMAC from the username the
    /// client presents to authenticate, and rejects the username once its embedded expiry passes. So
    /// nothing per-credential is stored on either side — the site mints these alongside the join
    /// descriptor and the relay validates them statelessly, mirroring how <see cref="ArcadeCapabilityToken"/>
    /// gates the WS join.
    ///
    /// HMAC-SHA1 (not SHA-256) and base64 (not base64url) are fixed by the REST-API scheme the server
    /// speaks; do not "modernize" them or authentication silently fails.
    /// </summary>
    public static class ArcadeTurnCredential
    {
        public readonly record struct Credential(string Username, string Password);

        /// <param name="nowUnixSeconds">Current time as unix seconds (passed in so callers stay testable
        /// and the mint is deterministic).</param>
        public static Credential Mint(string secret, int ttlSeconds, int userId, long nowUnixSeconds)
        {
            if (string.IsNullOrEmpty(secret))
                throw new ArgumentException("A TURN shared secret is required to mint credentials.", nameof(secret));

            var expiry = nowUnixSeconds + Math.Max(1, ttlSeconds);
            var username = $"{expiry}:{userId}";
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
            return new Credential(username, Convert.ToBase64String(mac));
        }
    }
}
