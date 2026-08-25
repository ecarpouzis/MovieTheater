using MovieTheater.Core;

namespace MovieTheater.Books.Media
{
    /// <summary>
    /// The capability behind every Books BYTE the browser fetches straight from the host (thumbnails, pages,
    /// EPUB resources, downloads): minted by the host for the identity the header established, validated by
    /// the host's <c>/m/{token}/…</c> routes. Session-scoped and long-lived (<see cref="TtlHours"/>) — one
    /// token per session, not one per asset, because a token per thumbnail would bloat every card URL and
    /// cost an HMAC per item per response. Its secret is the host's own (<c>Books:MediaTokenSecret</c>);
    /// the site never mints media URLs because every JSON that carries them already comes from the host.
    ///
    /// <para>Shape: <c>userId|maturityCeiling|isAdmin|scope|expiresUnixSeconds</c>. Thumbnails are the
    /// zero-database fast path (a leaked id reveals at most a cover); pages/EPUB/download run the item
    /// authorization (maturity + exclusion) on top of the token.</para>
    /// </summary>
    public static class BooksMediaToken
    {
        public const int TtlHours = 12;
        public const string ScopeRead = "read";

        public sealed record Payload(int UserId, int MaturityCeiling, bool IsAdmin, string Scope, long ExpiresUnixSeconds);

        public static string Mint(string secret, Payload payload) =>
            CapabilityEnvelope.Mint(secret, payload.UserId.ToString(), payload.MaturityCeiling.ToString(),
                payload.IsAdmin ? "1" : "0", payload.Scope, payload.ExpiresUnixSeconds.ToString());

        public static string MintNow(string secret, int userId, int maturityCeiling, bool isAdmin, out long expiresUnixSeconds)
        {
            expiresUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlHours * 3600;
            return Mint(secret, new Payload(userId, Math.Clamp(maturityCeiling, 0, 3), isAdmin, ScopeRead, expiresUnixSeconds));
        }

        /// <summary>Strict expiry (grace zero): the whole point of this ticket is the clock.</summary>
        public static bool TryValidate(string secret, string token, out Payload? payload)
        {
            payload = null;
            if (!CapabilityEnvelope.TryOpen(secret, token, 5, TimeSpan.Zero, out var parts, out var expires)) return false;
            if (!int.TryParse(parts![0], out var userId) || userId <= 0) return false;
            if (!int.TryParse(parts[1], out var ceiling) || ceiling < 0 || ceiling > 3) return false;
            if (parts[2] != "0" && parts[2] != "1") return false;
            if (parts[3] != ScopeRead) return false;
            payload = new Payload(userId, ceiling, parts[2] == "1", parts[3], expires);
            return true;
        }
    }

    /// <summary>The media-plane routes, named once so the host's endpoints and the URL builders in its JSON cannot drift.</summary>
    public static class BooksMediaRoutes
    {
        public const string Prefix = "/m";

        public static string ThumbUrl(string publicBaseUrl, string token, long itemId) => $"{publicBaseUrl.TrimEnd('/')}/m/{token}/thumbs/{itemId}.webp";
        public static string PageUrl(string publicBaseUrl, string token, long itemId, int page) => $"{publicBaseUrl.TrimEnd('/')}/m/{token}/pages/{itemId}/{page}";
        public static string EpubResourceUrl(string publicBaseUrl, string token, long itemId, string path) => $"{publicBaseUrl.TrimEnd('/')}/m/{token}/epub/{itemId}/{path.TrimStart('/')}";
        public static string DownloadUrl(string publicBaseUrl, string token, long itemId) => $"{publicBaseUrl.TrimEnd('/')}/m/{token}/download/{itemId}";
        public static string FolderIconUrl(string publicBaseUrl, string token, long folderId) => $"{publicBaseUrl.TrimEnd('/')}/m/{token}/folders/{folderId}/icon";

        /// <summary>
        /// The thumbnail file for an item, confined to the cache directory: the id is numeric by construction and
        /// the resolved path must still sit under the cache root — defense in depth costs one string compare.
        /// Null when the id is not a positive integer or the path escapes.
        /// </summary>
        public static string? ResolveThumb(string cacheDir, string idSegment)
        {
            if (!long.TryParse(idSegment, out var id) || id <= 0) return null;
            var root = Path.GetFullPath(cacheDir);
            var full = Path.GetFullPath(Path.Combine(root, id + ".webp"));
            return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
    }
}
