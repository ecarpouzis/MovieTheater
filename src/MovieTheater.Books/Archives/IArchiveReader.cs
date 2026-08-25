namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// One container format, read. Every byte the reader serves — a page, a cover, a thumbnail source —
    /// comes through this interface, so the media plane never has to know whether it is holding a ZIP, a RAR,
    /// a PDF, an EPUB or a MOBI.
    ///
    /// <para><b>Page index is an ORDINAL POSITION in a fixed ordering</b>, not a name. Stored reading positions
    /// (<c>UserItemState.LastPage</c>) and every <c>Item.PageCount</c> in the catalog were produced under that
    /// ordering, so it is pinned in exactly one place — <see cref="ArchiveEntryOrder"/> — and changing it
    /// re-indexes the whole library's saved positions.</para>
    ///
    /// <para>All reads are read-only: nothing here ever writes to, renames or deletes a library file.</para>
    /// </summary>
    public interface IArchiveReader
    {
        bool CanHandle(string fileExtension);

        /// <summary>How many pages the container has, in the ordering of <see cref="GetPageNamesAsync"/>.</summary>
        Task<int> GetPageCountAsync(string filePath);

        /// <summary>The bytes of one page, by ordinal position. Out of range throws <see cref="ArgumentOutOfRangeException"/>.</summary>
        Task<Stream> GetPageAsync(string filePath, int pageIndex);

        /// <summary>
        /// The cover. For CBZ/CBR/PDF this IS page 0; for EPUB it is the declared cover image (a spine page 0 is
        /// usually a title page), and for MOBI the embedded jacket — which is why callers must not just ask for
        /// page 0 when they mean "the cover".
        /// </summary>
        Task<Stream> GetCoverAsync(string filePath);

        /// <summary>
        /// The page entry names in ordinal order — index <c>i</c> here is the page <see cref="GetPageAsync"/>
        /// serves at <c>i</c>. Formats with no named entries (PDF, MOBI) synthesize positional names.
        /// </summary>
        Task<IReadOnlyList<string>> GetPageNamesAsync(string filePath);

        /// <summary>The container's embedded metadata (ComicInfo.xml, the OPF package, PDF document info).</summary>
        Task<ArchiveMetadata?> ReadMetadataAsync(string filePath);
    }
}
