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
        /// </summary>
        private static readonly HashSet<string> GenericArchiveExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".cbz", ".cbr", ".cb7", ".cbt" };

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
                Container.Zip => ".cbz",
                Container.Rar => ".cbr",
                // SharpCompress's ArchiveFactory (behind the .cbr reader) opens 7-Zip too.
                Container.SevenZip => ".cbr",
                _ => declared,
            };
        }

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
