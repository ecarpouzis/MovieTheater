using Microsoft.Extensions.Caching.Memory;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// The raw bytes of one extracted page, so a reading session touches the file once per page (per sliding
    /// window) instead of re-opening the archive on every request. Without it each page is pulled out several
    /// times over: the image request, the Bubble Zoom text-region pass, and any re-read all re-open the file,
    /// enumerate and sort its entries, and extract again — and CBR (SharpCompress) and PDF (a PdfPig re-parse)
    /// pay that cost in full.
    ///
    /// <para>It owns a DEDICATED <see cref="MemoryCache"/> with a byte <c>SizeLimit</c>. The shared DI
    /// <c>IMemoryCache</c> is a COUNT-limited cache (the browse heads/facets budget), so per-entry byte sizes
    /// would be meaningless there and one omnibus session could crowd out every cached facet set.</para>
    /// </summary>
    public sealed class PageByteCache : IDisposable
    {
        private readonly MemoryCache cache;
        private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

        public PageByteCache(BooksOptions options)
        {
            var limitBytes = Math.Max(16, options.PageCacheLimitMb) * 1024L * 1024L;
            cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = limitBytes });
        }

        /// <summary>
        /// The key for one page. <paramref name="modifiedTicks"/> means a replaced or re-scanned file invalidates
        /// naturally; <paramref name="variant"/> separates the EPUB cover path from spine page 0, which are
        /// different images at the same index.
        /// </summary>
        public static string Key(string filePath, long modifiedTicks, int pageIndex, string? variant = null) =>
            variant is null
                ? $"{filePath}|{modifiedTicks}|{pageIndex}"
                : $"{filePath}|{modifiedTicks}|{pageIndex}|{variant}";

        /// <summary>
        /// The cached bytes, or run <paramref name="extract"/> (which reads the file), cache and return them.
        /// Exceptions from the extractor — including <see cref="ArgumentOutOfRangeException"/> for a past-the-end
        /// prefetch probe — propagate and cache nothing.
        /// </summary>
        public async Task<byte[]> GetOrExtractAsync(string key, Func<Task<Stream>> extract)
        {
            if (cache.TryGetValue(key, out byte[]? cached) && cached is not null) return cached;

            await using var stream = await extract();
            byte[] bytes;
            if (stream is MemoryStream ms)
            {
                bytes = ms.ToArray();
            }
            else
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = Sliding });
            return bytes;
        }

        public void Dispose() => cache.Dispose();
    }
}
