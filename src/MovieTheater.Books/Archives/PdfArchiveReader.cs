using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Comic and document PDFs. Page images come from <see cref="PdfPageImageSource"/>: the embedded full-page
    /// image untouched for image-per-page comic PDFs, a pdfium raster when the page carries visible text,
    /// composites or vector content. What is returned here is a full-size image; scaling and re-encoding for the
    /// wire happen downstream in <c>ImageScalingService</c> / <c>ThumbnailService</c>.
    /// </summary>
    public sealed class PdfArchiveReader : IArchiveReader
    {
        public bool CanHandle(string fileExtension) =>
            ".pdf".Equals(fileExtension, StringComparison.OrdinalIgnoreCase);

        public Task<int> GetPageCountAsync(string filePath)
        {
            using var document = PdfDocument.Open(filePath);
            return Task.FromResult(Math.Max(1, document.NumberOfPages));
        }

        /// <summary>A PDF page has no entry name; the positional names keep the interface uniform.</summary>
        public async Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath)
        {
            var count = await GetPageCountAsync(filePath);
            return Enumerable.Range(1, count).Select(n => $"page {n}").ToList();
        }

        public Task<Stream> GetPageAsync(string filePath, int pageIndex)
        {
            using var document = PdfDocument.Open(filePath);
            var pageCount = Math.Max(1, document.NumberOfPages);
            if (pageIndex < 0 || pageIndex >= pageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var page = document.GetPage(pageIndex + 1);
            if (TryGetPageImage(filePath, page, pageIndex, out var bytes))
                return Task.FromResult<Stream>(new MemoryStream(bytes));

            return DocumentPlaceholderRenderer.CreatePageAsync(Path.GetFileName(filePath), pageIndex, pageCount);
        }

        public Task<Stream> GetCoverAsync(string filePath)
        {
            using var document = PdfDocument.Open(filePath);
            if (document.NumberOfPages <= 0)
                return DocumentPlaceholderRenderer.CreateCoverAsync(Path.GetFileName(filePath));

            var page = document.GetPage(1);
            if (TryGetPageImage(filePath, page, 0, out var bytes))
                return Task.FromResult<Stream>(new MemoryStream(bytes));

            return DocumentPlaceholderRenderer.CreateCoverAsync(Path.GetFileName(filePath));
        }

        private static bool TryGetPageImage(string filePath, Page page, int pageIndex, out byte[] bytes) =>
            PdfPageImageSource.TryGetPageImage(filePath, page, pageIndex, out bytes, out _);

        public Task<ArchiveMetadata?> ReadMetadataAsync(string filePath)
        {
            using var document = PdfDocument.Open(filePath);
            var info = document.Information;

            return Task.FromResult<ArchiveMetadata?>(new ArchiveMetadata
            {
                IssueTitle = string.IsNullOrWhiteSpace(info.Title) ? Path.GetFileNameWithoutExtension(filePath) : info.Title,
                Writers = string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
                Description = string.IsNullOrWhiteSpace(info.Subject) ? null : info.Subject,
                Tags = string.IsNullOrWhiteSpace(info.Keywords) ? null : info.Keywords,
                PublicationDate = string.IsNullOrWhiteSpace(info.CreationDate) ? null : info.CreationDate,
                PageCount = Math.Max(1, document.NumberOfPages),
            });
        }
    }
}
