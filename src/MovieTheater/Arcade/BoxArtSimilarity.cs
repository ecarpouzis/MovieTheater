using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// "Do these two cards show the same box?" — the independent corroboration
    /// <see cref="ArcadeMergeCardsCommand"/> uses to tell a localized re-release of one game apart from two
    /// different games that a title index happened to link.
    ///
    /// <para>A cover is reduced to a normalized 12x12 luma grid of its inner 76%x80% crop, and two covers are
    /// scored by the mean product of their grids (Pearson correlation of the downsampled images): 1.0 =
    /// identical artwork, ~0 = unrelated. The CROP is load-bearing — box scans include the platform's own
    /// frame, which by itself correlates every Nintendo DS cover with every other at ~0.32 and every Game Boy
    /// Advance cover at ~0.56; cropping it away drops those floors to ~0.03 and ~0.33. Downsampling to 12x12
    /// is what makes a German cover match its English twin at all: same photograph, different title text.</para>
    ///
    /// <para>Covers are read from the posters mount when it is populated, else fetched from a running site's
    /// <c>/ArcadeImage/{id}</c> (which resolves a row to its whole card's art and lazily fills the cascade) and
    /// cached on disk, so a resumed or repeated run re-fetches nothing. A cover that cannot be resolved yields
    /// a null score, never an exception — missing art means "no opinion", not "not a match".</para>
    /// </summary>
    public sealed class BoxArtSimilarity
    {
        private const int Grid = 12;              // downsample size; small enough to see past overlaid text
        private const double CropX = 0.12;        // trim per side: drops the platform frame / spine
        private const double CropY = 0.10;

        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly string cacheDir;
        private readonly string postersDir;
        private readonly Dictionary<int, float[]> features = new();

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        public BoxArtSimilarity(HttpClient http, string baseUrl, string cacheDir, string postersDir)
        {
            this.http = http;
            this.baseUrl = (baseUrl ?? "").TrimEnd('/');
            this.cacheDir = cacheDir;
            this.postersDir = postersDir;
        }

        /// <summary>Similarity of two cards' covers in [-1, 1], or null when either cover is unavailable.</summary>
        public async Task<double?> ScoreAsync(int anchorA, int anchorB)
        {
            var a = await FeatureAsync(anchorA);
            var b = await FeatureAsync(anchorB);
            if (a == null || b == null) return null;
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum / a.Length;
        }

        private async Task<float[]> FeatureAsync(int anchorId)
        {
            if (features.TryGetValue(anchorId, out var cached)) return cached;
            var bytes = await LoadAsync(anchorId);
            var f = bytes == null ? null : Describe(bytes);
            if (f == null) Misses++; else Hits++;
            features[anchorId] = f;
            return f;
        }

        private async Task<byte[]> LoadAsync(int anchorId)
        {
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, anchorId + ".img");
            var missPath = Path.Combine(cacheDir, anchorId + ".none");
            if (File.Exists(cachePath)) return await File.ReadAllBytesAsync(cachePath);
            if (File.Exists(missPath)) return null;

            // The posters mount, when this run has one (prod / Ziggy). BoxArtPath is per-row and the merge
            // command hands us the card's anchor, which is the row the image route writes a fresh fetch to.
            if (!string.IsNullOrEmpty(postersDir) && Directory.Exists(postersDir))
                foreach (var ext in new[] { ".png", ".jpg" })
                    foreach (var dir in Directory.EnumerateDirectories(Path.Combine(postersDir, "arcade"), "*",
                                                                       SearchOption.TopDirectoryOnly))
                    {
                        var p = Path.Combine(dir, anchorId + ext);
                        if (File.Exists(p)) return await File.ReadAllBytesAsync(p);
                    }

            if (baseUrl.Length == 0) { File.WriteAllText(missPath, "no-source"); return null; }
            try
            {
                using var resp = await http.GetAsync($"{baseUrl}/ArcadeImage/{anchorId}");
                if (!resp.IsSuccessStatusCode) { File.WriteAllText(missPath, ((int)resp.StatusCode).ToString()); return null; }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(cachePath, bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                // A transient fetch failure must not be cached as "no art" — leave it retryable next run.
                _ = ex;
                return null;
            }
        }

        /// <summary>Cropped, downsampled, mean-zeroed / unit-variance luma grid. Null if undecodable or flat.</summary>
        private static float[] Describe(byte[] bytes)
        {
            try
            {
                using var img = Image.Load<L8>(bytes);
                int x = (int)(img.Width * CropX), y = (int)(img.Height * CropY);
                int cw = Math.Max(1, img.Width - 2 * x), ch = Math.Max(1, img.Height - 2 * y);
                img.Mutate(c => c.Crop(new Rectangle(x, y, cw, ch)).Resize(Grid, Grid));

                var v = new float[Grid * Grid];
                int i = 0;
                for (int r = 0; r < Grid; r++)
                    for (int col = 0; col < Grid; col++)
                        v[i++] = img[col, r].PackedValue;

                double mean = 0;
                foreach (var t in v) mean += t;
                mean /= v.Length;
                double var2 = 0;
                foreach (var t in v) var2 += (t - mean) * (t - mean);
                var sd = Math.Sqrt(var2 / v.Length);
                if (sd < 1e-3) return null;                      // a blank/solid cover describes nothing
                for (int k = 0; k < v.Length; k++) v[k] = (float)((v[k] - mean) / sd);
                return v;
            }
            catch
            {
                return null;
            }
        }
    }
}
