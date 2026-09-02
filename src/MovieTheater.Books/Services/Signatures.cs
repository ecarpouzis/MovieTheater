using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// The three duplicate signals on <c>ItemSignature</c>, computed. Ported from the standalone's
    /// <c>ContentHasher</c> + <c>DHash</c> (which v1 ran inside its scanner and its dedup pass respectively), so
    /// a v2-scanned item groups with a migrated one under the SAME hash — the fingerprint recipe is byte-for-byte
    /// the v1 one, ComicInfo exclusion and lower-casing included.
    ///
    /// <para><b>All reads are read-only</b>, and the expensive one is opt-in: a ZIP-family fingerprint reads the
    /// central directory alone (a seek to the tail of the file, no entry decompressed), the cover hash reads the
    /// LOCAL thumbnail and never the share, and only <see cref="HashFileBytes"/> — a full pass over the file —
    /// is gated behind <c>--hash-bytes</c> in the verb.</para>
    /// </summary>
    public static class Signatures
    {
        private static readonly HashSet<string> ZipFamily = new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".zip", ".epub" };

        /// <summary>True when the extension is a ZIP-family archive whose central directory carries a cheap fingerprint.</summary>
        public static bool SupportsArchiveFingerprint(string? extension) =>
            extension != null && ZipFamily.Contains(extension.StartsWith('.') ? extension : "." + extension);

        /// <summary>
        /// The two archive signals from ONE central-directory read: the content fingerprint (name | size | CRC of
        /// every page, so identical pages re-zipped with different compression still agree) and the weaker page
        /// signature (name | size only — the "same comic, one page re-saved at the same size" tier the dedup
        /// groups as identical contents). Directory entries and <c>ComicInfo.xml</c> are excluded, so a
        /// metadata-only re-tag changes neither. Null when the archive cannot be opened or holds no pages.
        /// </summary>
        public static (string Content, string Pages)? ArchiveSignatures(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var content = new StringBuilder();
                var pages = new StringBuilder();
                foreach (var e in zip.Entries
                             .Where(e => e.Length > 0 && !e.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    var name = e.FullName.ToLowerInvariant();
                    content.Append(name).Append('|').Append(e.Length).Append('|').Append(e.Crc32).Append('\n');
                    pages.Append(name).Append('|').Append(e.Length).Append('\n');
                }
                if (content.Length == 0) return null;
                return (Hex(content.ToString()), Hex(pages.ToString()));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>SHA-256 (uppercase hex) of the whole file, streamed. Null when the file cannot be read.</summary>
        public static string? HashFileBytes(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan);
                return Convert.ToHexString(SHA256.HashData(fs));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 64-bit difference hash of an image file — stable across rescaling and re-compression, so two scans of
        /// the same cover land a small Hamming distance apart. Null on any read/decode error.
        /// </summary>
        public static long? CoverHash(string imagePath)
        {
            try
            {
                using var image = Image.Load<L8>(imagePath);
                return CoverHash(image);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>dHash: 9×8 grayscale, one bit per adjacent column pair (left &lt; right) per row → 64 bits.</summary>
        public static long CoverHash(Image<L8> image)
        {
            image.Mutate(x => x.Resize(9, 8));
            ulong hash = 0;
            var bit = 0;
            for (var row = 0; row < 8; row++)
                for (var col = 0; col < 8; col++)
                {
                    if (image[col, row].PackedValue < image[col + 1, row].PackedValue) hash |= 1UL << bit;
                    bit++;
                }
            return unchecked((long)hash);
        }

        /// <summary>Differing bits between two cover hashes — 0 identical, small near-identical, large different.</summary>
        public static int Hamming(long a, long b) => System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));

        private static string Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}
