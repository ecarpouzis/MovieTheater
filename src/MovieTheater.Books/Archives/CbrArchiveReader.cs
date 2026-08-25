using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Readers;
using ReaderOptions = SharpCompress.Readers.ReaderOptions;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// RAR-container comics (<c>.cbr</c>), read with SharpCompress — which also opens 7-Zip through
    /// <c>ArchiveFactory</c>, so a mislabelled 7z routed here by the sniffer still works.
    ///
    /// <para>Three attempts, in order, each cheaper-first: the random-access <c>ArchiveFactory</c> path; the
    /// SEQUENTIAL <c>ReaderFactory</c> path (solid archives whose central header SharpCompress can't seek);
    /// and finally the 7-Zip CLI for RAR5 and quirky headers neither managed path can parse.</para>
    /// </summary>
    public sealed class CbrArchiveReader : IArchiveReader
    {
        private readonly SevenZipCliExtractor sevenZip;
        public CbrArchiveReader(SevenZipCliExtractor sevenZip) => this.sevenZip = sevenZip;

        public bool CanHandle(string fileExtension) =>
            ".cbr".Equals(fileExtension, StringComparison.OrdinalIgnoreCase);

        public async Task<int> GetPageCountAsync(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var archive = ArchiveFactory.OpenArchive(stream);
                return ImageEntries(archive).Count();
            }
            catch
            {
                try { return SequentialKeys(filePath).Count; }
                catch when (sevenZip.IsAvailable) { return await sevenZip.CountImagesAsync(filePath) ?? 0; }
            }
        }

        public async Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var archive = ArchiveFactory.OpenArchive(stream);
                return ImageEntries(archive).Select(e => e.Key ?? "").ToList();
            }
            catch
            {
                try { return SequentialKeys(filePath); }
                catch when (sevenZip.IsAvailable)
                {
                    return (IReadOnlyList<string>?)await sevenZip.ListImagesAsync(filePath) ?? [];
                }
            }
        }

        public async Task<Stream> GetPageAsync(string filePath, int pageIndex)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var archive = ArchiveFactory.OpenArchive(stream);
                var entry = ImageEntries(archive).ElementAtOrDefault(pageIndex)
                    ?? throw new ArgumentOutOfRangeException(nameof(pageIndex));

                var ms = new MemoryStream();
                await using var entryStream = entry.OpenEntryStream();
                await entryStream.CopyToAsync(ms);
                ms.Position = 0;
                return ms;
            }
            catch
            {
                try
                {
                    return await GetPageSequentialAsync(filePath, pageIndex);
                }
                catch when (sevenZip.IsAvailable)
                {
                    var fallback = await sevenZip.ExtractImageAtAsync(filePath, pageIndex);
                    if (fallback != null) return fallback;
                    throw;
                }
            }
        }

        public Task<Stream> GetCoverAsync(string filePath) => GetPageAsync(filePath, 0);

        public async Task<ArchiveMetadata?> ReadMetadataAsync(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var archive = ArchiveFactory.OpenArchive(stream);
                var entry = archive.Entries.FirstOrDefault(e => !e.IsDirectory
                    && string.Equals(Path.GetFileName(e.Key), CbzArchiveReader.ComicInfoEntryName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;

                var ms = new MemoryStream();
                await using var entryStream = entry.OpenEntryStream();
                await entryStream.CopyToAsync(ms);
                ms.Position = 0;
                var doc = await XDocument.LoadAsync(ms, LoadOptions.None, CancellationToken.None);
                return doc.Root is { } root ? ComicInfoParser.Parse(root) : null;
            }
            catch
            {
                if (!sevenZip.IsAvailable) return null;
                var metaStream = await sevenZip.ExtractNamedEntryAsync(filePath, CbzArchiveReader.ComicInfoEntryName);
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
        }

        private static IEnumerable<IArchiveEntry> ImageEntries(IArchive archive) =>
            ArchiveEntryOrder.InPageOrder(
                archive.Entries.Where(e => !e.IsDirectory && ArchiveEntryOrder.IsImage(e.Key)),
                e => e.Key);

        /// <summary>The sequential pass: read every entry name once, in page order.</summary>
        private static List<string> SequentialKeys(string filePath)
        {
            using var reader = ReaderFactory.OpenReader(filePath, new ReaderOptions());
            var keys = new List<string>();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                if (ArchiveEntryOrder.IsImage(reader.Entry.Key)) keys.Add(reader.Entry.Key!);
            }
            return ArchiveEntryOrder.InPageOrder(keys, k => k).ToList();
        }

        private static async Task<Stream> GetPageSequentialAsync(string filePath, int pageIndex)
        {
            // Two passes: name the entries in page order first, then stream the one that lands at the index.
            var sortedKeys = SequentialKeys(filePath);
            if (pageIndex < 0 || pageIndex >= sortedKeys.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var targetKey = sortedKeys[pageIndex];

            await using var reader = await ReaderFactory.OpenAsyncReader(filePath, new ReaderOptions(), CancellationToken.None);
            while (await reader.MoveToNextEntryAsync(CancellationToken.None))
            {
                if (reader.Entry.IsDirectory || reader.Entry.Key != targetKey) continue;
                var ms = new MemoryStream();
                await using var entryStream = await reader.OpenEntryStreamAsync(CancellationToken.None);
                await entryStream.CopyToAsync(ms);
                ms.Position = 0;
                return ms;
            }
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
    }
}
