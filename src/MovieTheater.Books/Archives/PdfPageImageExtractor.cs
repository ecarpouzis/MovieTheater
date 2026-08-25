using UglyToad.PdfPig.Content;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Pulls the single full-page raster out of a comic PDF page. A comic PDF is almost always one full-page
    /// image per page, so the LARGEST embedded image IS the page. This is the lossless fast path of
    /// <see cref="PdfPageImageSource"/>, which decides per page whether extraction suffices.
    /// </summary>
    internal static class PdfPageImageExtractor
    {
        public static bool TryExtractLargestImage(Page page, out byte[] bytes, out string extension)
        {
            // A page can carry several images (tiles, overlays, decorations); for a comic the page itself is the
            // largest, so pick the one covering the most pixels.
            var best = page.GetImages()
                .OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples)
                .FirstOrDefault();

            if (best == null)
            {
                bytes = [];
                extension = string.Empty;
                return false;
            }
            return TryGetImageBytes(best, out bytes, out extension);
        }

        /// <summary>
        /// Prefer passing the original JPEG/PNG bytes through untouched — re-encoding a lossy format to serve it
        /// costs quality for nothing. Other encodings go through PdfPig's PNG conversion.
        /// </summary>
        private static bool TryGetImageBytes(IPdfImage image, out byte[] bytes, out string extension)
        {
            var raw = image.RawMemory;
            if (raw.Length > 3)
            {
                var span = raw.Span;
                if (span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF)   // JPEG
                {
                    bytes = raw.ToArray();
                    extension = ".jpg";
                    return true;
                }
                if (span[0] == 0x89 && span[1] == 0x50 && span[2] == 0x4E && span[3] == 0x47)   // PNG
                {
                    bytes = raw.ToArray();
                    extension = ".png";
                    return true;
                }
            }

            if (image.TryGetPng(out var png) && png is { Length: > 0 })
            {
                bytes = png;
                extension = ".png";
                return true;
            }

            bytes = [];
            extension = string.Empty;
            return false;
        }
    }
}
