using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Two judgements about a candidate cover image, kept together because both are "is this the picture we
    /// actually want on the card".
    ///
    /// <para><b>1. Is it usable at all</b> (<see cref="IsUsableCover"/>) — three failure modes, each measured:
    /// a BLANK SWATCH (one flat colour, no art or lettering: population std-dev near zero); a TINY image (a
    /// thumbnail-sized "cover" that can only upscale into a blurry mess); and a FLAT LOGO / publisher
    /// placeholder (line art with plenty of contrast but near-monochrome and concentrated in a handful of tones).
    /// Thresholds were calibrated on real covers: flat placeholders measure colourfulness ≤ 1.1 with ~89–100 % of
    /// the image in three tones, while the lowest-scoring REAL cover (a sepia B&amp;W author photo) measured 5.8
    /// at 63 % concentration — so 4.0 / 0.85 sit in the gap and a grayscale PHOTOGRAPH is deliberately spared.</para>
    ///
    /// <para><b>2. Is it a double-page spread</b> (<see cref="TryCropSpread"/>) — page 0 of a scanned comic is
    /// often a back|front wraparound. A single cover is ~0.66 w:h; two pages side by side are ~1.3, so
    /// <see cref="SpreadAspectThreshold"/> = 1.15 sits clearly above any normal single cover. Over the threshold
    /// the RIGHT half is kept — that is the front cover in a back|front scan — and the crop happens BEFORE the
    /// cover's dimensions are measured, so the stored aspect describes the cropped, portrait cover the reader
    /// will actually see.</para>
    /// </summary>
    public static class CoverImageAnalyzer
    {
        /// <summary>Wider than this (w:h) and page 0 is a spread, not a cover. See the type remarks.</summary>
        public const double SpreadAspectThreshold = 1.15;

        // The sampled grid. It must stay fairly large: tone concentration is resolution sensitive (a logo's
        // anti-aliased edges spread into more tone buckets as the grid shrinks), and below ~96 a flat logo's
        // concentration drops under the threshold and escapes detection.
        private const int SampleSize = 96;

        // Std-dev (0–255) below which an image is a blank/near-solid swatch. The distribution is cleanly bimodal
        // — blanks measure ~0, real covers sit well above 9 — so 6.0 catches every blank and touches no cover.
        private const double BlankStdDevThreshold = 6.0;

        // A cover whose SHORT side is below this can only upscale into a blurry card. Real covers measure ≥ 330
        // on the short side; placeholder thumbnails fall far below.
        private const int MinShortSidePx = 200;

        private const double LogoColourfulnessMax = 4.0;
        private const double LogoToneConcentrationMin = 0.85;

        private readonly record struct Metrics(int Width, int Height, double StdDev, double Colourfulness, double ToneConcentration);

        /// <summary>
        /// Crops a double-page spread to its right half, in place. Returns true when a crop happened, so a caller
        /// that logs or measures can tell. A decode-independent operation on an already-loaded image: the callers
        /// are the thumbnail generator (which measures the cover AFTER this) and nothing else.
        /// </summary>
        public static bool TryCropSpread(Image image)
        {
            if (image.Height <= 0) return false;
            if ((double)image.Width / image.Height <= SpreadAspectThreshold) return false;

            var halfW = image.Width / 2;
            if (halfW <= 0) return false;
            var srcW = image.Width;
            var srcH = image.Height;
            image.Mutate(x => x.Crop(new Rectangle(srcW - halfW, 0, halfW, srcH)));
            return true;
        }

        /// <summary>True when the image carries real visual content — not a blank swatch, not a tiny thumbnail,
        /// not a flat publisher logo. A decode failure returns false so callers fall back to a generated cover.</summary>
        public static bool IsUsableCover(byte[]? imageBytes)
        {
            if (imageBytes is null || imageBytes.Length == 0) return false;
            if (!TryMeasure(imageBytes, out var m)) return false;

            if (m.StdDev < BlankStdDevThreshold) return false;                      // blank swatch
            if (Math.Min(m.Width, m.Height) < MinShortSidePx) return false;         // tiny thumbnail
            if (m.Colourfulness < LogoColourfulnessMax
                && m.ToneConcentration >= LogoToneConcentrationMin) return false;   // flat logo
            return true;
        }

        /// <summary>The blank-swatch test alone, for callers that only care about that one.</summary>
        public static bool HasContent(byte[]? imageBytes)
        {
            if (imageBytes is null || imageBytes.Length == 0) return false;
            return TryMeasure(imageBytes, out var m) && m.StdDev >= BlankStdDevThreshold;
        }

        /// <summary>The raw spread metric, for calibration and diagnostics.</summary>
        public static bool TryMeasureStdDev(byte[] imageBytes, out double stdDev)
        {
            stdDev = 0;
            if (!TryMeasure(imageBytes, out var m)) return false;
            stdDev = m.StdDev;
            return true;
        }

        private static bool TryMeasure(byte[] imageBytes, out Metrics metrics)
        {
            metrics = default;
            try
            {
                using var image = Image.Load<Rgb24>(imageBytes);
                var srcW = image.Width;
                var srcH = image.Height;
                image.Mutate(x => x.Resize(SampleSize, SampleSize));

                double sum = 0, sumSq = 0;          // luminance spread (blank detection)
                double sumRg = 0, sumRgSq = 0;      // chroma opponent channels (colourfulness)
                double sumYb = 0, sumYbSq = 0;
                var n = 0;
                var buckets = new Dictionary<int, int>(64);   // 3-bits/channel tone histogram

                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        foreach (ref var p in row)
                        {
                            double v = (p.R + p.G + p.B) / 3.0;
                            sum += v; sumSq += v * v;

                            double rg = Math.Abs(p.R - p.G);
                            double yb = Math.Abs((p.R + p.G) / 2.0 - p.B);
                            sumRg += rg; sumRgSq += rg * rg;
                            sumYb += yb; sumYbSq += yb * yb;

                            var key = ((p.R >> 5) << 6) | ((p.G >> 5) << 3) | (p.B >> 5);
                            buckets[key] = buckets.GetValueOrDefault(key) + 1;
                            n++;
                        }
                    }
                });

                if (n == 0) return false;

                var stdDev = Spread(sum, sumSq, n);

                // Hasler–Süsstrunk colourfulness: std + 0.3·mean of the two chroma opponent channels.
                var meanRg = sumRg / n;
                var meanYb = sumYb / n;
                var stdRg = Spread(sumRg, sumRgSq, n);
                var stdYb = Spread(sumYb, sumYbSq, n);
                var colourfulness = Math.Sqrt(stdRg * stdRg + stdYb * stdYb)
                                  + 0.3 * Math.Sqrt(meanRg * meanRg + meanYb * meanYb);

                // Share of pixels in the three most-common tone buckets — high for flat logo art.
                var top3 = buckets.Values.OrderByDescending(c => c).Take(3).Sum();
                var concentration = (double)top3 / n;

                metrics = new Metrics(srcW, srcH, stdDev, colourfulness, concentration);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double Spread(double sum, double sumSq, int n)
        {
            var mean = sum / n;
            var variance = sumSq / n - mean * mean;
            return variance > 0 ? Math.Sqrt(variance) : 0;
        }
    }
}
