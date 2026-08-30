using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// Materializes Item.Resolved* / CoverAspect and Series.Resolved* — the standalone site's <c>transformComic</c>
    /// cascade (title rule, series/publisher tiers, date, creators, synopsis pointer, tags, aspect clamp), run
    /// ONCE server-side over the v2 rows so the browse projection joins Item + Series and nothing else.
    /// </summary>
    public static class ItemResolver
    {
        public const double DefaultAspect = 0.66, MinAspect = 0.35, MaxAspect = 1.6;
        private static readonly Regex Cvdb = new(@"^CVDB\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex YearMonth = new(@"^(\d{4})-(\d{2})", RegexOptions.Compiled);
        private static readonly Regex YearOnly = new(@"^(\d{4})", RegexOptions.Compiled);

        public static double ClampAspect(int? w, int? h)
        {
            var raw = w > 0 && h > 0 ? (double)w.Value / h.Value : DefaultAspect;
            return Math.Max(MinAspect, Math.Min(MaxAspect, raw));
        }

        /// <summary>resolveComicDate: readDate (year always; month only at Day/Month precision) → publication date → parsed year → external year.</summary>
        public static (int? year, int? month, DatePrecision precision) ResolveDate(string? readDate, DatePrecision readPrecision, string? publicationDate, int? parsedYear, int? extYear)
        {
            int year = 0, month = 0;
            var fromReadMonth = false;
            if (readDate != null && YearMonth.Match(readDate) is { Success: true } rd)
            {
                year = int.Parse(rd.Groups[1].Value, CultureInfo.InvariantCulture);
                if (readPrecision is DatePrecision.Day or DatePrecision.Month) { month = int.Parse(rd.Groups[2].Value, CultureInfo.InvariantCulture); fromReadMonth = true; }
            }
            if (publicationDate != null)
            {
                var pym = YearMonth.Match(publicationDate);
                if (pym.Success)
                {
                    var py = int.Parse(pym.Groups[1].Value, CultureInfo.InvariantCulture);
                    var pm = int.Parse(pym.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (year == 0) year = py;
                    if (month == 0 && py == year) month = pm;
                }
                else
                {
                    var py = YearOnly.Match(publicationDate);
                    if (py.Success && year == 0) year = int.Parse(py.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            }
            if (year == 0 && parsedYear > 0) year = parsedYear.Value;
            if (year == 0 && extYear > 0) year = extYear.Value;
            if (month is < 1 or > 12) month = 0;
            var precision = year == 0 ? DatePrecision.None : month == 0 ? DatePrecision.Year : fromReadMonth && readPrecision == DatePrecision.Day ? DatePrecision.Day : DatePrecision.Month;
            return (year == 0 ? null : year, month == 0 ? null : month, precision);
        }

        /// <summary>The display-title rule: single-issue series read as the series; numbered non-collections as "Series[ Vol N] #n"; else the item's own title.</summary>
        public static string? ResolveTitle(string? itemTitle, string? series, bool isSingleIssueSeries, bool isCollection, double? readNumber, string? parsedIssueNo, int? volumeNo)
        {
            if (isSingleIssueSeries && !string.IsNullOrEmpty(series)) return series;
            var parsedNum = parsedIssueNo != null && !string.Equals(parsedIssueNo.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? parsedIssueNo.Trim() : null;
            var issueNum = FormatNumber(readNumber) ?? parsedNum;
            if (!isCollection && !string.IsNullOrEmpty(issueNum) && !string.IsNullOrEmpty(series))
                return series + (volumeNo > 1 ? $" Vol {volumeNo}" : "") + " #" + issueNum;
            return itemTitle;
        }

        public static string? FormatNumber(double? n)
        {
            if (n == null || double.IsNaN(n.Value) || double.IsInfinity(n.Value)) return null;
            if (Math.Abs(n.Value - Math.Round(n.Value)) < 1e-9) return ((long)Math.Round(n.Value)).ToString(CultureInfo.InvariantCulture);
            return Math.Round(n.Value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>resolveCreators: the first source that yields any names wins.</summary>
        public static List<string> FirstNonEmpty(params string?[] sources)
        {
            foreach (var s in sources)
            {
                var names = Transforms.SplitNames(s);
                if (names.Count > 0) return names;
            }
            return new List<string>();
        }

        private const string ItemSql = @"
SELECT i.Id, i.Kind, i.Title, i.SeriesId, i.PublisherId,
       cd.ParsedSeriesKey, cd.IssueNo, cd.Year AS ParsedYear, cd.VolumeNo, cd.Publisher AS ParsedPublisher, cd.IsCollection,
       ce.Summary, ce.Publisher AS EmbPublisher, ce.PublicationDate, ce.Writers, ce.Pencillers, ce.CoverArtist, ce.Series AS EmbSeries,
       bd.SeriesName AS BookSeries, bd.Publisher AS BookPublisher, bd.PublishedOn, bd.Description AS BookDescription,
       s.Name AS SeriesName, s.DisplayNameOverride, s.IssueCount, s.CvVolumeId, s.ExternalWorkId, s.MuSeriesId,
       cv.Deck AS CvDeck, cv.Description AS CvDescription, cv.PublisherName AS CvPublisher,
       ew.Description AS ExtDescription, ew.Authors AS ExtAuthors, ew.FirstPublishYear AS ExtYear,
       mu.Description AS MuDescription,
       lc.Description AS LocgDescription,
       si.Synopsis AS SeriesAiSynopsis, si.Rating AS SeriesAiRating, si.Author AS AiAuthor, si.Artist AS AiArtist,
       bi.Synopsis AS BookAiSynopsis, bi.Rating AS BookAiRating, bi.Author AS BookAiAuthor,
       ro.ReadNumber, ro.ReadDate, ro.ReadDatePrecision,
       cn.Level AS CollectionLevel,
       st.CoverWidth, st.CoverHeight,
       p.Name AS PublisherName,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 0 AND r.TargetId = i.Id AND r.Source = 5) AS OverrideRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 0 AND r.TargetId = i.Id AND r.Source = 4) AS LibraryRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 0 AND r.TargetId = i.Id AND r.Source = 0) AS UserRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 1 AND r.TargetId = i.SeriesId AND r.Source = 5) AS SeriesOverrideRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 1 AND r.TargetId = i.SeriesId AND r.Source = 4) AS SeriesLibraryRating,
       -- A BOOK'S AUTHOR LIVES IN ItemCredit. books-import-calibre writes one row per author
       -- there (Source=Calibre, Role=Author) and nothing else in this query can see it: the
       -- other creator columns are ComicEmbedded.Writers, ExternalWork.Authors and the AI
       -- insight, none of which a Calibre book ever fills. Those credits were powering the
       -- Authors facet while ResolvedCreatorsCsv stayed empty for 125,531 books.
       (SELECT group_concat(c.Name, ', ')
          FROM (SELECT Name FROM ItemCredit
                 WHERE ItemId = i.Id AND Role = 'Author' ORDER BY Ordinal) c) AS CreditAuthors,
       (SELECT group_concat(t.Value, char(31)) FROM ItemTag t WHERE t.ItemId = i.Id AND t.Category IN ('genre','tag')) AS ItemTags,
       (SELECT group_concat(t.Value, char(31)) FROM SeriesTag t WHERE t.SeriesId = i.SeriesId AND t.Category = 'tag') AS SeriesTags
FROM Item i
LEFT JOIN ComicDetail cd ON cd.ItemId = i.Id
LEFT JOIN ComicEmbedded ce ON ce.ItemId = i.Id
LEFT JOIN BookDetail bd ON bd.ItemId = i.Id
LEFT JOIN Series s ON s.Id = i.SeriesId
LEFT JOIN CvVolume cv ON cv.Id = s.CvVolumeId
LEFT JOIN ExternalWork ew ON ew.Id = s.ExternalWorkId
LEFT JOIN MuSeries mu ON mu.Id = s.MuSeriesId
LEFT JOIN ItemProviderLink lk ON lk.ItemId = i.Id AND lk.Provider = 2 AND lk.Status = 1 AND lk.Quality IN (2, 3)
LEFT JOIN LocgComic lc ON lc.LocgComicId = CAST(lk.ProviderKey AS INTEGER)
LEFT JOIN Insight si ON si.SubjectKind = 1 AND si.SubjectId = i.SeriesId AND si.IsCurrent = 1
LEFT JOIN Insight bi ON bi.SubjectKind = 0 AND bi.SubjectId = i.Id AND bi.IsCurrent = 1
LEFT JOIN ReadingOrderEntry ro ON ro.ItemId = i.Id
LEFT JOIN CollectionNode cn ON cn.ItemId = i.Id
LEFT JOIN ItemState st ON st.ItemId = i.Id
LEFT JOIN Publisher p ON p.Id = i.PublisherId
WHERE i.Id > $after ORDER BY i.Id LIMIT $n";

        /// <summary>One chunk of items after <paramref name="afterId"/>; returns the last id and how many were resolved.</summary>
        public static long ResolveItems(TargetWriter hot, long afterId, int batchSize, out int resolved)
        {
            resolved = 0;
            var last = afterId;
            var now = DateTime.UtcNow;
            using var cmd = hot.CreateCommand(ItemSql);
            cmd.Parameters.AddWithValue("$after", afterId);
            cmd.Parameters.AddWithValue("$n", batchSize);
            var rows = new List<V1Row>();
            using (var rd = cmd.ExecuteReader())
            {
                var names = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToArray();
                while (rd.Read())
                {
                    var vals = new object?[rd.FieldCount];
                    for (var i = 0; i < vals.Length; i++) vals[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(new V1Row(names, vals));
                }
            }
            foreach (var r in rows)
            {
                var id = r.L("Id")!.Value;
                last = id;
                var isBook = r.Int("Kind") == (int)ItemKind.Book;
                var series = FirstText(r.T("DisplayNameOverride"), r.T("SeriesName"), r.T("ParsedSeriesKey"), r.T("EmbSeries"), r.T("BookSeries"));
                var publisher = FirstText(r.T("CvPublisher"), isBook ? r.T("BookPublisher") : r.T("EmbPublisher"), r.T("ParsedPublisher"), r.T("PublisherName"));
                var isSingle = r.I("IssueCount") == 1;
                var isCollection = (r.I("CollectionLevel") ?? 0) > 0 || r.B("IsCollection");
                var title = isBook ? r.T("Title") : ResolveTitle(r.T("Title"), series, isSingle, isCollection, r.D("ReadNumber"), r.T("IssueNo"), r.I("VolumeNo"));
                var (year, month, precision) = ResolveDate(r.T("ReadDate"), (DatePrecision)r.Int("ReadDatePrecision"), isBook ? r.T("PublishedOn") : r.T("PublicationDate"), r.I("ParsedYear"), r.I("ExtYear"));
                var aiSynopsis = isBook ? r.S("BookAiSynopsis") : r.S("SeriesAiSynopsis");
                var synopsis = SynopsisRules.ResolveItem(r.S("CvDescription"), isBook ? r.S("BookDescription") : r.S("Summary"), r.S("LocgDescription"), r.S("ExtDescription"), r.S("MuDescription"), r.S("CvDeck"), aiSynopsis);
                // For a BOOK the Calibre credit is the best source there is — it came from the
                // library's own metadata — so it leads. A comic keeps its existing order.
                var authors = isBook
                    ? FirstNonEmpty(r.S("CreditAuthors"), r.S("Writers"), r.S("ExtAuthors"), r.S("BookAiAuthor"))
                    : FirstNonEmpty(r.S("Writers"), r.S("ExtAuthors"), r.S("AiAuthor"));
                var artists = FirstNonEmpty(string.IsNullOrWhiteSpace(r.S("Pencillers")) ? r.S("CoverArtist") : r.S("Pencillers"), r.S("AiArtist"));
                var creators = authors.Concat(artists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var tags = new List<string>();
                var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in new[] { r.S("ItemTags"), r.S("SeriesTags") })
                    if (src != null)
                        foreach (var t in src.Split(TargetWriter.Sep))
                            if (t.Length > 0 && !Cvdb.IsMatch(t) && seenTags.Add(t)) tags.Add(t);
                var rating = r.I("OverrideRating") ?? r.I("LibraryRating") ?? r.I("SeriesOverrideRating") ?? r.I("SeriesLibraryRating") ?? r.I("UserRating") ?? (isBook ? r.I("BookAiRating") : r.I("SeriesAiRating"));
                hot.Update("Item", "Id", (int)id, new
                {
                    ResolvedTitle = title, ResolvedSeries = series, ResolvedPublisher = publisher, ResolvedYear = year, ResolvedMonth = month, ResolvedDatePrecision = precision,
                    ResolvedRating = rating, ResolvedSynopsisSource = synopsis, ResolvedCreatorsCsv = creators.Count == 0 ? null : string.Join(", ", creators),
                    ResolvedTagsCsv = tags.Count == 0 ? null : string.Join(", ", tags), CoverAspect = ClampAspect(r.I("CoverWidth"), r.I("CoverHeight")), ResolvedAt = now,
                });
                resolved++;
            }
            return last;
        }

        private const string SeriesSql = @"
SELECT s.Id, cv.Description AS CvDescription, cv.Deck AS CvDeck, mu.Description AS MuDescription, ew.Description AS ExtDescription,
       si.Synopsis AS AiSynopsis, si.Rating AS AiRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 1 AND r.TargetId = s.Id AND r.Source = 5) AS OverrideRating,
       (SELECT r.Value FROM Rating r WHERE r.TargetKind = 1 AND r.TargetId = s.Id AND r.Source = 4) AS LibraryRating
FROM Series s
LEFT JOIN CvVolume cv ON cv.Id = s.CvVolumeId
LEFT JOIN ExternalWork ew ON ew.Id = s.ExternalWorkId
LEFT JOIN MuSeries mu ON mu.Id = s.MuSeriesId
LEFT JOIN Insight si ON si.SubjectKind = 1 AND si.SubjectId = s.Id AND si.IsCurrent = 1
ORDER BY s.Id";

        /// <summary>Series.ResolvedSynopsisSource / ResolvedRating for every series (small: ~20k rows, one pass).</summary>
        public static int ResolveSeries(TargetWriter hot)
        {
            var now = DateTime.UtcNow;
            var n = 0;
            using var cmd = hot.CreateCommand(SeriesSql);
            var rows = new List<V1Row>();
            using (var rd = cmd.ExecuteReader())
            {
                var names = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToArray();
                while (rd.Read())
                {
                    var vals = new object?[rd.FieldCount];
                    for (var i = 0; i < vals.Length; i++) vals[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(new V1Row(names, vals));
                }
            }
            foreach (var r in rows)
            {
                var synopsis = SynopsisRules.ResolveSeries(r.S("CvDescription"), r.S("MuDescription"), r.S("ExtDescription"), r.S("AiSynopsis"), r.S("CvDeck"));
                var rating = r.I("OverrideRating") ?? r.I("LibraryRating") ?? r.I("AiRating");
                hot.Update("Series", "Id", r.Int("Id"), new { ResolvedSynopsisSource = synopsis, ResolvedRating = rating, ResolvedAt = now });
                n++;
            }
            return n;
        }

        private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }
}
