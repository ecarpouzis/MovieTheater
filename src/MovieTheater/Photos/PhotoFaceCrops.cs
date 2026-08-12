using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MovieTheater.Services;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Face crops for the tag queue (docs/photos-plan.md §2.4: "face-crop images for the suggestion UI
    /// are fetched server-side from the Immich API — never exposing Immich itself — and cached into our
    /// thumb cache like any other derivative").
    ///
    /// <para><b>The site never exposes Immich.</b> The bytes are pulled server-side by whichever process
    /// can actually reach the LAN-only sidecar (the sync CLI on the gateway-adjacent host, or the site
    /// itself when it happens to be co-located) and written into the SAME derivative cache the gateway
    /// already serves. The browser is then handed an ordinary capability URL for a file in that cache —
    /// it never learns a sidecar was involved, and no Immich URL, host or key crosses the wire.</para>
    ///
    /// <para><b>Degradation is the default, not the fallback.</b> When no cached crop exists — Immich
    /// unreachable, never deployed, or its container thrown away — the queue draws the face box over our
    /// own <c>view</c> derivative instead. The box fractions live on the tag row precisely so that works
    /// with the sidecar absent (§2.8), so a missing crop is a slightly plainer card, never a broken
    /// image and never a dead button.</para>
    ///
    /// <para>Keyed by the Immich cluster id, which is the id the crop actually belongs to: the tested
    /// API serves one representative crop per PERSON, and the per-asset face is drawn from its box over
    /// our own pixels rather than fetched. Hashed into the name so an opaque upstream id can never
    /// escape the cache directory (the confinement rule the data plane is built on).</para>
    /// </summary>
    public static class PhotoFaceCrops
    {
        /// <summary>Subdirectory of the derivative cache the crops live in — beside, never inside, the
        /// id-sharded asset derivatives, so a cache sweep can drop them independently.</summary>
        public const string Folder = "faces";

        /// <summary>Cache-root-relative path, forward slashes — exactly the string a capability token
        /// for the <c>PhotoThumb</c> route carries.</summary>
        public static string RelativePath(string immichPersonId) =>
            $"{Folder}/{Key(immichPersonId)}.jpg";

        /// <summary>A stable, filesystem-safe name for an opaque upstream id. Hashed rather than
        /// sanitized: a sanitizer has to be right about every character an unknown id might contain,
        /// and a hash simply cannot contain one.</summary>
        public static string Key(string immichPersonId)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(immichPersonId ?? ""));
            return Convert.ToHexString(bytes).Substring(0, 24).ToLowerInvariant();
        }

        public static string? FullPath(string? thumbCacheDir, string immichPersonId)
        {
            if (string.IsNullOrWhiteSpace(thumbCacheDir)) return null;
            return Path.Combine(thumbCacheDir!,
                RelativePath(immichPersonId).Replace('/', Path.DirectorySeparatorChar));
        }

        public static bool Exists(string? thumbCacheDir, string immichPersonId)
        {
            var full = FullPath(thumbCacheDir, immichPersonId);
            return full != null && File.Exists(full);
        }

        /// <summary>
        /// Ensures a cluster's crop is on disk, fetching it once. Returns the cache-relative path when a
        /// crop exists afterwards, otherwise null — and null is an ordinary answer, not an error.
        ///
        /// <para>Every failure mode collapses to null on purpose: no cache directory configured, no
        /// Immich configured, Immich unreachable, the server has no thumbnail for that cluster, or the
        /// directory is read-only (which is the normal state in a prod pod). The caller draws the box
        /// over our own thumb and the family carries on tagging.</para>
        /// </summary>
        public static async Task<string?> EnsureAsync(string? thumbCacheDir, IImmichApi? immich,
            string immichPersonId, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(immichPersonId)) return null;
            var full = FullPath(thumbCacheDir, immichPersonId);
            if (full == null) return null;
            if (File.Exists(full)) return RelativePath(immichPersonId);
            if (immich == null) return null;

            byte[]? bytes;
            try
            {
                bytes = await immich.PersonThumbnailAsync(immichPersonId, cancel);
            }
            catch (Exception)
            {
                // A sidecar that is down must never take a tagging surface down with it (§2.4).
                return null;
            }
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                // Written to a temp name and moved into place: two members opening the queue at once
                // would otherwise race on a half-written file, and a torn JPEG is a broken image that
                // survives every later cache hit.
                //
                // The suffix is a GUID, not the managed thread id. Thread ids are per-PROCESS and are
                // recycled, and the two writers most likely to collide here are the site and the sync
                // CLI — two processes, over one shared cache directory, whose "thread 1" is the same
                // string. A collision is exactly the torn file the temp name exists to prevent.
                var temp = full + ".tmp" + Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(temp, bytes, cancel);
                File.Move(temp, full, overwrite: true);
            }
            catch (Exception)
            {
                return null;
            }
            return RelativePath(immichPersonId);
        }
    }
}
