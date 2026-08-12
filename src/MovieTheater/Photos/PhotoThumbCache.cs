using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Core;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Naming for the pre-generated derivative cache (photos-plan.md §2.2). The ingest writes these
    /// files; the StreamGateway only ever joins the token's relative path onto its own
    /// <c>PhotoThumbCacheDir</c> and serves the bytes — it holds no database and generates nothing, so
    /// every naming rule has to live on this side.
    ///
    /// <para><b>Keyed by asset id + content hash</b>, which is what makes a re-ingest safe: the id
    /// gives a stable owner, and a changed file produces a different key, so the URL the browser
    /// cached for the old bytes can never resolve to the new ones. Sharded by id/1000 to keep any one
    /// directory in the low thousands of entries at the planned 50k–150k scale.</para>
    /// </summary>
    public static class PhotoThumbCache
    {
        /// <summary>Longest edge, in pixels, per derivative (§2.2).</summary>
        public static int MaxEdgeFor(string size) => size switch
        {
            PhotoStreamRoutes.SizeGrid => 400,
            PhotoStreamRoutes.SizeView => 1600,
            PhotoStreamRoutes.SizeZoom => 3200,
            _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Not a derivative size."),
        };

        /// <summary>Cache-root-relative, forward slashes — the exact string a
        /// <see cref="PhotoCapabilityToken"/> for <see cref="PhotoStreamRoutes.Thumb"/> carries.</summary>
        public static string RelativePath(int assetId, string thumbKey, string size) =>
            $"{assetId / 1000}/{assetId}-{thumbKey}-{size}.webp";

        /// <summary>
        /// Derivatives for a GOOGLE-ONLY Takeout item (§2.10, Phase 6) — a picture that has no
        /// <c>PhotoAsset</c> at all, because the whole point of the review list is that we do not own it
        /// yet. Its own <c>google/</c> namespace, so an item id and an asset id can never name the same
        /// file: they are different id spaces over different tables, and one cache directory serves
        /// both.
        ///
        /// <para>The gateway needs no change for this. It joins the token's relative path onto its
        /// thumb-cache mount and serves the bytes; a deeper relative path is still a relative path
        /// inside the same root (<c>PhotoPathConfinement</c> enforces exactly that).</para>
        /// </summary>
        public static string GoogleRelativePath(int googleItemId, string thumbKey, string size) =>
            $"google/{googleItemId / 1000}/{googleItemId}-{thumbKey}-{size}.webp";

        /// <summary>Derivatives a Google-only item gets: the grid card for the review list and the view
        /// size for looking at one properly. Never <c>zoom</c> — deep zoom exists for originals a browser
        /// cannot render (§2.2), and nothing here is an original we hold.</summary>
        public static readonly IReadOnlyList<string> GoogleVariants =
            new[] { PhotoStreamRoutes.SizeGrid, PhotoStreamRoutes.SizeView };

        /// <summary>
        /// The content key the derivatives are named with: the first 16 hex digits of the file's
        /// SHA-256 when the hash pass has run, otherwise a digest of the size+mtime the walk recorded.
        /// The fallback exists so thumbs can be generated before hashing — the passes are independent
        /// queues by design — and it changes whenever the bytes change, which is all the key has to do.
        /// </summary>
        public static string KeyFor(string? sha256, long sizeBytes, DateTime fileModifiedUtc)
        {
            if (!string.IsNullOrEmpty(sha256) && sha256!.Length >= 16)
                return sha256.Substring(0, 16).ToLowerInvariant();

            var seed = System.Text.Encoding.UTF8.GetBytes($"{sizeBytes}|{fileModifiedUtc.Ticks}");
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(seed))
                .Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>Which derivatives an asset should have. Renderable originals deep-zoom straight
        /// from <c>PhotoOriginal</c>; the rest need the 3200px <c>zoom</c> because no browser can open
        /// their original at all (§2.2).</summary>
        public static IReadOnlyList<string> VariantsFor(bool originalRenderable) =>
            originalRenderable
                ? new[] { PhotoStreamRoutes.SizeGrid, PhotoStreamRoutes.SizeView }
                : new[] { PhotoStreamRoutes.SizeGrid, PhotoStreamRoutes.SizeView, PhotoStreamRoutes.SizeZoom };

        public static string Join(IEnumerable<string> variants) => string.Join(",", variants);

        public static bool Has(string? variants, string size) =>
            !string.IsNullOrEmpty(variants)
            && variants!.Split(',').Any(v => string.Equals(v, size, StringComparison.OrdinalIgnoreCase));
    }
}
