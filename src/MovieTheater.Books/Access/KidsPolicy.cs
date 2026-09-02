using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Access
{
    /// <summary>One kid-clear series as the kids surfaces need it.</summary>
    public sealed record KidSeries(int Id, string Name, int? Rating, int? YearStart, int? YearEnd);

    /// <summary>
    /// <b>The kids allow-list.</b> The one place that answers "is this kid content", shared by
    /// <c>/explore/kids</c> and <c>/kids/…</c> so the landing and the browse can never disagree about what a
    /// child may see.
    ///
    /// <para><b>Two gates, in this order.</b> The ADMIN ALLOW-LIST (<c>KidSafeTag</c>, edited in the admin panel)
    /// decides inclusion: a series is eligible when one of its CURRENT AI tags matches an allow-listed
    /// <c>(Category, Tag)</c> pair scoped to its content type — comics are cleared by <c>audience: all-ages</c>,
    /// books by <c>audience: children</c>, because their kid signals genuinely differ. The BLOCKED AUDIENCE FLOOR
    /// then removes anything carrying a contradictory tag, whatever the allow-list said. The floor is
    /// <see cref="MaturityFilter.HardBlockedAbove"/> at ceiling 0 — the same list the maturity gate uses, read
    /// from the same place, so raising or lowering it is one edit.</para>
    ///
    /// <para><b>"teen" is deliberately NOT blocked</b>, mirroring the min-wins rule: the audience vocabulary is
    /// descriptive and multi-valued, so a kid-clear series that ALSO reads as teen (the Bone / Tintin / Asterix
    /// shape — 1,369 series on the real library) is still kid content. Only a two-or-more-level spread
    /// (all-ages AND mature) is a contradiction worth acting on.</para>
    ///
    /// <para><b>The item set is gated at ceiling 0 regardless of who is asking</b> — an admin browsing the kids
    /// view sees exactly what a child sees, which is the only way the view can be checked.</para>
    /// </summary>
    public static class KidsPolicy
    {
        public const string AudienceCategory = MaturityFilter.AudienceCategory;

        /// <summary>The kids ceiling. Not a parameter: a "kids view at ceiling 2" is not a thing.</summary>
        public const int Ceiling = 0;

        /// <summary>Which <c>AppliesTo</c> values clear a given content type. "both" always counts.</summary>
        private static string[] AppliesTo(ItemKind kind) =>
            kind == ItemKind.Book ? ["book", "both"] : ["comic", "both"];

        /// <summary>The allow-listed <c>(Category, Tag)</c> pairs for one content type.</summary>
        public static async Task<HashSet<(string Category, string Tag)>> AllowedPairsAsync(
            BooksDb db, ItemKind kind, CancellationToken ct = default)
        {
            var scopes = AppliesTo(kind);
            var rows = await db.KidSafeTags.AsNoTracking()
                .Where(t => t.AppliesTo != null && scopes.Contains(t.AppliesTo))
                .Select(t => new { t.Category, t.Tag })
                .ToListAsync(ct);
            return rows.Select(r => (r.Category, r.Tag)).ToHashSet();
        }

        /// <summary>
        /// Every kid-clear SERIES, keyed by id. Two indexed reads and an in-memory pair match: the allow-list is
        /// a handful of rows, so the tag query is narrowed by its categories and values — which is exactly the
        /// <c>SeriesTag (Category, Value, SeriesId)</c> index — and the exact pairing is settled afterwards
        /// rather than as a string-concatenated IN list.
        /// </summary>
        public static async Task<Dictionary<int, KidSeries>> KidSeriesAsync(
            BooksDb db, ItemKind kind, CancellationToken ct = default)
        {
            var allowed = await AllowedPairsAsync(db, kind, ct);
            if (allowed.Count == 0) return new Dictionary<int, KidSeries>();

            var categories = allowed.Select(a => a.Category).Distinct().ToList();
            var values = allowed.Select(a => a.Tag).Distinct().ToList();
            var candidates = (await db.SeriesTags.AsNoTracking()
                    .Where(t => t.Source == TagSource.AI && categories.Contains(t.Category) && values.Contains(t.Value))
                    .Select(t => new { t.SeriesId, t.Category, t.Value })
                    .ToListAsync(ct))
                .Where(t => allowed.Contains((t.Category, t.Value)))
                .Select(t => t.SeriesId)
                .Distinct()
                .ToList();
            if (candidates.Count == 0) return new Dictionary<int, KidSeries>();

            var blockedValues = MaturityFilter.HardBlockedAbove(Ceiling);
            var blocked = (await db.SeriesTags.AsNoTracking()
                    .Where(t => t.Source == TagSource.AI && t.Category == AudienceCategory && blockedValues.Contains(t.Value))
                    .Select(t => t.SeriesId).ToListAsync(ct))
                .ToHashSet();

            var ids = candidates.Where(id => !blocked.Contains(id)).ToList();
            if (ids.Count == 0) return new Dictionary<int, KidSeries>();

            var rows = await db.Series.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new KidSeries(s.Id, s.DisplayNameOverride ?? s.Name ?? "", s.ResolvedRating, s.YearStart, s.YearEnd))
                .ToListAsync(ct);
            return rows.ToDictionary(s => s.Id);
        }

        /// <summary>
        /// The items of the given kid-clear series, gated at ceiling 0 and with shadow duplicates removed.
        /// An <see cref="IQueryable{T}"/> so a caller composes its own paging onto it.
        /// </summary>
        public static IQueryable<Item> KidItems(BooksDb db, ItemKind kind, IReadOnlyCollection<int> seriesIds)
        {
            var ids = seriesIds.ToList();
            return db.Items.AsNoTracking()
                .Where(i => i.Kind == kind && i.SeriesId != null && ids.Contains(i.SeriesId.Value))
                .ExcludeHidden()
                .ApplyMaturity(db, Ceiling);
        }

        /// <summary>
        /// Kid-clear BOOKS. A book carries its own tags (<c>ItemTag</c>) rather than inheriting a series', so it
        /// is matched item-side; the maturity floor is the same <see cref="MaturityFilter"/> gate at ceiling 0,
        /// which for a book means its current insight's maturity must be 0.
        /// </summary>
        public static async Task<List<int>> KidBookIdsAsync(BooksDb db, CancellationToken ct = default)
        {
            var allowed = await AllowedPairsAsync(db, ItemKind.Book, ct);
            if (allowed.Count == 0) return [];

            var categories = allowed.Select(a => a.Category).Distinct().ToList();
            var values = allowed.Select(a => a.Tag).Distinct().ToList();
            // Source = AI, exactly like the series path: the allow-list is a vocabulary of INSIGHT tags, and a
            // Calibre subject or an External fold that happens to spell "children" must not clear a book.
            var tagged = (await db.ItemTags.AsNoTracking()
                    .Where(t => t.Source == TagSource.AI && categories.Contains(t.Category) && values.Contains(t.Value))
                    .Select(t => new { t.ItemId, t.Category, t.Value })
                    .ToListAsync(ct))
                .Where(t => allowed.Contains((t.Category, t.Value)))
                .Select(t => t.ItemId).Distinct().ToList();
            if (tagged.Count == 0) return [];

            var blockedValues = MaturityFilter.HardBlockedAbove(Ceiling);
            var blocked = (await db.ItemTags.AsNoTracking()
                    .Where(t => t.Source == TagSource.AI && t.Category == AudienceCategory && blockedValues.Contains(t.Value))
                    .Select(t => t.ItemId).ToListAsync(ct))
                .ToHashSet();

            var ids = tagged.Where(id => !blocked.Contains(id)).ToList();
            return await db.Items.AsNoTracking()
                .Where(i => i.Kind == ItemKind.Book && ids.Contains(i.Id))
                .ExcludeHidden().ApplyMaturity(db, Ceiling)
                .OrderBy(i => i.ResolvedTitle).ThenBy(i => i.Id)
                .Select(i => i.Id)
                .ToListAsync(ct);
        }
    }
}
