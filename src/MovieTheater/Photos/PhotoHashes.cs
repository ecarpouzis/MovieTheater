using System;
using System.IO;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The hash pass's arithmetic (photos-plan.md §2.5 phase 3 / §2.6).
    ///
    /// <para><b>SHA-256 is identity</b> (§2.5): content is identity, path is location. It is what
    /// re-pairs a moved file to its existing row once hashes exist, what groups exact dupes, and what
    /// the Google mesh matches on first.</para>
    ///
    /// <para><b>dHash and pHash are similarity</b>, not identity — they answer "is this the same
    /// picture" across a re-encode, a resize or a second scan of the same print, which is the whole
    /// problem the scanned-album folders pose. Both are computed here rather than pulled in as a
    /// dependency: they are a downscale plus sixty-four comparisons, the definitions are fixed
    /// (any deviation would silently change every stored hash), and a perceptual-hash package would be
    /// a third imaging stack beside ImageSharp for that.</para>
    ///
    /// <para>Hashes are computed on the AUTO-ORIENTED image so a photo and its EXIF-rotated twin do not
    /// read as different pictures — the same normalization the derivatives get (§2.2).</para>
    /// </summary>
    public static class PhotoHashes
    {
        public static string Sha256File(string fullPath)
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 128);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        /// <summary>
        /// Difference hash: 9×8 grayscale, one bit per horizontal neighbour pair. Cheap, and robust to
        /// brightness/contrast shifts because it only ever compares a pixel to the one beside it.
        /// </summary>
        public static long DHash(Image<Rgba32> image)
        {
            using var small = image.Clone(ctx => ctx
                .Resize(new ResizeOptions { Size = new Size(9, 8), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Box })
                .Grayscale());

            ulong bits = 0;
            var bit = 0;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    if (small[x, y].R < small[x + 1, y].R) bits |= 1UL << bit;
                    bit++;
                }
            }
            return unchecked((long)bits);
        }

        /// <summary>
        /// Perceptual hash: 32×32 grayscale → DCT-II → the top-left 8×8 low-frequency block, each
        /// coefficient compared to the block's median. The DC term is excluded from the median (it is
        /// average brightness, orders of magnitude larger than everything else, and including it drags
        /// the median so far that ~63 of the 64 bits become the same value).
        /// </summary>
        public static long PHash(Image<Rgba32> image)
        {
            const int N = 32;
            using var small = image.Clone(ctx => ctx
                .Resize(new ResizeOptions { Size = new Size(N, N), Mode = ResizeMode.Stretch, Sampler = KnownResamplers.Box })
                .Grayscale());

            var pixels = new double[N, N];
            for (var y = 0; y < N; y++)
                for (var x = 0; x < N; x++)
                    pixels[y, x] = small[x, y].R;

            var dct = Dct2D(pixels, N);

            // Row-major top-left 8×8, DC (0,0) skipped.
            var values = new double[63];
            var i = 0;
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    if (x == 0 && y == 0) continue;
                    values[i++] = dct[y, x];
                }

            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            var median = (sorted[30] + sorted[31]) / 2.0;

            ulong bits = 0;
            var bit = 0;
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                {
                    // The DC slot keeps its place in the 64-bit word (always 0) so the bit positions of
                    // every other coefficient are fixed for all time — a stored hash must stay
                    // comparable to one computed years later.
                    if (!(x == 0 && y == 0) && dct[y, x] > median) bits |= 1UL << bit;
                    bit++;
                }
            return unchecked((long)bits);
        }

        /// <summary>Separable DCT-II. Only the first 8 output rows/columns are ever read, but the
        /// full transform is cheap at 32×32 and writing the truncated form invites an off-by-one in
        /// the one place a mistake would be invisible (wrong hashes still look like hashes).</summary>
        private static double[,] Dct2D(double[,] input, int n)
        {
            var cos = new double[n, n];
            for (var u = 0; u < n; u++)
                for (var x = 0; x < n; x++)
                    cos[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * n));

            var rows = new double[n, n];
            for (var y = 0; y < n; y++)
                for (var u = 0; u < n; u++)
                {
                    double sum = 0;
                    for (var x = 0; x < n; x++) sum += input[y, x] * cos[u, x];
                    rows[y, u] = sum * (u == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n));
                }

            var output = new double[n, n];
            for (var u = 0; u < n; u++)
                for (var v = 0; v < n; v++)
                {
                    double sum = 0;
                    for (var y = 0; y < n; y++) sum += rows[y, u] * cos[v, y];
                    output[v, u] = sum * (v == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n));
                }
            return output;
        }

        /// <summary>Bits that differ — the near-dupe distance Phase 3 thresholds on.</summary>
        public static int HammingDistance(long a, long b) =>
            System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));
    }
}
