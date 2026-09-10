using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MovieTheater.Books.Archives;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MovieTheater.Books.Services
{
    /// <summary>The outcome of one generation attempt. <see cref="Width"/>/<see cref="Height"/> are the SOURCE
    /// cover's pixel dimensions and are present only when a thumbnail was actually generated — a cache hit
    /// generates nothing and measures nothing.</summary>
    public sealed class ThumbnailResult
    {
        public string? Path { get; init; }
        public string? Error { get; init; }
        /// <summary>The ARCHIVE could not be opened or read (a corrupt/truncated file), as opposed to a cover
        /// image that merely would not decode. Only this deserves the broken flag.</summary>
        public bool ArchiveUnreadable { get; init; }
        /// <summary>The thumbnail written is a GENERATED JACKET, not artwork out of the file. <see cref="Error"/>
        /// still carries why no real cover could be had — the caller decides whether that is worth recording.</summary>
        public bool Placeholder { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public bool Success => Path != null;

        public static ThumbnailResult Ok(string path, int? width = null, int? height = null) =>
            new() { Path = path, Width = width, Height = height };

        public static ThumbnailResult Fail(string error, bool archiveUnreadable = false) =>
            new() { Error = error, ArchiveUnreadable = archiveUnreadable };

        public static ThumbnailResult Generated(
            string path, string? reason, bool archiveUnreadable, int? width = null, int? height = null) =>
            new()
            {
                Path = path,
                Error = reason,
                ArchiveUnreadable = archiveUnreadable,
                Placeholder = true,
                Width = width,
                Height = height,
            };
    }

    /// <summary>
    /// What to typeset on a generated jacket when a file yields no usable cover art. Passing one is what OPTS AN
    /// ITEM IN to the placeholder fallback; a null means "fail instead", which is what comics want — a comic
    /// that will not open is a defect to fix, and a plausible-looking jacket over it hides the defect. A prose
    /// book in a text-only container (a .rar of .txt/.rtf/.html, 1,463 of them here) has no artwork to find and
    /// never will, so a title card is the honest best answer rather than a permanent gap in the shelf.
    /// </summary>
    public sealed record PlaceholderCover(string? Title, string? Author);

    /// <summary>
    /// The cover thumbnail for one item: <c>{id}.webp</c> under <c>Books:CacheDir</c>.
    ///
    /// <para><b>The file name is the item id and nothing else</b>, which is why the 141k thumbnails the
    /// standalone site already generated are valid the moment v2 opens — ids were preserved by the migration on
    /// purpose. Regenerating them would be days of work for identical bytes.</para>
    ///
    /// <para><b>720×440, WebP lossy, method 4, quality from configuration.</b> Measured against 60 real covers at
    /// matched settings, WebP is both more faithful than JPEG q82 (SSIM 0.9737 vs 0.9660) and 26 % smaller;
    /// method 4 beats method 6 on fidelity (0.9737 vs 0.9721) for +1.2 KB and 40 % less encode time. The file
    /// format is set EXPLICITLY because the default lets the encoder choose, and lossless on a photographic cover
    /// is enormous.</para>
    ///
    /// <para><b>Shrink only.</b> <c>ResizeMode.Max</c> scales by min(W/w, H/h), which is greater than 1 for a
    /// cover already smaller than the box — it would upscale, spending bytes on interpolated detail that is not
    /// in the source. ~6.7 % of covers are shorter than the target, so this is not a rare path.</para>
    ///
    /// <para>This class writes ONLY into the cache directory. The library file is opened read-only.</para>
    /// </summary>
    public sealed class ThumbnailService
    {
        /// <summary>How many leading pages to try before giving up. Page 0 (the cover) goes first; if that entry
        /// is individually damaged the rest of the archive is usually intact, so we walk forward a few pages.
        /// Bounded so a fully-corrupt file fails fast.</summary>
        private const int MaxCoverAttempts = 5;

        /// <summary>The generated thumbnail's extension, including the dot.</summary>
        public const string Extension = ".webp";

        /// <summary>The MIME type matching <see cref="Extension"/>. Every endpoint that serves a thumbnail off
        /// disk must use this — a hardcoded "image/jpeg" once made the format impossible to change in one place.</summary>
        public const string ContentType = "image/webp";

        public const int TargetWidth = 720;
        public const int TargetHeight = 440;

        private readonly IEnumerable<IArchiveReader> readers;
        private readonly BooksOptions options;
        private readonly ILogger<ThumbnailService> logger;

        // Per-item async locks: two concurrent requests must not both race to generate the same file.
        private readonly ConcurrentDictionary<long, SemaphoreSlim> locks = new();

        public ThumbnailService(IEnumerable<IArchiveReader> readers, BooksOptions options, ILogger<ThumbnailService> logger)
        {
            this.readers = readers;
            this.options = options;
            this.logger = logger;
        }

        /// <summary>Whether a cache directory is configured at all. Without one nothing can be generated.</summary>
        public bool Configured => !string.IsNullOrWhiteSpace(options.CacheDir);

        /// <summary>The ONE place a thumbnail's on-disk location is spelled. Callers must not rebuild it.</summary>
        public string GetCachePath(long itemId) =>
            Path.Combine(options.CacheDir ?? "", $"{itemId}{Extension}");

        public bool Exists(long itemId) => Configured && File.Exists(GetCachePath(itemId));

        /// <param name="placeholder">When non-null and no real cover can be extracted, a generated jacket is
        /// written instead of failing. See <see cref="PlaceholderCover"/> for who should pass one.</param>
        public async Task<ThumbnailResult> TryGetOrGenerateAsync(
            long itemId, string filePath, string? fileExtension, PlaceholderCover? placeholder = null)
        {
            if (!Configured) return ThumbnailResult.Fail("Books:CacheDir is not configured.");
            var cachePath = GetCachePath(itemId);
            if (File.Exists(cachePath)) return ThumbnailResult.Ok(cachePath);

            var sem = locks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Re-check inside the lock: another waiter may have generated it while we waited.
                if (File.Exists(cachePath)) return ThumbnailResult.Ok(cachePath);
                return await GenerateAsync(itemId, filePath, fileExtension, placeholder);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// Delete this item's thumbnail. There is deliberately NO "regenerate all" here: the only generation mode
        /// is "generate missing" (<see cref="ThumbnailJob"/>), and a rebuild is delete-then-generate-missing so it
        /// stays chunked, resumable and countable instead of one unbounded pass.
        /// </summary>
        public void Delete(long itemId)
        {
            if (!Configured) return;
            var path = GetCachePath(itemId);
            if (File.Exists(path)) File.Delete(path);
        }

        private async Task<ThumbnailResult> GenerateAsync(
            long itemId, string filePath, string? fileExtension, PlaceholderCover? placeholder)
        {
            var reader = readers.ForFile(filePath, fileExtension);
            if (reader == null)
            {
                logger.LogWarning("No archive reader for item {ItemId} (extension {Extension}).", itemId, fileExtension);
                var noReader = $"No archive reader for extension '{fileExtension}'";
                return await FallBackAsync(itemId, filePath, placeholder, noReader, archiveUnreadable: false)
                       ?? ThumbnailResult.Fail(noReader);
            }

            var cachePath = GetCachePath(itemId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            string? lastError = null;
            var coverEntryThrew = false;   // page 0 itself would not extract → the archive is likely unreadable

            for (var pageIndex = 0; pageIndex < MaxCoverAttempts; pageIndex++)
            {
                Stream pageStream;
                try
                {
                    // The first attempt goes through the reader's dedicated COVER resolver, not raw page 0. For
                    // comics and PDFs the cover IS page 0, so nothing changes; for an EPUB it resolves the
                    // declared cover image instead of the first spine image, which for a reflowable novel is
                    // routinely an interior illustration or a publisher logo. Later attempts walk real pages in
                    // case the cover entry itself is damaged.
                    pageStream = pageIndex == 0
                        ? await reader.GetCoverAsync(filePath)
                        : await reader.GetPageAsync(filePath, pageIndex);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Ran off the end — no more pages to try.
                    if (pageIndex == 0) coverEntryThrew = true;   // opened, but has no usable images
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read page {Page} of item {ItemId}.", pageIndex, itemId);
                    lastError = $"Could not read cover: {ex.Message}";
                    if (pageIndex == 0) coverEntryThrew = true;
                    continue;   // this entry is damaged — try the next page
                }

                await using (pageStream)
                {
                    try
                    {
                        using var image = await Image.LoadAsync(pageStream);

                        // The spread rule runs BEFORE measuring, so the dimensions recorded describe the cropped,
                        // portrait cover the client will actually lay out against.
                        CoverImageAnalyzer.TryCropSpread(image);

                        var srcWidth = image.Width;
                        var srcHeight = image.Height;

                        if (image.Width > TargetWidth || image.Height > TargetHeight)
                            image.Mutate(x => x.Resize(new ResizeOptions
                            {
                                Size = new Size(TargetWidth, TargetHeight),
                                Mode = ResizeMode.Max,
                            }));

                        var encoder = new WebpEncoder
                        {
                            FileFormat = WebpFileFormatType.Lossy,
                            Quality = options.ThumbnailQuality,
                            Method = WebpEncodingMethod.Level4,
                        };
                        await image.SaveAsync(cachePath, encoder);
                        return ThumbnailResult.Ok(cachePath, srcWidth, srcHeight);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not decode page {Page} of item {ItemId}.", pageIndex, itemId);
                        lastError = $"Could not decode cover image: {ex.Message}";
                        // An undecodable image is not a corrupt archive — fall through and try the next page.
                    }
                }
            }

            // ArchiveUnreadable only when the COVER entry itself failed to extract and no later page rescued it.
            var reason = lastError ?? "No decodable page found in archive";
            return await FallBackAsync(itemId, filePath, placeholder, reason, coverEntryThrew)
                   ?? ThumbnailResult.Fail(reason, archiveUnreadable: coverEntryThrew);
        }

        /// <summary>
        /// The generated jacket, written into the cache like any other thumbnail — or null when the caller did
        /// not opt in (<paramref name="placeholder"/> is null) or the render itself failed, which leaves the
        /// ordinary failure to stand.
        ///
        /// <para>The reason is carried back on the result but the jacket counts as a SUCCESS, because that is
        /// what it is: the shelf has a cover. Whether the underlying file is also <i>broken</i> is a separate
        /// question, answered by <paramref name="archiveUnreadable"/> and recorded in <c>ItemState.IsBroken</c>,
        /// which the thumbnail job never retracts. Storing a <c>ThumbnailError</c> here instead would put ~1,500
        /// text-only books into <c>/admin/broken</c> as false "broken" tags — the exact failure the skip-path
        /// retraction was written to undo.</para>
        /// </summary>
        private async Task<ThumbnailResult?> FallBackAsync(
            long itemId, string filePath, PlaceholderCover? placeholder, string reason, bool archiveUnreadable)
        {
            if (placeholder == null) return null;

            var cachePath = GetCachePath(itemId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await using var jacket = await DocumentPlaceholderRenderer.CreateCoverAsync(
                    placeholder.Title, placeholder.Author, Path.GetFileName(filePath));
                using var image = await Image.LoadAsync(jacket);

                var srcWidth = image.Width;
                var srcHeight = image.Height;
                if (image.Width > TargetWidth || image.Height > TargetHeight)
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(TargetWidth, TargetHeight),
                        Mode = ResizeMode.Max,
                    }));

                await image.SaveAsync(cachePath, new WebpEncoder
                {
                    FileFormat = WebpFileFormatType.Lossy,
                    Quality = options.ThumbnailQuality,
                    Method = WebpEncodingMethod.Level4,
                });

                logger.LogInformation("Wrote a generated jacket for item {ItemId} ({Reason}).", itemId, reason);
                return ThumbnailResult.Generated(cachePath, reason, archiveUnreadable, srcWidth, srcHeight);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not render a placeholder jacket for item {ItemId}.", itemId);
                return null;
            }
        }

        /// <summary>
        /// The pixel dimensions from an image file's HEADER — no full decode. Used to backfill cover dimensions
        /// from already-cached thumbnails: those are written with <c>ResizeMode.Max</c> (or not resized at all
        /// when the source was already smaller), so they preserve the source cover's aspect ratio either way.
        /// </summary>
        public static bool TryReadImageSize(string imagePath, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                var info = Image.Identify(imagePath);
                if (info == null) return false;
                width = info.Width;
                height = info.Height;
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
