using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// The ONE decision point for turning a PDF page into an image. An image-per-page comic PDF serves its
    /// embedded full-page image untouched (lossless, no re-encode); a page that carries visible PDF text or
    /// isn't dominated by a single full-page raster — magazine layouts, vector pages, multi-image composites —
    /// is rasterized via pdfium so that content actually renders.
    /// </summary>
    internal static class PdfPageImageSource
    {
        /// <summary>A handful of VISIBLE letters means real page content, not a stray artifact. An invisible OCR
        /// layer over a scan renders nothing, so it does not count.</summary>
        private const int VisibleLetterThreshold = 8;

        /// <summary>The largest image must cover this fraction of the page for extraction to "be" the page.</summary>
        private const double FullPageImageCoverage = 0.85;

        public static bool TryGetPageImage(
            string filePath, Page page, int pageIndex, out byte[] bytes, out string extension)
        {
            if (!NeedsRasterization(page) && PdfPageImageExtractor.TryExtractLargestImage(page, out bytes, out extension))
                return true;

            if (PdfPageRasterizer.TryRasterizePage(filePath, pageIndex, page.Width, page.Height, out bytes))
            {
                extension = ".jpg";
                return true;
            }

            // Rasterization failed (corrupt page, pdfium unavailable) — the largest image beats nothing.
            return PdfPageImageExtractor.TryExtractLargestImage(page, out bytes, out extension);
        }

        private static bool NeedsRasterization(Page page)
        {
            var visibleLetters = 0;
            foreach (var letter in page.Letters)
            {
                if (letter.RenderingMode == TextRenderingMode.Neither || string.IsNullOrWhiteSpace(letter.Value)) continue;
                if (++visibleLetters >= VisibleLetterThreshold) return true;
            }

            var best = page.GetImages()
                .OrderByDescending(i => i.BoundingBox.Width * i.BoundingBox.Height)
                .FirstOrDefault();
            if (best == null) return true;   // vector page: nothing to extract

            var pageArea = page.Width * page.Height;
            return pageArea > 0 && best.BoundingBox.Width * best.BoundingBox.Height < pageArea * FullPageImageCoverage;
        }
    }
}
