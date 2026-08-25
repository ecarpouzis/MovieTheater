using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Projections
{
    // ── the provenance blocks ─────────────────────────────────────────────────────────────────────────────
    // Each one is a NAMED LEG. The modal prints them side by side with their source label ("ComicVine says…",
    // "the library rating blends…"), which is the whole reason they stay separate instead of being merged into
    // one flattened bag: a reader is meant to see WHERE a fact came from, and a disagreement between two legs is
    // information, not a bug to hide.

    /// <summary>The raw ComicInfo.xml block as it was read from the archive. Never rewritten by the site.</summary>
    public sealed record EmbeddedBlock(
        string? Series, string? Number, string? AltSeries, string? AltNumber, int? Volume, string? Title,
        string? Summary, string? Publisher, string? Imprint, string? Genre, string? Tags, string? Characters,
        string? Teams, string? Locations, string? StoryArc, string? Web, string? Language, string? Format,
        string? PublicationDate, string? Writers, string? Pencillers, string? Inker, string? Colorist,
        string? Letterer, string? CoverArtist, string? Editor, bool? BlackAndWhite, string? Manga,
        int? Rating, string? Identifier, string? Notes, int? Count, string? AgeRating);

    /// <summary>The parse pipeline's reading of the file name and folder — the RESOLUTION INPUT, shown so a
    /// wrong series or issue number can be traced to the string it was parsed from.</summary>
    public sealed record ParsedBlock(
        string? SeriesKey, string? IssueNo, int? Year, int? VolumeNo, string? Publisher, ComicFormat Format,
        string? FormatRaw, bool IsCollection, string? EventName, string? IssueTitle, Confidence Confidence,
        ParseSource SeriesSource, ParseSource IssueSource, ParseSource YearSource, ParseSource PublisherSource,
        string? FolderSeries, int? FolderYear, string? ParseNotes);

    public sealed record BookBlock(string? Isbn, string? SeriesName, double? SeriesIndex, string? Publisher,
        string? PublishedOn, string? Language, string? Description);

    public sealed record CvVolumeBlock(int Id, string? Name, int? StartYear, string? PublisherName,
        int? CountOfIssues, string? Deck, string? Description, string? ImageUrl, string? SiteDetailUrl);

    public sealed record CvIssueBlock(int Id, int? VolumeId, string? Name, string? IssueNumber, string? CoverDate,
        string? StoreDate, string? Deck, string? Description, string? ImageUrl, string? SiteDetailUrl);

    public sealed record LocgBlock(int LocgComicId, int? LocgSeriesId, string? SeriesName, string? Title,
        string? IssueNumber, string? Format, string? CoverDate, int? PageCount, string? Description,
        double? CommunityRating, int? RatingCount, bool IsKey, string? KeyType, string? CoverPrice,
        string? CoverUrl, LinkQuality Quality);

    public sealed record MuBlock(long Id, string? Title, int? Year, string? Type, string? Status, bool Completed,
        string? Description, double? BayesianRating, string? Url);

    public sealed record ExternalBlock(int Id, string? Provider, string? Title, string? Authors, string? Publisher,
        int? FirstPublishYear, string? Description, string? CoverImageUrl, string? Isbn, string? InfoUrl);

    /// <summary>The current model-written insight for one subject: prose, score, attribution, tags.</summary>
    public sealed record InsightBlock(string ModelId, Confidence Confidence, bool Recognized, int? Rating,
        string? Synopsis, string? Author, string? Artist, int? YearBegin, int? YearEnd, int? Maturity,
        DateTime? GeneratedAt, List<string> Tags);

    public sealed record ReadingOrderBlock(int? SeriesId, int? ReadTier, double? ReadNumber, string? ReadDate,
        DatePrecision ReadDatePrecision, int? ReadIndex, int ReadCount, ReadingOrderSource Source, Confidence Confidence);

    public sealed record CollectionBlock(CollectionLevel Level, TrackRole TrackRole, int? SpanStart, int? SpanEnd,
        int ContainsCount, int? ParentItemId, SpanSource SpanSource, string? SpanLabel);

    public sealed record EditionSpanBlock(EditionSource Source, int? SeriesId, double? IssueStart, double? IssueEnd,
        string? EditionTitle, string? ProviderRef, bool Contiguous, double? Confidence, string? Note);

    /// <summary>Credits and tags carry their SOURCE, and the client groups by it — the same person arriving from
    /// two legs is one row per leg, not a silently deduplicated single row.</summary>
    public sealed record CreditRow(TagSource Source, int Ordinal, string? Role, string? Name);
    public sealed record TagRow(TagSource Source, string Category, string Value);
    public sealed record ProviderLinkRow(Provider Provider, string? ProviderKey, LinkStatus Status,
        LinkQuality Quality, string? Method, double? Confidence, bool Applied);

    /// <summary>The series facts the modal shows above the issue's own.</summary>
    public sealed record SeriesBlock(int Id, string? Name, string? DisplayNameOverride, string? CanonicalKey,
        int IssueCount, int? YearStart, int? YearEnd, bool IsOngoing, string? Franchise, int? ResolvedRating,
        SynopsisSource ResolvedSynopsisSource);

    /// <summary>Health and cover facts that jobs (never requests) write.</summary>
    public sealed record StateBlock(bool IsBroken, string? BrokenReason, DateTime? BrokenCheckedAt,
        string? ThumbnailError, DateTime? ThumbnailCheckedAt, int? CoverWidth, int? CoverHeight,
        string? ExclusionReason, DateTime? ExcludedAt);

    /// <summary>
    /// <b>The item modal's whole payload.</b> The browse projection deliberately joins Item + Series only, so
    /// every raw provider fact, every insight, every credit and tag row lives HERE — a by-id read that can
    /// afford a dozen small indexed queries because it happens once per opened card, not once per grid page.
    ///
    /// <para><see cref="Summary"/> is the SAME <see cref="ItemSummary"/> the list surfaces sent, so the client
    /// never has to reconcile two shapes of the same item; everything else is additive provenance.</para>
    /// </summary>
    public sealed record ItemDetail(
        ItemSummary Summary,
        string RelativePath,
        string? FolderName,
        string? FolderPath,
        int? TopFolderId,
        string? TopFolderName,
        bool HasThumbnail,
        StateBlock? State,
        EmbeddedBlock? Embedded,
        ParsedBlock? Parsed,
        BookBlock? Book,
        SeriesBlock? Series,
        InsightBlock? Insight,
        InsightBlock? SeriesInsight,
        CvVolumeBlock? CvVolume,
        CvIssueBlock? CvIssue,
        LocgBlock? Locg,
        MuBlock? Mu,
        ExternalBlock? External,
        ReadingOrderBlock? ReadingOrder,
        CollectionBlock? Collection,
        List<EditionSpanBlock> EditionSpans,
        List<CreditRow> Credits,
        List<TagRow> Tags,
        List<TagRow> SeriesTags,
        List<ProviderLinkRow> ProviderLinks,
        string? ThumbUrl,
        string? DownloadUrl,
        string? PagesUrlTemplate);

    /// <summary>
    /// Assembles an <see cref="ItemDetail"/>. Kept out of the controller because the media plane's manifest and
    /// (later) OPDS need the same reads, and because the ORDER of the reads is the thing worth reviewing: every
    /// one is a point lookup or a small indexed range, none is a scan.
    /// </summary>
    public static class ItemDetailBuilder
    {
        public static async Task<ItemDetail> BuildAsync(
            BooksDb db, Item item, Func<long, string?>? thumbUrl = null, Func<long, string?>? downloadUrl = null,
            Func<long, string?>? pagesUrlTemplate = null, bool hasThumbnail = false, CancellationToken ct = default)
        {
            var summary = await db.Items.AsNoTracking().Where(i => i.Id == item.Id)
                .Select(ItemSummary.Project).FirstAsync(ct);

            var folder = await db.Folders.AsNoTracking().Where(f => f.Id == item.FolderId)
                .Select(f => new { f.Name, f.Path }).FirstOrDefaultAsync(ct);
            var topFolderName = item.TopFolderId == null ? null
                : await db.Folders.AsNoTracking().Where(f => f.Id == item.TopFolderId).Select(f => f.Name).FirstOrDefaultAsync(ct);

            // The path is shown as a breadcrumb RELATIVE to its library root — the absolute path is a share name
            // and a directory layout, which is nothing a reader needs and something a screenshot should not carry.
            var roots = await db.LibraryRoots.AsNoTracking().Select(r => r.Path).ToListAsync(ct);
            var relative = ToRelativePath(item.Path, roots);

            var state = await db.ItemStates.AsNoTracking().Where(s => s.ItemId == item.Id)
                .Select(s => new StateBlock(s.IsBroken, s.BrokenReason, s.BrokenCheckedAt, s.ThumbnailError,
                    s.ThumbnailCheckedAt, s.CoverWidth, s.CoverHeight, s.ExclusionReason, s.ExcludedAt))
                .FirstOrDefaultAsync(ct);

            var embedded = await db.ComicEmbeddeds.AsNoTracking().Where(e => e.ItemId == item.Id)
                .Select(e => new EmbeddedBlock(e.Series, e.Number, e.AltSeries, e.AltNumber, e.Volume, e.Title,
                    e.Summary, e.Publisher, e.Imprint, e.Genre, e.Tags, e.Characters, e.Teams, e.Locations,
                    e.StoryArc, e.Web, e.Language, e.Format, e.PublicationDate, e.Writers, e.Pencillers, e.Inker,
                    e.Colorist, e.Letterer, e.CoverArtist, e.Editor, e.BlackAndWhite, e.Manga, e.Rating,
                    e.Identifier, e.Notes, e.Count, e.AgeRating))
                .FirstOrDefaultAsync(ct);

            var parsed = await db.ComicDetails.AsNoTracking().Where(c => c.ItemId == item.Id)
                .Select(c => new ParsedBlock(c.ParsedSeriesKey, c.IssueNo, c.Year, c.VolumeNo, c.Publisher, c.Format,
                    c.FormatRaw, c.IsCollection, c.EventName, c.IssueTitle, c.Confidence, c.SeriesSource,
                    c.IssueSource, c.YearSource, c.PublisherSource, c.FolderSeries, c.FolderYear, c.ParseNotes))
                .FirstOrDefaultAsync(ct);

            var book = await db.BookDetails.AsNoTracking().Where(b => b.ItemId == item.Id)
                .Select(b => new BookBlock(b.Isbn, b.SeriesName, b.SeriesIndex, b.Publisher, b.PublishedOn,
                    b.Language, b.Description))
                .FirstOrDefaultAsync(ct);

            SeriesBlock? seriesBlock = null;
            CvVolumeBlock? cvVolume = null;
            MuBlock? mu = null;
            ExternalBlock? external = null;
            InsightBlock? seriesInsight = null;
            List<TagRow> seriesTags = [];

            if (item.SeriesId is int seriesId)
            {
                var s = await db.Series.AsNoTracking().FirstOrDefaultAsync(x => x.Id == seriesId, ct);
                if (s != null)
                {
                    seriesBlock = new SeriesBlock(s.Id, s.Name, s.DisplayNameOverride, s.CanonicalKey, s.IssueCount,
                        s.YearStart, s.YearEnd, s.IsOngoing, s.Franchise, s.ResolvedRating, s.ResolvedSynopsisSource);

                    if (s.CvVolumeId is int volumeId)
                        cvVolume = await db.CvVolumes.AsNoTracking().Where(v => v.Id == volumeId)
                            .Select(v => new CvVolumeBlock(v.Id, v.Name, v.StartYear, v.PublisherName, v.CountOfIssues,
                                v.Deck, v.Description, v.ImageUrl, v.SiteDetailUrl))
                            .FirstOrDefaultAsync(ct);

                    if (s.MuSeriesId is long muId)
                        mu = await db.MuSeries.AsNoTracking().Where(m => m.Id == muId)
                            .Select(m => new MuBlock(m.Id, m.Title, m.Year, m.Type, m.Status, m.Completed,
                                m.Description, m.BayesianRating, m.Url))
                            .FirstOrDefaultAsync(ct);

                    if (s.ExternalWorkId is int extId)
                        external = await db.ExternalWorks.AsNoTracking().Where(w => w.Id == extId)
                            .Select(w => new ExternalBlock(w.Id, w.Provider, w.Title, w.Authors, w.Publisher,
                                w.FirstPublishYear, w.Description, w.CoverImageUrl, w.Isbn, w.InfoUrl))
                            .FirstOrDefaultAsync(ct);
                }

                seriesInsight = await CurrentInsightAsync(db, SubjectKind.Series, seriesId, ct);
                seriesTags = await db.SeriesTags.AsNoTracking().Where(t => t.SeriesId == seriesId)
                    .Select(t => new TagRow(t.Source, t.Category, t.Value)).ToListAsync(ct);
            }

            var insight = await CurrentInsightAsync(db, SubjectKind.Item, item.Id, ct);

            var links = await db.ItemProviderLinks.AsNoTracking().Where(l => l.ItemId == item.Id)
                .Select(l => new { l.Provider, l.ProviderKey, l.Status, l.Quality, l.Method, l.Confidence, l.Applied })
                .ToListAsync(ct);

            // ComicVine's ISSUE row hangs off the item's own link, unlike the volume, which is the series'.
            CvIssueBlock? cvIssue = null;
            var cvLink = links.FirstOrDefault(l => l.Provider == Provider.Cv && l.Status == LinkStatus.Matched);
            if (cvLink != null && int.TryParse(cvLink.ProviderKey, out var cvIssueId))
                cvIssue = await db.CvIssues.AsNoTracking().Where(i => i.Id == cvIssueId)
                    .Select(i => new CvIssueBlock(i.Id, i.VolumeId, i.Name, i.IssueNumber, i.CoverDate, i.StoreDate,
                        i.Deck, i.Description, i.ImageUrl, i.SiteDetailUrl))
                    .FirstOrDefaultAsync(ct);

            // LOCG is shown only for a HIGH or MEDIUM link. A low-quality LOCG match is a guess, and printing a
            // community rating and a cover price from the wrong issue reads as fact — so it is not printed.
            LocgBlock? locg = null;
            var locgLink = links.FirstOrDefault(l => l.Provider == Provider.Locg
                && (l.Quality == LinkQuality.High || l.Quality == LinkQuality.Medium));
            if (locgLink != null && int.TryParse(locgLink.ProviderKey, out var locgId))
                locg = await db.LocgComics.AsNoTracking().Where(l => l.LocgComicId == locgId)
                    .Select(l => new LocgBlock(l.LocgComicId, l.LocgSeriesId, l.SeriesName, l.Title, l.IssueNumber,
                        l.Format, l.CoverDate, l.PageCount, l.Description, l.CommunityRating, l.RatingCount,
                        l.IsKey, l.KeyType, l.CoverPrice, l.CoverUrl, locgLink.Quality))
                    .FirstOrDefaultAsync(ct);

            var readingOrder = await db.ReadingOrderEntries.AsNoTracking().Where(r => r.ItemId == item.Id)
                .Select(r => new ReadingOrderBlock(r.SeriesId, r.ReadTier, r.ReadNumber, r.ReadDate,
                    r.ReadDatePrecision, r.ReadIndex, r.ReadCount, r.Source, r.Confidence))
                .FirstOrDefaultAsync(ct);

            var collection = await db.CollectionNodes.AsNoTracking().Where(n => n.ItemId == item.Id)
                .Select(n => new CollectionBlock(n.Level, n.TrackRole, n.SpanStart, n.SpanEnd, n.ContainsCount,
                    n.ParentItemId, n.SpanSource, n.SpanLabel))
                .FirstOrDefaultAsync(ct);

            var spans = await db.CollectedEditionSpans.AsNoTracking().Where(s => s.ItemId == item.Id)
                .OrderBy(s => s.Source).ThenBy(s => s.IssueStart)
                .Select(s => new EditionSpanBlock(s.Source, s.SeriesId, s.IssueStart, s.IssueEnd, s.EditionTitle,
                    s.ProviderRef, s.Contiguous, s.Confidence, s.Note))
                .ToListAsync(ct);

            var credits = await db.ItemCredits.AsNoTracking().Where(c => c.ItemId == item.Id)
                .OrderBy(c => c.Source).ThenBy(c => c.Ordinal)
                .Select(c => new CreditRow(c.Source, c.Ordinal, c.Role, c.Name)).ToListAsync(ct);

            var tags = await db.ItemTags.AsNoTracking().Where(t => t.ItemId == item.Id)
                .Select(t => new TagRow(t.Source, t.Category, t.Value)).ToListAsync(ct);

            return new ItemDetail(
                summary, relative, folder?.Name, folder?.Path, item.TopFolderId, topFolderName, hasThumbnail,
                state, embedded, parsed, book, seriesBlock, insight, seriesInsight, cvVolume, cvIssue, locg, mu,
                external, readingOrder, collection, spans, credits, tags, seriesTags,
                links.Select(l => new ProviderLinkRow(l.Provider, l.ProviderKey, l.Status, l.Quality, l.Method,
                    l.Confidence, l.Applied)).ToList(),
                thumbUrl?.Invoke(item.Id), downloadUrl?.Invoke(item.Id), pagesUrlTemplate?.Invoke(item.Id));
        }

        /// <summary>
        /// The CURRENT insight only. The table is append-only history — rank, then confidence, then recency
        /// already picked a winner and stamped <c>IsCurrent</c>; re-deriving it here would be a second, drifting
        /// copy of that rule.
        /// </summary>
        private static async Task<InsightBlock?> CurrentInsightAsync(
            BooksDb db, SubjectKind kind, int subjectId, CancellationToken ct)
        {
            var row = await db.Insights.AsNoTracking()
                .Where(n => n.SubjectKind == kind && n.SubjectId == subjectId && n.IsCurrent)
                .Select(n => new { n.Id, n.ModelId, n.Confidence, n.Recognized, n.Rating, n.Synopsis, n.Author,
                    n.Artist, n.YearBegin, n.YearEnd, n.Maturity, n.GeneratedAt })
                .FirstOrDefaultAsync(ct);
            if (row == null) return null;

            var tags = await db.InsightTags.AsNoTracking().Where(t => t.InsightId == row.Id)
                .Select(t => t.Category + ":" + t.Value).ToListAsync(ct);

            return new InsightBlock(row.ModelId, row.Confidence, row.Recognized, row.Rating, row.Synopsis,
                row.Author, row.Artist, row.YearBegin, row.YearEnd, row.Maturity, row.GeneratedAt, tags);
        }

        /// <summary>The path with its library root stripped — longest root first, so a nested root wins.</summary>
        public static string ToRelativePath(string path, IEnumerable<string> roots)
        {
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).OrderByDescending(r => r.Length))
            {
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return "\\" + rel.Replace('/', '\\');
            }
            // Outside every registered root: return the file name alone rather than leaking a full share path.
            return Path.GetFileName(path);
        }
    }
}
