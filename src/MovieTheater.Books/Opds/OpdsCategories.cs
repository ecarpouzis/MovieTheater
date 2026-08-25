namespace MovieTheater.Books.Opds
{
    /// <summary>What one root-feed entry is, and what <c>GET /opds/{category}</c> will build for it.</summary>
    /// <param name="Key">The URL segment. Lower-case, hyphenated, stable — an e-reader stores the link forever.</param>
    /// <param name="Title">The shelf name the reader shows.</param>
    /// <param name="Subtitle">One line of explanation, emitted as the entry's content.</param>
    /// <param name="IsNavigation">A feed OF FEEDS (series, publishers) rather than of books.</param>
    /// <param name="NeedsUser">A personal shelf: hidden from the root when the caller carries no user id.</param>
    /// <param name="NeedsKey">Takes a <c>?key=</c> (the publisher drill), so it is never a root entry itself.</param>
    public sealed record OpdsCategory(string Key, string Title, string Subtitle,
        bool IsNavigation = false, bool NeedsUser = false, bool NeedsKey = false);

    /// <summary>
    /// The category table — ONE list, read both by the root feed (which entries to write) and by the category
    /// route (what to build). A category that exists in one and not the other is the classic OPDS bug: a root
    /// entry every reader shows and every reader 404s on.
    /// </summary>
    public static class OpdsCategories
    {
        public const string Recent = "recent";
        public const string Comics = "comics";
        public const string Books = "books";
        public const string SeriesList = "series";
        public const string PublisherList = "publishers";
        public const string Publisher = "publisher";
        public const string Kids = "kids";
        public const string WantToRead = "want-to-read";
        public const string InProgress = "in-progress";

        public static readonly IReadOnlyList<OpdsCategory> All =
        [
            new(Recent, "Recently added", "The newest arrivals in the library, newest first"),
            new(Comics, "Comics", "Every comic, alphabetically"),
            new(Books, "Books", "Every novel and ebook, alphabetically"),
            new(SeriesList, "Series", "Browse by series, A to Z", IsNavigation: true),
            new(PublisherList, "Publishers", "Browse by publisher", IsNavigation: true),
            new(Publisher, "Publisher", "Everything from one publisher", NeedsKey: true),
            new(Kids, "Kids", "All-ages titles only"),
            new(WantToRead, "Want to read", "The titles you marked to read", NeedsUser: true),
            new(InProgress, "In progress", "Where you left off", NeedsUser: true),
        ];

        /// <summary>Look a category up by its URL segment. Unknown ⇒ null ⇒ the route answers 404.</summary>
        public static OpdsCategory? Find(string? key) =>
            key == null ? null : All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
