using System.IO.Compression;
using System.Xml.Linq;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// ZIP-container comics (<c>.cbz</c>), read with <c>System.IO.Compression</c>. Every failure that is about
    /// the CONTAINER (a corrupt central directory, an LZMA-method entry the BCL lists but cannot inflate) falls
    /// through to <see cref="SevenZipCliExtractor"/>; a genuine out-of-range page does not, because that is a
    /// caller error, not an archive problem.
    /// </summary>
    public sealed class CbzArchiveReader : IArchiveReader
    {
        private readonly SevenZipCliExtractor sevenZip;
        public CbzArchiveReader(SevenZipCliExtractor sevenZip) => this.sevenZip = sevenZip;

        public bool CanHandle(string fileExtension) =>
            ".cbz".Equals(fileExtension, StringComparison.OrdinalIgnoreCase);

        public async Task<int> GetPageCountAsync(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                return ImageEntries(zip).Count();
            }
            catch when (sevenZip.IsAvailable)
            {
                return await sevenZip.CountImagesAsync(filePath) ?? 0;
            }
        }

        public async Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                return ImageEntries(zip).Select(e => e.FullName).ToList();
            }
            catch when (sevenZip.IsAvailable)
            {
                return (IReadOnlyList<string>?)await sevenZip.ListImagesAsync(filePath) ?? [];
            }
        }

        public async Task<Stream> GetPageAsync(string filePath, int pageIndex)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var entry = ImageEntries(zip).ElementAtOrDefault(pageIndex)
                    ?? throw new ArgumentOutOfRangeException(nameof(pageIndex));

                var ms = new MemoryStream();
                await using var entryStream = entry.Open();
                await entryStream.CopyToAsync(ms);
                ms.Position = 0;
                return ms;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw;   // a real out-of-range page request — not an archive problem
            }
            catch when (sevenZip.IsAvailable)
            {
                var fallback = await sevenZip.ExtractImageAtAsync(filePath, pageIndex);
                if (fallback != null) return fallback;
                throw;
            }
        }

        public Task<Stream> GetCoverAsync(string filePath) => GetPageAsync(filePath, 0);

        public async Task<ArchiveMetadata?> ReadMetadataAsync(string filePath)
        {
            Stream? metaStream = null;
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals(ComicInfoEntryName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                var ms = new MemoryStream();
                await using (var stream = entry.Open()) await stream.CopyToAsync(ms);
                ms.Position = 0;
                metaStream = ms;
            }
            catch when (sevenZip.IsAvailable)
            {
                // ComicInfo.xml itself may be stored with unsupported compression.
                metaStream = await sevenZip.ExtractNamedEntryAsync(filePath, ComicInfoEntryName);
            }

            if (metaStream == null) return null;
            await using (metaStream)
            {
                try
                {
                    var doc = await XDocument.LoadAsync(metaStream, LoadOptions.None, CancellationToken.None);
                    return doc.Root is { } root ? ComicInfoParser.Parse(root) : null;
                }
                catch { return null; }
            }
        }

        internal const string ComicInfoEntryName = "ComicInfo.xml";

        private static IEnumerable<ZipArchiveEntry> ImageEntries(ZipArchive zip) =>
            ArchiveEntryOrder.InPageOrder(
                zip.Entries.Where(e => ArchiveEntryOrder.IsImage(e.Name)
                                       && !e.Name.Equals(ComicInfoEntryName, StringComparison.OrdinalIgnoreCase)),
                e => e.FullName);
    }
}
