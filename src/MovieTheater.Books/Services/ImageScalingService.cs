using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// Turns a page's raw bytes into what goes on the wire: a JPEG no wider than the caller's budget.
    ///
    /// <para><b>The fast path decodes nothing.</b> A header-only <c>Identify</c> tells us the format and the
    /// width; the overwhelmingly common case is a JPEG already inside the budget, and those bytes are returned
    /// UNTOUCHED — no pixel decode, and no quality lost to re-encoding a lossy format for no reason. Only an
    /// over-budget page or a non-JPEG pays the full decode/resize/encode.</para>
    ///
    /// <para><b>The budget doubles for a landscape page.</b> <c>maxWidth</c> is the client's viewport width in
    /// device pixels, sent for a single portrait page; a landscape image at that index is a double-page spread
    /// being shown across the same viewport, so it is allowed twice the pixels — otherwise every spread would
    /// arrive at half the resolution of the pages around it.</para>
    /// </summary>
    public sealed class ImageScalingService
    {
        private readonly BooksOptions options;
        public ImageScalingService(BooksOptions options) => this.options = options;

        public async Task<Stream> ScalePageAsync(Stream input, int? maxWidth)
        {
            // The hot path (the page-byte cache) already hands us an in-memory stream — use it directly rather
            // than paying a full copy per request. Only a non-seekable source needs buffering.
            MemoryStream buffer;
            if (input is MemoryStream seekable)
            {
                buffer = seekable;
                buffer.Position = 0;
            }
            else
            {
                buffer = new MemoryStream();
                await input.CopyToAsync(buffer);
                await input.DisposeAsync();
                buffer.Position = 0;
            }

            try
            {
                var info = await Image.IdentifyAsync(buffer);
                buffer.Position = 0;
                var budget = Budget(maxWidth, info.Width, info.Height);
                var isJpeg = info.Metadata.DecodedImageFormat is JpegFormat;
                if (info.Width <= budget && isJpeg) return buffer;   // already a JPEG within budget
            }
            catch
            {
                // Unidentifiable or odd header — fall through to the full decode, which handles it.
                buffer.Position = 0;
            }

            using var image = await Image.LoadAsync(buffer);
            var decodedBudget = Budget(maxWidth, image.Width, image.Height);
            var needsResize = image.Width > decodedBudget;

            image.Mutate(x =>
            {
                // Flatten transparency onto white: a PNG page with an alpha channel would otherwise composite
                // against JPEG's implicit black.
                x.BackgroundColor(Color.White);
                if (needsResize) x.Resize(new ResizeOptions { Size = new Size(decodedBudget, 0), Mode = ResizeMode.Max });
            });

            var ms = new MemoryStream();
            await image.SaveAsync(ms, new JpegEncoder { Quality = options.PageJpegQuality });
            ms.Position = 0;
            return ms;
        }

        private static int Budget(int? maxWidth, int width, int height) =>
            maxWidth is > 0 ? (width < height ? maxWidth.Value : maxWidth.Value * 2) : int.MaxValue;
    }
}
