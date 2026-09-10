using VersOne.Epub;
using VersOne.Epub.Options;

namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// The ONE set of EPUB parser options this site reads books with.
    ///
    /// <para>It exists because the two halves of the EPUB path — <see cref="EpubReaderService"/> for the
    /// reflowable reader, <see cref="EpubArchiveReader"/> for covers, pages and embedded metadata — were parsing
    /// the same file at two different strictnesses, so a book could have a cover and still refuse to open.</para>
    ///
    /// <para><b>Why the most permissive preset.</b> Measured over 1,200 books of this library on 2026-09-07,
    /// counting how many VersOne would hand back a non-empty reading order:</para>
    ///
    /// <list type="table">
    ///   <item><term>400 healthy <c>.epub</c></term><description>RELAXED 397 · IGNORE_ALL 400</description></item>
    ///   <item><term>400 flagged broken</term><description>RELAXED 298 · IGNORE_ALL 355</description></item>
    ///   <item><term>400 <c>.zip</c> (all real EPUBs)</term><description>RELAXED <b>0</b> · IGNORE_ALL 392</description></item>
    /// </list>
    ///
    /// <para>The <c>.zip</c> row is the one that decides it. Those 6,768 books declare a <c>toc.ncx</c> they do
    /// not ship, and RELAXED throws <c>Epub2NcxException</c> on every single one — a missing table of contents,
    /// refusing a book whose text is entirely present. Not one book in the 1,200 came back with an EMPTY reading
    /// order under any preset, so the permissive parse is not buying "readable" by quietly returning nothing.
    /// The handful that still fail fail under every preset; they arrive as a different exception type, which is
    /// worth saying out loud because it makes the message in a log less specific.</para>
    ///
    /// <para>This is a READ path only. Nothing here writes to a book, and a parser's complaint is still not
    /// evidence a file is corrupt — see <see cref="ArchiveFormatSniffer.CanOpenContainer"/>, which answers that
    /// question about the BYTES instead.</para>
    /// </summary>
    public static class EpubParsing
    {
        /// <summary>A fresh options object per call: <c>EpubReaderOptions</c> is mutable and not thread-safe.</summary>
        public static EpubReaderOptions Permissive() => new(EpubReaderOptionsPreset.IGNORE_ALL_ERRORS);

        /// <summary>
        /// Read a book, or THROW. The options overload is annotated as possibly returning null, and every caller
        /// on this path is already inside a <c>catch</c> that means "not an EPUB we can read" — so a null is
        /// turned into that same failure here rather than into a null reference three frames later, where the
        /// fallbacks (the raw-zip cover read, the typeset jacket) would never get their chance to run.
        /// </summary>
        public static EpubBook ReadOrThrow(string filePath) =>
            EpubReader.ReadBook(filePath, Permissive()) ?? throw NoBook(filePath);

        /// <inheritdoc cref="ReadOrThrow"/>
        public static async Task<EpubBook> ReadOrThrowAsync(string filePath) =>
            await EpubReader.ReadBookAsync(filePath, Permissive()) ?? throw NoBook(filePath);

        private static InvalidDataException NoBook(string filePath) =>
            new("The EPUB parser returned no book for " + Path.GetFileName(filePath));
    }
}
