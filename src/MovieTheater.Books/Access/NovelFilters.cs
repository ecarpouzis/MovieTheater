using System.Globalization;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Access
{
    /// <summary>
    /// The query the NOVELS rail expresses, as one reusable filter — author / series / publisher / decade /
    /// tag / excludeTag / minRating / unknown.
    ///
    /// <para><b>Why this is a type and not a method on <see cref="Controllers.NovelsController"/>.</b> The flat
    /// novels list and the grouped browse are two surfaces over one shelf, and a reader switches between them
    /// with the View pill. If they filtered through different code the two would disagree the moment a facet
    /// was set — the same book present in Grid and absent in Shelves. So the definition lives here and BOTH
    /// controllers apply it; there is no second spelling of "by this author" to drift from.</para>
    ///
    /// <para>It is deliberately NOT folded into <see cref="ExactFilters"/>, which is the COMIC rail's language:
    /// that one matches a credit on its normalized name across every author-ish role and any source, which is
    /// right for comics (one writer, many spellings, many sources) and wrong here. A novel's author is the
    /// name Calibre wrote, and <c>/novels/facets</c> counts those exact strings — so a chip must select
    /// exactly the books its own count promised.</para>
    ///
    /// <para>Every value is comma-separated, OR within a facet and AND across, and a tag may pin its category
    /// (<c>genre:dystopian</c>) or match any (<c>dystopian</c>) — the composite spelling <c>/novels/facets</c>
    /// hands back, so a chip round-trips unchanged.</para>
    /// </summary>
    public sealed class NovelFilters
    {
        /// <summary>Calibre's own author role — the one credit source a book actually has.</summary>
        public const string AuthorRole = "Author";

        public static readonly NovelFilters None = new(null, null, null, null, null, null, null, false);

        private readonly List<string> authors;
        private readonly List<string> series;
        private readonly List<string> publishers;
        private readonly List<int> decades;
        private readonly List<(string? Category, string Value)> tags;
        private readonly List<(string? Category, string Value)> exTags;
        private readonly int? minRating;
        private readonly bool unknown;

        private NovelFilters(string? author, string? series, string? publisher, string? decade,
            string? tag, string? excludeTag, int? minRating, bool unknown)
        {
            authors = Csv(author);
            this.series = Csv(series);
            publishers = Csv(publisher);
            decades = Decades(decade);
            tags = Tags(tag);
            exTags = Tags(excludeTag);
            this.minRating = minRating is int floor && floor > 0 ? floor : null;
            this.unknown = unknown;
        }

        /// <summary>Bind from the query; an all-blank set folds to <see cref="None"/> so signatures stay clean.</summary>
        public static NovelFilters From(string? author = null, string? series = null, string? publisher = null,
            string? decade = null, string? tag = null, string? excludeTag = null, int? minRating = null,
            bool unknown = false)
        {
            var f = new NovelFilters(author, series, publisher, decade, tag, excludeTag, minRating, unknown);
            return f.IsEmpty ? None : f;
        }

        public bool IsEmpty =>
            authors.Count + series.Count + publishers.Count + decades.Count + tags.Count + exTags.Count == 0
            && minRating == null && !unknown;

        /// <summary>A stable text form for cache keys: empty for no filter, so unfiltered signatures are unchanged.</summary>
        public string Sig => IsEmpty
            ? ""
            : string.Join("|",
                $"a={string.Join(",", authors)}", $"s={string.Join(",", series)}",
                $"p={string.Join(",", publishers)}", $"d={string.Join(",", decades)}",
                $"t={Part(tags)}", $"T={Part(exTags)}",
                $"r={minRating}", $"u={(unknown ? 1 : 0)}");

        /// <summary>
        /// Applied to the ENTITY set, before the projection — the same place <see cref="ExactFilters.Apply"/>
        /// lands, so heads, bands, letters and counts all fall out of one filtered set and cannot disagree.
        /// </summary>
        public IQueryable<Item> Apply(BooksDb db, IQueryable<Item> items)
        {
            if (IsEmpty) return items;

            foreach (var (category, value) in exTags)
            {
                var cat = category;
                var val = value;
                items = cat == null
                    ? items.Where(i => !db.ItemTags.Any(t => t.ItemId == i.Id && t.Value == val))
                    : items.Where(i => !db.ItemTags.Any(t => t.ItemId == i.Id && t.Category == cat && t.Value == val));
            }

            if (minRating is int floor)
                items = items.Where(i => i.ResolvedRating != null && i.ResolvedRating >= floor);

            // ONLY the books with no current insight row — the "no metadata yet" pile, the inverse of what the
            // rest of the rail can reach.
            if (unknown)
                items = items.Where(i => !db.Insights.Any(n => n.SubjectKind == SubjectKind.Item && n.SubjectId == i.Id && n.IsCurrent));

            if (authors.Count > 0)
            {
                var names = authors;
                items = items.Where(i => db.ItemCredits.Any(c => c.ItemId == i.Id && c.Source == TagSource.Calibre
                                                                 && c.Role == AuthorRole && c.Name != null
                                                                 && names.Contains(c.Name)));
            }

            if (series.Count > 0)
            {
                var names = series;
                items = items.Where(i => db.BookDetails.Any(b => b.ItemId == i.Id && b.SeriesName != null
                                                                 && names.Contains(b.SeriesName)));
            }

            if (publishers.Count > 0)
            {
                var names = publishers;
                items = items.Where(i => db.BookDetails.Any(b => b.ItemId == i.Id && b.Publisher != null
                                                                 && names.Contains(b.Publisher)));
            }

            if (decades.Count > 0)
            {
                var ds = decades;
                items = items.Where(i => i.ResolvedYear != null && ds.Contains(i.ResolvedYear.Value / 10 * 10));
            }

            foreach (var (category, value) in tags)
            {
                // Captured per iteration on purpose: each selected tag ANDs, so each becomes its own EXISTS.
                var cat = category;
                var val = value;
                items = cat == null
                    ? items.Where(i => db.ItemTags.Any(t => t.ItemId == i.Id && t.Value == val))
                    : items.Where(i => db.ItemTags.Any(t => t.ItemId == i.Id && t.Category == cat && t.Value == val));
            }

            return items;
        }

        // ── small helpers (moved verbatim from NovelsController, which now calls them here) ──

        private static string Part(List<(string? Category, string Value)> parts) =>
            string.Join(",", parts.Select(p => p.Category == null ? p.Value : $"{p.Category}:{p.Value}"));

        private static List<string> Csv(string? s) =>
            string.IsNullOrWhiteSpace(s)
                ? []
                : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        /// <summary>"1990s" or "1990" → 1990. Anything else is dropped rather than guessed at.</summary>
        private static List<int> Decades(string? decade) => Csv(decade)
            .Select(d => d.TrimEnd('s', 'S'))
            .Select(d => int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v / 10 * 10 : (int?)null)
            .Where(v => v != null).Select(v => v!.Value).Distinct().ToList();

        /// <summary>
        /// <c>?tag=genre:dystopian</c> pins the category; a bare <c>?tag=dystopian</c> matches any category.
        /// </summary>
        private static List<(string? Category, string Value)> Tags(string? tag) => Csv(tag)
            .Select(t =>
            {
                var i = t.IndexOf(':');
                return i <= 0 || i == t.Length - 1
                    ? ((string?)null, t)
                    : (t[..i].Trim(), t[(i + 1)..].Trim());
            })
            .Where(t => t.Item2.Length > 0).ToList();
    }

    /// <summary>
    /// The novels filter as it rides the BROWSE endpoints, under a <c>book.</c> prefix
    /// (<c>?book.author=Asimov&amp;book.decade=1950</c>).
    ///
    /// <para>Prefixed because <c>/browse/*</c> already binds <c>author</c>, <c>tag</c> and <c>exTag</c> for
    /// <see cref="ExactFilters"/> — the comic rail's language, with different matching. Two filters that mean
    /// nearly the same thing must not share a spelling, or a comic browse would silently acquire a book
    /// filter. It is applied for <see cref="ItemKind.Book"/> only, which makes that impossible either way.</para>
    /// </summary>
    public sealed class NovelFilterQuery
    {
        public string? Author { get; set; }
        public string? Series { get; set; }
        public string? Publisher { get; set; }
        public string? Decade { get; set; }
        public string? Tag { get; set; }
        public string? ExcludeTag { get; set; }
        public int? MinRating { get; set; }
        public bool Unknown { get; set; }

        public NovelFilters ToFilters() =>
            NovelFilters.From(Author, Series, Publisher, Decade, Tag, ExcludeTag, MinRating, Unknown);
    }
}
