using System.Security.Cryptography;
using System.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Stand-in cover / page images for documents that ship no usable artwork — a book whose embedded "cover" is
    /// a blank colour swatch, or a file that would not decode at all. A cover gets a generated JACKET (title and
    /// author typeset on a seeded gradient), which reads far better in a grid than a flat rectangle; a missing
    /// interior page stays a plain seeded colour.
    ///
    /// <para>The seed is a hash of the title and page position, so the same book always gets the same jacket —
    /// a re-render is stable, and the thumbnail cache is not invalidated by chance.</para>
    /// </summary>
    internal static class DocumentPlaceholderRenderer
    {
        private const int W = 1200, H = 1700;

        // Serif first (it reads as a book jacket), then common sans, then whatever the machine has.
        private static readonly string[] PreferredFonts =
        [
            "Georgia", "Constantia", "Cambria", "Palatino Linotype", "Times New Roman",
            "Segoe UI", "Calibri", "Arial", "Verdana",
        ];

        private static readonly Lazy<FontFamily?> ResolvedFamily = new(ResolveFamily);

        public static Task<Stream> CreateCoverAsync(string fileName) =>
            CreateCoverAsync(CleanTitle(fileName), author: null, fallbackName: fileName);

        public static Task<Stream> CreateCoverAsync(string? title, string? author, string fallbackName)
        {
            var heading = string.IsNullOrWhiteSpace(title) ? CleanTitle(fallbackName) : title.Trim();
            var seed = BuildSeed(heading, 0, 1);

            using var image = new Image<Rgba32>(W, H);
            PaintGradient(image, seed);
            // With no font available the gradient alone still beats a flat swatch.
            if (ResolvedFamily.Value is { } family) DrawJacket(image, family, heading, author);
            return Encode(image);
        }

        public static Task<Stream> CreatePageAsync(string fileName, int pageIndex, int pageCount)
        {
            var seed = BuildSeed(fileName, pageIndex, pageCount);
            using var image = new Image<Rgba32>(W, H, Color.FromRgb(seed[0], seed[1], seed[2]));
            return Encode(image);
        }

        /// <summary>A vertical gradient between two shades of a seeded, muted hue — dark enough that near-white
        /// text always reads on it.</summary>
        private static void PaintGradient(Image<Rgba32> image, byte[] seed)
        {
            var hue = seed[0] / 255.0 * 360.0;
            var top = FromHsl(hue, 0.32, 0.34);
            var bottom = FromHsl(hue, 0.40, 0.18);
            var brush = new LinearGradientBrush(
                new PointF(0, 0), new PointF(0, H), GradientRepetitionMode.None,
                new ColorStop(0f, top), new ColorStop(1f, bottom));
            image.Mutate(x => x.Fill(brush));
        }

        private static void DrawJacket(Image<Rgba32> image, FontFamily family, string title, string? author)
        {
            const float margin = 130f;
            var wrap = W - margin * 2;

            var ink = Color.FromRgba(0xF6, 0xF3, 0xEC, 0xFF);
            var faded = Color.FromRgba(0xF6, 0xF3, 0xEC, 0xCC);

            // The title size steps down for longer titles so it stays inside the cover.
            var titleSize = title.Length switch { > 70 => 60f, > 45 => 76f, > 28 => 92f, _ => 110f };
            var titleFont = family.CreateFont(titleSize, FontStyle.Bold);
            var authorFont = family.CreateFont(46f, FontStyle.Regular);

            var titleOpts = new RichTextOptions(titleFont)
            {
                WrappingLength = wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Origin = new PointF(W / 2f, 0),
            };
            var titleBox = TextMeasurer.MeasureSize(title, titleOpts);

            var hasAuthor = !IsJunkAuthor(author);
            var authorOpts = new RichTextOptions(authorFont)
            {
                WrappingLength = wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Origin = new PointF(W / 2f, 0),
            };
            var authorBox = hasAuthor ? TextMeasurer.MeasureSize(author!, authorOpts) : default;

            const float gap = 48f, rule = 2f;
            var blockH = titleBox.Height + (hasAuthor ? gap + rule + gap + authorBox.Height : 0);
            var top = (H - blockH) / 2f;

            titleOpts.Origin = new PointF(W / 2f, top);
            image.Mutate(x => x.DrawText(titleOpts, title, ink));

            if (!hasAuthor) return;
            var ruleY = top + titleBox.Height + gap;
            image.Mutate(x => x.DrawLine(faded, rule, new PointF(W / 2f - 90, ruleY), new PointF(W / 2f + 90, ruleY)));
            authorOpts.Origin = new PointF(W / 2f, ruleY + gap);
            image.Mutate(x => x.DrawText(authorOpts, author!, faded));
        }

        private static FontFamily? ResolveFamily()
        {
            foreach (var name in PreferredFonts)
                if (SystemFonts.TryGet(name, out var fam)) return fam;
            return SystemFonts.Families.Any() ? SystemFonts.Families.First() : null;
        }

        private static Task<Stream> Encode(Image<Rgba32> image)
        {
            var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 88 });
            ms.Position = 0;
            return Task.FromResult<Stream>(ms);
        }

        /// <summary>A file name into a readable title: drop the extension, separators to spaces, and trim a
        /// trailing " - Author" segment.</summary>
        private static string CleanTitle(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('.', ' ').Trim();
            var dash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 8) stem = stem[..dash].Trim();
            return string.IsNullOrWhiteSpace(stem) ? "Untitled" : stem;
        }

        /// <summary>Many EPUBs stamp a placeholder author; "Unknown" on a jacket looks worse than nothing.</summary>
        private static bool IsJunkAuthor(string? author) =>
            string.IsNullOrWhiteSpace(author)
            || author.Trim().ToLowerInvariant() is "unknown" or "unknown author" or "anonymous"
               or "n/a" or "na" or "none" or "no author";

        private static byte[] BuildSeed(string key, int pageIndex, int pageCount) =>
            SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{pageIndex}|{pageCount}"));

        private static Color FromHsl(double h, double s, double l)
        {
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var hp = h / 60.0;
            var x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;
            switch ((int)hp)
            {
                case 0: r = c; g = x; break;
                case 1: r = x; g = c; break;
                case 2: g = c; b = x; break;
                case 3: g = x; b = c; break;
                case 4: r = x; b = c; break;
                default: r = c; b = x; break;
            }
            var m = l - c / 2;
            return Color.FromRgb(
                (byte)Math.Clamp((r + m) * 255, 0, 255),
                (byte)Math.Clamp((g + m) * 255, 0, 255),
                (byte)Math.Clamp((b + m) * 255, 0, 255));
        }
    }
}
