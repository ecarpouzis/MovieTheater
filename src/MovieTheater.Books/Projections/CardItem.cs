using System.Globalization;
using MovieTheater.Books.Db;
using MovieTheater.Books.Media;

namespace MovieTheater.Books.Projections
{
    /// <summary>
    /// One badge on a card. <c>Tone</c> maps onto the site's chip tokens; it is a hint, not a colour.
    /// </summary>
    public sealed record CardBadge(string Label, string? Tone = null, string? Title = null);

    /// <summary>
    /// <b>The site-wide card.</b> This is the C# side of <c>src/ui/src/catalog/types.ts</c>'s <c>CardItem</c> —
    /// the contract EVERY section's Explore endpoint answers in, not a Books shape that Books happens to use
    /// first. A card is "an image with an identity and a few labels"; anything the section's own modal needs
    /// rides untouched in <see cref="Raw"/>.
    ///
    /// <para>The fields are the TypeScript ones, spelled the way ASP.NET serializes them (camelCase), so the
    /// SPA's contract file and this record can be diffed by eye. <c>Kind</c> is a string — <c>"comic"</c>,
    /// <c>"book"</c>, <c>"series"</c> — because ids collide across kinds and <see cref="Key"/> is what a list
    /// keys on.</para>
    /// </summary>
    public sealed record CardItem(
        string Kind,
        int Id,
        string Key,
        string Title,
        string? Subtitle,
        string? Label,
        int? Year,
        double Aspect,
        string? ImageUrl,
        string? ImageThumbUrl,
        int? Hue,
        int? Rating,
        List<CardBadge>? Badges,
        string? GroupKey,
        string? SortKey,
        object? Raw);

    /// <summary>Where a rail's "more" leads. Relative to the Books API root; the SPA's source maps it to a route.</summary>
    public sealed record ExploreMore(string Href);

    /// <summary>
    /// One rail. <c>Kind</c> is the LAYOUT (<c>strip</c> | <c>wall</c> | <c>grid</c>) — deliberately the same
    /// word the card uses for its entity space, because that is what the TypeScript contract says and a rename
    /// here would be a silent drift.
    /// </summary>
    public sealed record ExploreRail(string Key, string Title, string Kind, List<CardItem> Items, ExploreMore? More = null);

    /// <summary>
    /// <b>The envelope every section's Explore endpoint returns.</b> Books is its first server; Movies, Music,
    /// Arcade, Photos and Boardgames answer the same shape (plan §9.4). <c>Seed</c> is echoed so a "re-roll"
    /// can ask for a different one.
    /// </summary>
    public sealed record ExploreResponse(List<CardItem> Spotlight, List<ExploreRail> Rails, int Seed);

    /// <summary>
    /// Turns the vertical's rows into cards. Kept beside the contract rather than inside a controller because
    /// Explore, the kids shelves and (later) OPDS all project the same way, and a second copy of the aspect
    /// default or the key format is how two surfaces start disagreeing about what a card is.
    /// </summary>
    public static class CardFactory
    {
        /// <summary>The aspect a card falls back to when the cover's real dimensions are unknown (the TS default).</summary>
        public const double DefaultAspect = 0.66;

        public static string Key(string kind, int id) => $"{kind}:{id}";

        /// <summary>
        /// One item as a card. <paramref name="sortKey"/> is the value the caller ordered by — a rail sorted by
        /// rating passes the rating, a rail sorted by arrival passes the timestamp — so a view can render a
        /// "you are here" without knowing the query.
        /// </summary>
        public static CardItem FromItem(ItemSummary s, MediaUrls media, string? sortKey = null,
            IEnumerable<CardBadge>? extraBadges = null)
        {
            var thumb = media.Thumb(s.Id);
            var badges = new List<CardBadge>();
            if (s.Rating is int rating)
                badges.Add(new CardBadge(rating.ToString(CultureInfo.InvariantCulture), "rating", "Library rating"));
            if (extraBadges != null) badges.AddRange(extraBadges);

            return new CardItem(
                Kind: s.Kind,
                Id: s.Id,
                Key: Key(s.Kind, s.Id),
                Title: s.Title ?? s.FileName,
                // The series is the card's second line for a comic; for a book it is the publisher, because a
                // book's "series" is usually absent and its publisher is the fact a shelf is browsed by.
                Subtitle: s.Kind == "book" ? s.Publisher ?? s.Series : s.Series,
                Label: s.Year?.ToString(CultureInfo.InvariantCulture),
                Year: s.Year,
                Aspect: s.CoverAspect ?? DefaultAspect,
                // There is no second rendition: the generated 720×440 WebP IS the cover the site shows, so the
                // full and thumb URLs are the same file. The field stays because the contract has it.
                ImageUrl: thumb,
                ImageThumbUrl: thumb,
                Hue: null,
                Rating: s.Rating,
                Badges: badges.Count == 0 ? null : badges,
                GroupKey: s.SeriesId?.ToString(CultureInfo.InvariantCulture),
                SortKey: sortKey ?? s.Title,
                Raw: s);
        }

        /// <summary>
        /// A SERIES as a card, drawn with its representative issue's cover. <c>kind = "series"</c> and
        /// <c>key = "series:{id}"</c> keep it distinct from the issue whose picture it borrows — the two have
        /// colliding ids and open different modals.
        /// </summary>
        public static CardItem FromSeries(int seriesId, string title, string? subtitle, int? rating, int issueCount,
            int? yearStart, int? yearEnd, ItemSummary? cover, MediaUrls media, string? note = null, string? sortKey = null)
        {
            var thumb = cover == null ? null : media.Thumb(cover.Id);
            var badges = new List<CardBadge>();
            if (rating is int r) badges.Add(new CardBadge(r.ToString(CultureInfo.InvariantCulture), "rating", note ?? "Library rating"));
            if (issueCount > 0)
                badges.Add(new CardBadge(issueCount == 1 ? "1 issue" : $"{issueCount} issues", "neutral", "Issues held"));

            var years = yearStart == null ? null
                : yearEnd == null || yearEnd == yearStart ? yearStart.Value.ToString(CultureInfo.InvariantCulture)
                : $"{yearStart}–{yearEnd}";

            return new CardItem(
                Kind: "series",
                Id: seriesId,
                Key: Key("series", seriesId),
                Title: title,
                Subtitle: subtitle,
                Label: years,
                Year: yearStart,
                Aspect: cover?.CoverAspect ?? DefaultAspect,
                ImageUrl: thumb,
                ImageThumbUrl: thumb,
                Hue: null,
                Rating: rating,
                Badges: badges.Count == 0 ? null : badges,
                GroupKey: seriesId.ToString(CultureInfo.InvariantCulture),
                SortKey: sortKey ?? title,
                // A series card carries BOTH halves: the series facts a header shows and the cover issue's own
                // row, so clicking the picture can open the issue without a second fetch.
                Raw: new { seriesId, name = title, rating, note, issueCount, yearStart, yearEnd, cover });
        }
    }
}
