namespace MovieTheater.Books.Archives
{
    /// <summary>
    /// The ONE place the page ordering of an archive is decided, and the one list of what counts as a page.
    ///
    /// <para><b>Ordering is ordinal, case-insensitive, on the entry's full path</b> — the standalone site's
    /// ordering, ported unchanged and deliberately NOT swapped for a numeric-aware "natural" sort. Every
    /// <c>Item.PageCount</c> in the catalog and every migrated <c>UserItemState.LastPage</c> was produced under
    /// this comparer; a natural sort re-orders any archive whose page numbers are not zero-padded consistently
    /// ("p10.jpg" before "p9.jpg" becomes after it), which would silently move every saved reading position in
    /// those books. If the ordering is ever changed it has to be changed here, once, with a re-scan of PageCount
    /// and a decision about existing positions — not per reader.</para>
    /// </summary>
    public static class ArchiveEntryOrder
    {
        /// <summary>Extensions an archive entry must carry to count as a page.</summary>
        public static readonly HashSet<string> ImageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif" };

        public static bool IsImage(string? entryName) =>
            !string.IsNullOrEmpty(entryName) && ImageExtensions.Contains(Path.GetExtension(entryName));

        /// <summary>The page-order comparer. See the type remarks before changing it.</summary>
        public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        public static IEnumerable<T> InPageOrder<T>(IEnumerable<T> entries, Func<T, string?> key) =>
            entries.OrderBy(e => key(e) ?? "", Comparer);
    }
}
