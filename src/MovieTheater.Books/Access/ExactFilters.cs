using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;
using MovieTheater.Books.Services;

namespace MovieTheater.Books.Access
{
    /// <summary>The credit roles the browse facets file a person under. One vocabulary for the facets and the filters.</summary>
    public static class CreditRoles
    {
        public static readonly string[] Authors = { "Writer", "Author" };
        public static readonly string[] Artists = { "Penciller", "Artist", "Cover Artist" };
    }

    /// <summary>
    /// The EXACT facet filters an OData <c>$filter</c> over <see cref="Projections.ItemSummary"/> cannot express:
    /// a person by credit role, a tag, a crossover event. The projection carries only display CSVs
    /// (<c>creatorsCsv</c>, <c>tagsCsv</c>) and no event at all, and a <c>contains()</c> over a CSV is role-blind
    /// and matches "Film Noir" for "Noir". These land on the ROWS the facets count from — <c>ItemCredit</c>,
    /// <c>ItemTag</c> + <c>SeriesTag</c>, <c>ComicDetail.EventName</c> — so a facet chip filters exactly what its
    /// count promised.
    ///
    /// <para>Semantics are the standalone site's: OR within a facet, AND across facets, and every facet has an
    /// exclude twin (<c>exAuthor=</c>…) that is a NOT EXISTS. Values are repeatable query parameters
    /// (<c>author=A&amp;author=B</c>), never comma-split — names contain commas.</para>
    ///
    /// <para>Credits match on the NORMALIZED name, which is what the facets group by; both normalizers the
    /// library has ever written (<see cref="LibraryScanner.Normalize"/> strips punctuation,
    /// <see cref="Transforms.NormalizeName"/> only folds whitespace) are tried, plus the raw name, so a chip
    /// built from either generation of rows finds its items. Tags accept the facets' composite
    /// <c>category:value</c> spelling to pin a category; a bare value matches any category, on the item's own
    /// tags or its series'.</para>
    /// </summary>
    public sealed class ExactFilters
    {
        public IReadOnlyList<string> Authors { get; }
        public IReadOnlyList<string> Artists { get; }
        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<string> Events { get; }
        public IReadOnlyList<string> ExAuthors { get; }
        public IReadOnlyList<string> ExArtists { get; }
        public IReadOnlyList<string> ExTags { get; }
        public IReadOnlyList<string> ExEvents { get; }

        public static readonly ExactFilters None = new([], [], [], [], [], [], [], []);

        private ExactFilters(IReadOnlyList<string> authors, IReadOnlyList<string> artists, IReadOnlyList<string> tags,
            IReadOnlyList<string> events, IReadOnlyList<string> exAuthors, IReadOnlyList<string> exArtists,
            IReadOnlyList<string> exTags, IReadOnlyList<string> exEvents)
        {
            Authors = authors; Artists = artists; Tags = tags; Events = events;
            ExAuthors = exAuthors; ExArtists = exArtists; ExTags = exTags; ExEvents = exEvents;
        }

        /// <summary>Bind from the query: blanks dropped, duplicates folded, order kept (the signature depends on it).</summary>
        public static ExactFilters From(string[]? author, string[]? artist, string[]? tag, string[]? @event,
            string[]? exAuthor = null, string[]? exArtist = null, string[]? exTag = null, string[]? exEvent = null)
        {
            var f = new ExactFilters(Clean(author), Clean(artist), Clean(tag), Clean(@event),
                Clean(exAuthor), Clean(exArtist), Clean(exTag), Clean(exEvent));
            return f.IsEmpty ? None : f;
        }

        public bool IsEmpty =>
            Authors.Count + Artists.Count + Tags.Count + Events.Count
            + ExAuthors.Count + ExArtists.Count + ExTags.Count + ExEvents.Count == 0;

        /// <summary>A stable text form for cache keys: empty for no filter, so unfiltered signatures are unchanged.</summary>
        public string Sig => IsEmpty
            ? ""
            : string.Join("|",
                Part("a", Authors), Part("A", ExAuthors), Part("r", Artists), Part("R", ExArtists),
                Part("t", Tags), Part("T", ExTags), Part("e", Events), Part("E", ExEvents));

