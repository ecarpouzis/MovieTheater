using System;

namespace MovieTheater.Core
{
    /// <summary>
    /// The signed identity the site pods hand the Books host on every proxied request — the ONE way a
    /// caller's identity crosses the pod↔host seam. Minted per request by the site (cookie principal +
    /// one UserSettings read), opened by the host's authentication handler; the host holds no sessions.
    ///
    /// <para>Shape: <c>userId|username|isAdmin|maturityCeiling|expiresUnixSeconds</c> — five fields, so a
    /// payload with more or fewer is refused by <see cref="CapabilityEnvelope.TryOpen"/> rather than
    /// parsed as a shifted prefix. Short-lived (<see cref="TtlSeconds"/>) with a small grace
    /// (<see cref="Grace"/>): two machines' clocks disagree by seconds, not minutes, and a header that
    /// arrives a moment late is a proxy hop, not an attack.</para>
    ///
    /// <para>Kids style, the user's name for display and everything else the SPA shows come from
    /// <c>/API/Me</c>; the host needs exactly these four facts to authorize and cache.</para>
    /// </summary>
    public static class BooksIdentityToken
    {
        public const string HeaderName = "X-MT-Identity";
        public const int TtlSeconds = 60;
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

        public sealed record Payload(int UserId, string Username, bool IsAdmin, int MaturityCeiling, long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload) =>
            CapabilityEnvelope.Mint(secret,
                payload.UserId.ToString(), payload.Username, payload.IsAdmin ? "1" : "0",
                payload.MaturityCeiling.ToString(), payload.ExpiresUnixSeconds.ToString());

        /// <summary>Mint for the caller right now, expiring <see cref="TtlSeconds"/> from now.</summary>
        public static string MintNow(string secret, int userId, string username, bool isAdmin, int maturityCeiling) =>
            Mint(secret, new Payload(userId, username, isAdmin, Math.Clamp(maturityCeiling, 0, 3),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds));

        /// <summary>Validates signature, field count and expiry (with <see cref="Grace"/>). False on any defect.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload) =>
            TryValidate(secret, token, Grace, out payload);

        public static bool TryValidate(string secret, string token, TimeSpan expiryGrace, out Payload? payload)
        {
            payload = null;
            if (!CapabilityEnvelope.TryOpen(secret, token, 5, expiryGrace, out var parts, out var expires))
                return false;
            if (!int.TryParse(parts![0], out var userId) || userId <= 0) return false;
            if (string.IsNullOrEmpty(parts[1])) return false;
            if (parts[2] != "0" && parts[2] != "1") return false;
            if (!int.TryParse(parts[3], out var ceiling) || ceiling < 0 || ceiling > 3) return false;
            payload = new Payload(userId, parts[1], parts[2] == "1", ceiling, expires);
            return true;
        }
    }
}
