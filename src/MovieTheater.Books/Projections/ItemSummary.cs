using System.Linq.Expressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Projections
{
    /// <summary>
    /// The flat browse row — one item as every list surface sees it (the OData catalog, the grouped bands, the
    /// letter rail's payloads). The v2 successor of the standalone site's <c>ComicSummary</c>.
    ///
    /// <para><b>Two laws shape it.</b> First, <c>$count</c>: the projection is flat scalars only — no collections,
    /// no correlated subqueries — so <c>$count=true</c>, <c>$filter</c> and <c>$skip/$top</c> stay valid over it and
    /// EF never fans a row out. Second, the v2 rule that the browse projection joins <b>Item + Series ONLY</b>:
    /// <c>Item.Resolved*</c> ARE the truth (the resolver already picked the winning leg for the title, series,
    /// publisher, date, rating, creators and tags), so a browse page costs one index range and one 1:1 join. The old
    /// site's ten LEFT JOINs into ComicVine / LOCG / MangaUpdates / GCD / the insight rows are gone from this
    /// path.</para>
    ///
    /// <para><b>Where the rest lives.</b> Raw provider fields — the ComicVine deck and description, the LOCG
    /// community rating and its per-issue blurb, the MangaUpdates description, the embedded ComicInfo block, the
    /// current insight's synopsis, and the provenance labels the modal prints beside each of them — come from the
    /// ITEM DETAIL endpoint (slice 2, <c>/items/{id}</c>), which is a by-id read and can afford the joins. Tags,
    /// credits and ratings are ROWS in v2 (<c>ItemTag</c> / <c>SeriesTag</c> / <c>ItemCredit</c> / <c>Rating</c>);
    /// the facets GROUP BY those tables. <see cref="TagsCsv"/> and <see cref="CreatorsCsv"/> are materialized
    /// display/filter strings only — they exist so an OData <c>$filter</c> can <c>contains()</c> them, never as the
    /// source of a count.</para>
    /// </summary>
    public sealed class ItemSummary
    {
        // ── identity ──
        public int Id { get; set; }
        /// <summary>"comic" or "book" — a string so an OData $filter reads `kind eq 'book'` instead of enum syntax.</summary>
        public string Kind { get; set; } = "comic";
        /// <summary>The resolved display title (the title rule: single-issue series → series name; issue → "{Series}{ Vol N}#{n}"; else the embedded title).</summary>
        public string? Title { get; set; }

        // ── series (the 1:1 Series join — the ONLY join in this projection) ──
        public int? SeriesId { get; set; }
        public string? Series { get; set; }
        public int? SeriesIssueCount { get; set; }
        public int? SeriesYearStart { get; set; }
        public int? SeriesYearEnd { get; set; }
        public bool SeriesIsOngoing { get; set; }
        public string? Franchise { get; set; }
        /// <summary>The series has exactly one issue in the library, so the card collapses issue and series into one entity (no series modal).</summary>
        public bool IsSingleIssueSeries { get; set; }
        /// <summary>Series-level blended rating (<c>Series.ResolvedRating</c>) — the provenance the modal labels "series rating" beside the issue's own.</summary>
        public int? SeriesRatingResolved { get; set; }

        // ── resolved scalars ──
        public string? Publisher { get; set; }
        public int? Year { get; set; }
        public int? Month { get; set; }
        /// <summary>How much of the date is real — the client shows a month only at Month/Day precision.</summary>
        public DatePrecision DatePrecision { get; set; }
        public int? Rating { get; set; }
        /// <summary>Which leg won the synopsis. The TEXT is not projected (v2 copies no synopsis prose); the detail endpoint reads it from the named leg.</summary>
        public SynopsisSource SynopsisSource { get; set; }
        public string? CreatorsCsv { get; set; }
        public string? TagsCsv { get; set; }
        /// <summary>Cover width/height ratio, clamped to [0.35, 1.6] with 0.66 as the default, so covers render at their true shape.</summary>
        public double? CoverAspect { get; set; }

        // ── file identity ──
        public string FileName { get; set; } = "";
        public string? Extension { get; set; }
        public long FileSize { get; set; }
        public int? PageCount { get; set; }
        public DateTime? IndexedAt { get; set; }

        // ── placement ──
        public int FolderId { get; set; }
        /// <summary>The depth-1 folder under a library root — the "collection" the browse groups and facets key on.</summary>
        public int? TopFolderId { get; set; }
        /// <summary>Shadow duplicate. Hidden everywhere except the Directory drill, which dims it.</summary>
        public bool IsExcluded { get; set; }

        /// <summary>
        /// The ONE projection, shared by the OData catalog and the grouped bands so the two can never drift.
        /// EF renders it as a single LEFT JOIN to Series over the browse index range — nothing else.
        /// </summary>
        public static readonly Expression<Func<Item, ItemSummary>> Project = i => new ItemSummary
        {
            Id = i.Id,
            Kind = i.Kind == ItemKind.Book ? "book" : "comic",
            Title = i.ResolvedTitle,

            SeriesId = i.SeriesId,
            Series = i.ResolvedSeries,
            SeriesIssueCount = i.Series != null ? (int?)i.Series.IssueCount : null,
            SeriesYearStart = i.Series != null ? i.Series.YearStart : null,
            SeriesYearEnd = i.Series != null ? i.Series.YearEnd : null,
            SeriesIsOngoing = i.Series != null && i.Series.IsOngoing,
            Franchise = i.Series != null ? i.Series.Franchise : null,
            IsSingleIssueSeries = i.Series != null && i.Series.IssueCount == 1,
            SeriesRatingResolved = i.Series != null ? i.Series.ResolvedRating : null,

            Publisher = i.ResolvedPublisher,
            Year = i.ResolvedYear,
            Month = i.ResolvedMonth,
            DatePrecision = i.ResolvedDatePrecision,
            Rating = i.ResolvedRating,
            SynopsisSource = i.ResolvedSynopsisSource,
            CreatorsCsv = i.ResolvedCreatorsCsv,
            TagsCsv = i.ResolvedTagsCsv,
            CoverAspect = i.CoverAspect,

            FileName = i.FileName,
            Extension = i.Extension,
            FileSize = i.FileSize,
            PageCount = i.PageCount,
            IndexedAt = i.IndexedAt,

            FolderId = i.FolderId,
            TopFolderId = i.TopFolderId,
            IsExcluded = i.IsExcluded,
        };
    }
}
