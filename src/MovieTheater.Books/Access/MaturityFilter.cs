using MovieTheater.Books.Db;

namespace MovieTheater.Books.Access
{
    /// <summary>
    /// The query half of the maturity gate, ported from the standalone site's <c>ComicMaturityFilter</c> onto v2.
    /// The ceiling itself comes from <see cref="Identity.BooksIdentity.CeilingFor"/>; only the classification rules
    /// and the <see cref="IQueryable{T}"/> filter live here so every surface (browse, item-by-id, OPDS) enforces
    /// the same gate.
    ///
    /// <para><b>Comics.</b> An item has no maturity field of its own (the embedded <c>AgeRating</c> is ~99.6% empty),
    /// so its maturity comes from its SERIES' AI <c>audience</c> tags — <c>SeriesTag</c> rows with
    /// <c>Category = "audience"</c> and <c>Source = AI</c>, written by the tag fold from the series' CURRENT
    /// insight. all-ages = 0, teen = 1, mature / mature-readers = 2, adult = 3.</para>
    ///
    /// <para><b>MIN-WINS, not max-wins.</b> The audience vocabulary is descriptive and multi-valued — it answers
    /// "who is this for", and a series may legitimately carry several values. A series' maturity is therefore the
    /// LOWEST level it carries. Reading it as a ceiling instead (any higher tag disqualifies) excluded 1,369 of the
    /// 2,062 all-ages series from the kid tier on the real data. The one exception is a spread of two or more
    /// levels (all-ages + mature): that is a contradiction, not descriptive breadth, and resolves to the cautious
    /// reading — see <see cref="HardBlockedAbove"/>.</para>
    ///
    /// <para><b>Books.</b> A book carries its own maturity on its current <c>Insight</c> row
    /// (<c>SubjectKind = Item, IsCurrent = 1</c>). Fail-safe: no current row, or a current row without a maturity,
    /// means hidden below ceiling 3.</para>
    /// </summary>
    public static class MaturityFilter
    {
        public const string AudienceCategory = "audience";

        // Canonical audience tags by maturity level. Level 0 (all-ages) is the kid bucket. "mature-readers" is a
        // stray variant in the data, folded in with "mature". There is no Level1 constant: "teen" is never hard
        // blocked now (min-wins) and the allow lists spell it out inline.
        private const string AllAges = "all-ages";
        private static readonly string[] Level2 = { "mature", "mature-readers" };
        private static readonly string[] Level3 = { "adult" };

        /// <summary>Audience tags allowed at-or-below the ceiling (used to REQUIRE a known classification).</summary>
        public static string[] AllowedAtOrBelow(int ceiling) => ceiling switch
        {
            0 => new[] { AllAges },
            1 => new[] { AllAges, "teen" },
            2 => new[] { AllAges, "teen", "mature", "mature-readers" },
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// Audience tags that disqualify a series outright even when it also carries a tag at-or-below the ceiling.
        /// Only tags TWO OR MORE levels above the ceiling qualify: one level of overlap is normal descriptive
        /// spread (all-ages + teen), but all-ages + mature is self-contradictory and takes the cautious reading.
        /// </summary>
        public static string[] HardBlockedAbove(int ceiling) => ceiling switch
        {
            0 => Level2.Concat(Level3).ToArray(),   // mature / adult — NOT teen (that is the overlap case)
            1 => Level3,                            // adult
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// Apply the maturity gate to an item query for the given ceiling. A NO-OP at ceiling 3, so unrestricted
        /// accounts (the default, and every admin) keep seeing the whole library and pay nothing for the gate.
        /// </summary>
        public static IQueryable<Item> ApplyMaturity(this IQueryable<Item> items, BooksDb db, int ceiling)
        {
            if (ceiling >= 3) return items;
            var allowed = AllowedAtOrBelow(ceiling);
            var blocked = HardBlockedAbove(ceiling);
            return items.Where(i =>
                i.Kind == ItemKind.Book
                    ? db.Insights.Any(n => n.SubjectKind == SubjectKind.Item && n.SubjectId == i.Id
                                           && n.IsCurrent && n.Maturity != null && n.Maturity <= ceiling)
                    : i.SeriesId != null
                      && db.SeriesTags.Any(t => t.SeriesId == i.SeriesId && t.Category == AudienceCategory
                                                && t.Source == TagSource.AI && allowed.Contains(t.Value))
                      && !db.SeriesTags.Any(t => t.SeriesId == i.SeriesId && t.Category == AudienceCategory
                                                 && t.Source == TagSource.AI && blocked.Contains(t.Value)));
        }
    }
}
