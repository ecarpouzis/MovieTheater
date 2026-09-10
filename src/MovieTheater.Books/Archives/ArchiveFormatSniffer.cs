using System.IO.Compression;
using SharpCompress.Archives;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// Routes a file to a reader by its LEADING MAGIC BYTES, not its extension, so a comic archive saved under
    /// the wrong name still opens — a RAR named <c>.cbz</c>, a 7-Zip named <c>.cbr</c>.
    ///
    /// <para>The standalone site's library audit found ~35 % of its "unreadable" comics were simply misnamed
    /// (RAR data inside a <c>.cbz</c>), failing only because the extension forced them through the ZIP-only
    /// reader. Sniffing removes that whole class of failure — and it is why a "broken" flag is only ever set
    /// after the sniffed reader has also failed.</para>
    /// </summary>
    public static class ArchiveFormatSniffer
    {
        /// <summary>
        /// The extensions whose real container we second-guess. Format-specific ones (<c>.pdf</c>, <c>.epub</c>,
        /// <c>.mobi</c>) are deliberately excluded: an EPUB *is* a ZIP but needs the EPUB reader, so re-routing
        /// it by raw container would break it.
        ///
        /// <para><c>.zip</c> and <c>.rar</c> are here because a bare archive name says nothing about what is
        /// inside. 11,181 books in the library carry one and no reader claimed either extension, so every one
        /// failed with "No archive reader" and has no cover. Sniffing found 87% of the <c>.zip</c> files to be
        /// EPUBs under the wrong name — a real <c>mimetype</c> and <c>META-INF/container.xml</c> inside — which
        /// is precisely the case the exclusion above warns about. So a ZIP is separated by its CONTENT before
        /// it is routed, rather than sent wholesale to the comic reader.</para>
        /// </summary>
        private static readonly HashSet<string> GenericArchiveExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".cbr", ".cb7", ".cbt", ".zip", ".rar" };

        public enum Container { Unknown, Zip, Rar, SevenZip }

        /// <summary>
        /// The extension of the reader that should handle this file. Returns <paramref name="declaredExtension"/>
        /// unchanged for a format-specific type, an unreadable file, or an unrecognized container — those all
        /// mean "trust the extension".
        /// </summary>
        public static string ResolveReaderExtension(string filePath, string? declaredExtension)
        {
            var declared = declaredExtension ?? "";
            if (!GenericArchiveExtensions.Contains(declared)) return declared;

            return Detect(filePath) switch
            {
                // A ZIP that carries the EPUB signature is a BOOK, and needs the reader that knows how to find
                // a book's cover — the comic reader would hand back whichever image sorted first.
                Container.Zip => IsEpubZip(filePath) ? ".epub" : ".cbz",
                Container.Rar => ".cbr",
                // SharpCompress's ArchiveFactory (behind the .cbr reader) opens 7-Zip too.
                Container.SevenZip => ".cbr",
                _ => declared,
            };
        }

        /// <summary>
        /// Whether a ZIP is really an EPUB. The OCF signature, in the order the spec makes it definitive: the
        /// <c>mimetype</c> entry holding <c>application/epub+zip</c>, and failing that the
        /// <c>META-INF/container.xml</c> every EPUB must ship. Both are read from the entry list rather than
        /// guessed from names elsewhere in the archive, so a comic that merely contains a stray
        /// <c>container.xml</c> is not mistaken for a book.
        /// </summary>
        public static bool IsEpubZip(string filePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                var mimetype = zip.GetEntry("mimetype");
                if (mimetype != null)
                {
                    using var reader = new StreamReader(mimetype.Open());
                    Span<char> buf = stackalloc char[64];
                    var n = reader.Read(buf);
                    if (new string(buf[..Math.Max(0, n)]).Trim()
                            .StartsWith("application/epub+zip", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return zip.GetEntry("META-INF/container.xml") != null;
            }
            catch
            {
                // Unopenable, or a stream that will not read: no opinion, and the caller falls back to .cbz.
                return false;
            }
        }

        /// <summary>
        /// <b>Which READER SURFACE an item needs</b> — the one answer no caller is allowed to derive for itself
        /// from the stored extension, and the memoized form of <see cref="ResolveReaderExtension"/>.
        ///
        /// <para>6,768 books in this library are EPUBs saved as <c>.zip</c> (the OCF signature was found in all
        /// 200 of a 200-file sample). The archive readers have always coped, because they route by magic bytes —
        /// but four places asked the EXTENSION instead and so treated every one of those books as a comic: the
        /// SPA opened the canvas reader (which looks inside an EPUB for image pages, finds none, and shows a book
        /// that "will not open"), <c>/epub/*</c> 404'd, the grid cover came from spine page 0 rather than the
        /// declared cover, and the EPUB resource route 404'd every image a chapter referenced. All four now ask
        /// here.</para>
        ///
        /// <para><b>Only a BOOK with a generic container extension is sniffed</b>, and only then is the file
        /// touched. A comic reads on the canvas whichever of ZIP/RAR/7-Zip it turns out to be, so paying an SMB
        /// open per request for 118k comics would buy nothing. An unreadable or missing file yields the declared
        /// extension — "trust the extension" is this class's own fallback, and the reader then fails with its own
        /// message rather than this one guessing.</para>
        ///
        /// <para>The answer is memoized on <c>(path, mtime ticks)</c>, because it is asked once per resource an
        /// EPUB chapter references, not once per book. A replaced file changes its ticks and so misses the cache;
        /// the map is bounded by <see cref="ReaderFormatCacheLimit"/> and cleared wholesale when it is reached,
        /// which costs one re-sniff per live book and cannot leak.</para>
        /// </summary>
        public static string? ReaderFormatFor(bool isBook, string filePath, string? declaredExtension, long fileTicks)
        {
            if (!isBook) return declaredExtension;
            if (!GenericBookContainers.Contains(declaredExtension ?? "")) return declaredExtension;

            var key = (filePath, fileTicks);
            if (readerFormatCache.TryGetValue(key, out var hit)) return hit;

            var resolved = ResolveReaderExtension(filePath, declaredExtension);
            if (readerFormatCache.Count >= ReaderFormatCacheLimit) readerFormatCache.Clear();
            readerFormatCache[key] = resolved;
            return resolved;
        }

        /// <summary>The extensions that say nothing about what is inside, on the BOOK side. A <c>.cbz</c>/<c>.cbr</c>
        /// on a book is not sniffed: it already names the canvas reader, which is where it would land anyway.</summary>
        private static readonly HashSet<string> GenericBookContainers =
            new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar" };

        private const int ReaderFormatCacheLimit = 20_000;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Ticks), string>
            readerFormatCache = new();

        /// <summary>Classify the container from its first bytes. Unopenable ⇒ <see cref="Container.Unknown"/>.</summary>
        public static Container Detect(string filePath)
        {
            Span<byte> head = stackalloc byte[8];
            int n;
            try
            {
                using var fs = File.OpenRead(filePath);
                n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            catch
            {
                return Container.Unknown;
            }

            if (n < 4) return Container.Unknown;

            // ZIP: "PK\x03\x04" local header, plus the empty (\x05\x06) and spanned (\x07\x08) variants.
            if (head[0] == 'P' && head[1] == 'K' && head[2] is 3 or 5 or 7) return Container.Zip;
            // RAR4 "Rar!\x1A\x07\x00" and RAR5 "Rar!\x1A\x07\x01\x00" share this 4-byte prefix.
            if (head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!') return Container.Rar;
            // 7-Zip "7z\xBC\xAF\x27\x1C".
            if (n >= 6 && head[0] == '7' && head[1] == 'z'
                && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C) return Container.SevenZip;

            return Container.Unknown;
        }

        /// <summary>
        /// Whether the CONTAINER opens and enumerates — the byte-level question, asked WITHOUT the format's own
        /// parser. <c>true</c> it opens, <c>false</c> it does not, and <c>null</c> for bytes this cannot sniff
        /// (a PDF, a MOBI, an unrecognized head), which means "no opinion — the reader's verdict stands".
        ///
        /// <para><b>A parser's complaint is not a corrupt file.</b> VersOne throws on an EPUB whose spine declares
        /// no TOC or whose manifest names a cover it does not ship, in a book that opens and reads perfectly —
        /// 1,136 of the 1,163 rows the broken flag carried into v2 were exactly that, every one of them with a
        /// cover thumbnail already on disk. <see cref="Services.ThumbnailService"/> draws this line with
        /// <c>ThumbnailResult.ArchiveUnreadable</c>; this is the same line, for the scan path.</para>
        /// </summary>
        public static bool? CanOpenContainer(string filePath)
        {
            switch (Detect(filePath))
            {
                case Container.Zip:
                    try
                    {
                        using var zip = ZipFile.OpenRead(filePath);
                        _ = zip.Entries.Count;   // the central directory, actually read
                        return true;
                    }
                    catch { return false; }

                case Container.Rar:
                case Container.SevenZip:
                    try
                    {
                        using var stream = File.OpenRead(filePath);
                        using var archive = ArchiveFactory.OpenArchive(stream);
                        _ = archive.Entries.Any();
                        return true;
                    }
                    catch { return false; }

                default:
                    return null;
            }
        }
    }

    /// <summary>Reader selection that routes by the real container. The only way a reader is ever picked.</summary>
    public static class ArchiveReaderSelection
    {
        public static IArchiveReader? ForFile(
            this IEnumerable<IArchiveReader> readers, string filePath, string? declaredExtension)
        {
            var ext = ArchiveFormatSniffer.ResolveReaderExtension(filePath, declaredExtension);
            return readers.FirstOrDefault(r => r.CanHandle(ext));
        }
    }
}
