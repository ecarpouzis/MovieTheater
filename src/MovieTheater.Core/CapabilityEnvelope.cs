using System;
using System.Security.Cryptography;
using System.Text;

namespace MovieTheater.Core
{
    /// <summary>
    /// The wire format every signed capability in this repo shares — streaming, arcade, music and
    /// photos — and the only place its crypto is written.
    ///
    /// <para>Shape: <c>base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload))</c>, where the
    /// payload is <c>'|'</c>-separated fields whose LAST field is always the expiry in Unix seconds. The
    /// token rides the URL PATH in every lane, which is what lets Jellyfin's relative segment URIs and
    /// CloudRetro's hardcoded <c>/ws</c> inherit it without a rewritten playlist or a custom header.</para>
    ///
    /// <para><b>Why it is one class.</b> Four near-identical copies of the same forty lines existed, one
    /// per lane, each with its own <c>Sign</c>, its own base64url pair and its own hand-written envelope
    /// parse. They agreed, which is exactly the problem: a fix to any of them — a padding edge case, a
    /// missing fixed-time compare, a field-count check — would have been a fix to one lane's security
    /// boundary and a silent divergence for the other three. The per-lane classes keep what is genuinely
    /// per-lane (the payload record and what its fields MEAN) and share what is not.</para>
    ///
    /// <para><b>The field count is checked, not assumed.</b> A <c>'|'</c> cannot appear in a Windows file
    /// name, so it is a safe delimiter — but a payload that arrived with an extra one must be REFUSED
    /// rather than parsed as a shifted prefix, which is a different token with a valid signature.</para>
    /// </summary>
    public static class CapabilityEnvelope
    {
        /// <summary>Signs <paramref name="fields"/> (joined with '|') into a token. The last field must be
        /// the expiry in Unix seconds — <see cref="TryOpen"/> reads it from there.</summary>
        public static string Mint(string secret, params string[] fields)
        {
            var dataBytes = Encoding.UTF8.GetBytes(string.Join("|", fields));
            return $"{Base64UrlEncode(dataBytes)}.{Base64UrlEncode(Sign(secret, dataBytes))}";
        }

        /// <summary>
        /// Verifies signature, field count and expiry, and hands back the raw fields for the caller to
        /// interpret. False (with null fields) on any defect — a malformed token is never a throw.
        /// </summary>
        /// <param name="expiryGrace">
        /// How far past the stamped expiry a token is still accepted. Zero for every ticket whose whole
        /// point is the clock; non-zero only where the real bound is a live resource rather than a time
        /// (see <c>ArcadeCapabilityToken</c>'s in-room control calls).
        /// </param>
        public static bool TryOpen(string secret, string token, int fieldCount, TimeSpan expiryGrace,
            out string[]? fields, out long expiresUnixSeconds)
        {
            fields = null;
            expiresUnixSeconds = 0;
            if (string.IsNullOrEmpty(token)) return false;

            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1) return false;

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

            // Fixed-time compare: a signature check that returns early on the first differing byte is a
            // timing oracle for forging one.
            if (!CryptographicOperations.FixedTimeEquals(givenSignature, Sign(secret, dataBytes)))
                return false;

            var parts = Encoding.UTF8.GetString(dataBytes).Split('|');
            if (parts.Length != fieldCount || !long.TryParse(parts[fieldCount - 1], out var expires))
                return false;

            var graceSeconds = (long)Math.Max(0, expiryGrace.TotalSeconds);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires + graceSeconds) return false;

            fields = parts;
            expiresUnixSeconds = expires;
            return true;
        }

        private static byte[] Sign(string secret, byte[] data)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return hmac.ComputeHash(data);
        }

        /// <summary>Public because one payload nests an encoded value inside a field: a CloudRetro room
        /// id embeds a game title, and a title could in principle contain the '|' separator.</summary>
        public static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Base64UrlDecode(string s)
        {
            var padded = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
        }
    }
}