        public IQueryable<Item> Apply(BooksDb db, IQueryable<Item> items)
        {
            if (IsEmpty) return items;
            if (Authors.Count > 0) items = WithCredit(db, items, CreditRoles.Authors, Authors, exclude: false);
            if (ExAuthors.Count > 0) items = WithCredit(db, items, CreditRoles.Authors, ExAuthors, exclude: true);
            if (Artists.Count > 0) items = WithCredit(db, items, CreditRoles.Artists, Artists, exclude: false);
            if (ExArtists.Count > 0) items = WithCredit(db, items, CreditRoles.Artists, ExArtists, exclude: true);
            if (Tags.Count > 0) items = WithTags(db, items, Tags, exclude: false);
            if (ExTags.Count > 0) items = WithTags(db, items, ExTags, exclude: true);
            if (Events.Count > 0) items = WithEvents(items, Events, exclude: false);
            if (ExEvents.Count > 0) items = WithEvents(items, ExEvents, exclude: true);
            return items;
        }

        // ── the three row sources ──

        private static IQueryable<Item> WithCredit(BooksDb db, IQueryable<Item> items, string[] roles,
            IReadOnlyList<string> values, bool exclude)
        {
            var keys = values.SelectMany(v => new[] { LibraryScanner.Normalize(v), Transforms.NormalizeName(v) })
                .Where(k => k.Length > 0).Distinct().ToList();
            var names = values.ToList();
            // A subquery, never an id list: a prolific writer has thousands of credits and SQLite caps IN lists.
            var matched = db.ItemCredits.AsNoTracking()
                .Where(c => c.Role != null && roles.Contains(c.Role)
                            && ((c.NormalizedName != null && keys.Contains(c.NormalizedName))
                                || (c.Name != null && names.Contains(c.Name))))
                .Select(c => c.ItemId);
            return exclude
                ? items.Where(i => !matched.Contains(i.Id))
                : items.Where(i => matched.Contains(i.Id));
        }

        private static IQueryable<Item> WithTags(BooksDb db, IQueryable<Item> items, IReadOnlyList<string> values, bool exclude)
        {
            var plain = new List<string>();
            var pinned = new List<(string Category, string Value)>();
            foreach (var v in values)
            {
                var i = v.IndexOf(':');
                if (i <= 0 || i == v.Length - 1) plain.Add(v);
                else pinned.Add((v[..i].Trim(), v[(i + 1)..].Trim()));
            }

            IQueryable<int> itemIds = db.ItemTags.AsNoTracking().Where(t => plain.Contains(t.Value)).Select(t => t.ItemId);
            IQueryable<int> seriesIds = db.SeriesTags.AsNoTracking().Where(t => plain.Contains(t.Value)).Select(t => t.SeriesId);
            foreach (var (category, value) in pinned)
            {
                var cat = category;
                var val = value;
                itemIds = itemIds.Concat(db.ItemTags.AsNoTracking().Where(t => t.Category == cat && t.Value == val).Select(t => t.ItemId));
                seriesIds = seriesIds.Concat(db.SeriesTags.AsNoTracking().Where(t => t.Category == cat && t.Value == val).Select(t => t.SeriesId));
            }

            return exclude
                ? items.Where(i => !itemIds.Contains(i.Id) && !(i.SeriesId != null && seriesIds.Contains(i.SeriesId.Value)))
                : items.Where(i => itemIds.Contains(i.Id) || (i.SeriesId != null && seriesIds.Contains(i.SeriesId.Value)));
        }

        private static IQueryable<Item> WithEvents(IQueryable<Item> items, IReadOnlyList<string> values, bool exclude)
        {
            var names = values.ToList();
            return exclude
                ? items.Where(i => i.Comic == null || i.Comic.EventName == null || !names.Contains(i.Comic.EventName))
                : items.Where(i => i.Comic != null && i.Comic.EventName != null && names.Contains(i.Comic.EventName));
        }

        private static IReadOnlyList<string> Clean(string[]? raw) =>
            raw == null ? [] : raw.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        private static string Part(string tag, IReadOnlyList<string> values) =>
            values.Count == 0 ? "" : tag + ":" + string.Join(",", values);
    }
}
