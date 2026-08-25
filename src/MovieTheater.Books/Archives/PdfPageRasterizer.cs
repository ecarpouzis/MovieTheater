using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Rasterizes a PDF page to JPEG through pdfium, compositing images AND text/vector content — the path for
    /// magazine-style PDFs whose pages are an image plus real PDF text, which
    /// <see cref="PdfPageImageExtractor"/> would silently drop. Image-per-page comic PDFs keep using the
    /// extractor (original bytes, no re-encode); this is the fallback for everything else.
    /// </summary>
    internal static class PdfPageRasterizer
    {
        /// <summary>pdfium is NOT thread-safe. Every render in the process serializes through this lock.</summary>
        private static readonly Lock PdfiumLock = new();

        /// <summary>Long-edge render target (~235 DPI for a US-Letter page) — generous enough that the reader's
        /// no-maxWidth hi-res swap-in makes small magazine text legible.</summary>
        private const double TargetLongEdgePx = 2600;

        private const int JpegQuality = 90;

        public static bool TryRasterizePage(
            string filePath, int pageIndex, double pageWidthPts, double pageHeightPts, out byte[] jpegBytes)
        {
            try
            {
                var longEdge = Math.Max(pageWidthPts, pageHeightPts);
                var scale = longEdge > 0 ? TargetLongEdgePx / longEdge : 2.0;
                scale = Math.Clamp(scale, 1.0, 4.0);

                byte[] bgra;
                int width, height;
                lock (PdfiumLock)
                {
                    using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(scale));
                    using var pageReader = docReader.GetPageReader(pageIndex);
                    width = pageReader.GetPageWidth();
                    height = pageReader.GetPageHeight();
                    // A PDF page has no background of its own; flatten transparent pixels to paper white.
                    bgra = pageReader.GetImage(new NaiveTransparencyRemover(255, 255, 255));
                }

                if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
                {
                    jpegBytes = [];
                    return false;
                }

                using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms, new JpegEncoder { Quality = JpegQuality });
                jpegBytes = ms.ToArray();
                return true;
            }
            catch
            {
                jpegBytes = [];
                return false;
            }
        }
    }
}
