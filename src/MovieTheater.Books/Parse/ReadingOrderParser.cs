using System.Globalization;
using System.Text.RegularExpressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Parse
{
    /// <summary>
    /// Pure (DB-free) parsing of an issue's reading-order signals: which STREAM it belongs to (the tier), its
    /// numeric position within that stream, a sub-order within one number, and a normalized publication date
    /// that serves as both fallback and tie-break. Ported from the standalone site's `ReadingOrderParser`.
    ///
    /// <para>The tiers are what keep a run readable: the main line first, then annuals, then specials, then the
    /// collected editions, then whatever could not be placed at all. A collected edition with a KNOWN span is
    /// pulled back onto the main line by <see cref="Resolve.ReadingOrderJob"/> — "read the TPB where its content
    /// begins" — but that decision needs the whole run, so it does not live here.</para>
    /// </summary>
    public static class ReadingOrderParser
    {
        public const int TierMain = 0;          // the regular run, including #-1 / #0 / #½ / point issues
        public const int TierAnnual = 10;
        public const int TierSpecial = 20;      // specials, one-shots, giant-size, ashcans, previews
        public const int TierCollection = 30;   // TPB / HC / Omnibus / GN
        public const int TierUnorderable = 40;

        public readonly record struct IssueOrder(int Tier, double? Number, double Suffix, string? Note);
        public readonly record struct NormalizedDate(string? Iso, DatePrecision Precision);

        public static int TierFromFormat(ComicFormat format) => format switch
        {
            ComicFormat.Annual => TierAnnual,
            ComicFormat.Special or ComicFormat.OneShot => TierSpecial,
            ComicFormat.Tpb or ComicFormat.Hardcover or ComicFormat.Omnibus
                or ComicFormat.GraphicNovel or ComicFormat.Collection => TierCollection,
            _ => TierMain,
        };

        private static readonly Regex RxAnnual = new(@"\bannual\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxSpecial = new(@"\b(special|one[-\s]?shot|giant[-\s]?size|ashcan)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxMinusOne = new(@"(^\s*-\s*1\b|\bminus\s*1\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxStoryLtr = new(@"\(?\s*([A-Z])\s+Story\s*\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxHalf = new(@"(½|\b1\s*/\s*2\b|(?<![\d.])\.5\b|\b0\.5\b)", RegexOptions.Compiled);
        private static readonly Regex RxFirstNum = new(@"-?(?:\d+(?:\.\d+)?|\.\d+)", RegexOptions.Compiled);

        /// <summary>Tier + numeric position. The filename supplies the keyword signal a format field may lack.</summary>
        public static IssueOrder ParseIssue(string? issueNo, ComicFormat format, string? fileName)
        {
            var notes = new List<string>();
            var tier = TierFromFormat(format);
            var raw = (issueNo ?? "").Trim();
            var hay = $"{raw} {fileName}";

            // An "Annual 02" stored under Format=SingleIssue still reads as an annual.
            if (tier == TierMain)
            {
                if (RxAnnual.IsMatch(hay)) { tier = TierAnnual; notes.Add("tier=annual (keyword)"); }
                else if (RxSpecial.IsMatch(hay)) { tier = TierSpecial; notes.Add("tier=special (keyword)"); }
            }

            var (number, suffix, numNote) = ParseNumber(raw);
            if (numNote != null) notes.Add(numNote);
            return new IssueOrder(tier, number, suffix, notes.Count > 0 ? string.Join("; ", notes) : null);
        }

        private static (double? Number, double Suffix, string? Note) ParseNumber(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, 0, null);
            var low = raw.ToLowerInvariant();
            if (low is "none" or "tpb" or "hc" or "n/a") return (null, 0, null);

            double suffix = 0;
            string? note = null;

            var sm = RxStoryLtr.Match(raw);
            if (sm.Success)
            {
                var ch = char.ToUpperInvariant(sm.Groups[1].Value[0]);
                suffix = (ch - 'A' + 1) * 0.01;
                note = $"suffix={ch} story";
            }

            if (RxMinusOne.IsMatch(raw)) return (-1, suffix, note);   // #-1 reads before #0
            var half = RxHalf.IsMatch(raw);

            // First number wins ("01 (of 04)" → 1, "24 (B Story)" → 24). IssueNo is already a post-parse
            // value, so a 4-digit number is trusted here — 2000 AD progs legitimately exceed 2000.
            var nm = RxFirstNum.Match(raw);
            if (nm.Success && double.TryParse(nm.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return (half && Math.Abs(n % 1) < double.Epsilon ? n + 0.5 : n, suffix, note);

            return half ? (0.5, suffix, note) : (null, suffix, note);
        }

        private static readonly Regex RxYmd = new(@"\b((?:19|20)\d{2})[-/.](\d{1,2})[-/.](\d{1,2})\b", RegexOptions.Compiled);
        private static readonly Regex RxYm = new(@"\b((?:19|20)\d{2})[-/.](\d{1,2})\b", RegexOptions.Compiled);
        private static readonly Regex RxY = new(@"\b((?:19|20)\d{2})\b", RegexOptions.Compiled);

        /// <summary>
        /// A free-form publication date into a sortable <c>yyyy-MM-dd</c> plus its real precision. Coarse inputs
        /// are ANCHORED — month-only to day 15, year-only to 07-01 — so they sort amid that period's issues
        /// rather than before all of them.
        /// </summary>
        public static NormalizedDate NormalizeDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new NormalizedDate(null, DatePrecision.None);

            var ymd = RxYmd.Match(raw);
            if (ymd.Success)
            {
                var y = int.Parse(ymd.Groups[1].Value);
                var m = Math.Clamp(int.Parse(ymd.Groups[2].Value), 1, 12);
                var d = Math.Clamp(int.Parse(ymd.Groups[3].Value), 1, 28);
                return new NormalizedDate($"{y:0000}-{m:00}-{d:00}", DatePrecision.Day);
            }
            var ym = RxYm.Match(raw);
            if (ym.Success)
            {
                var y = int.Parse(ym.Groups[1].Value);
                var m = Math.Clamp(int.Parse(ym.Groups[2].Value), 1, 12);
                return new NormalizedDate($"{y:0000}-{m:00}-15", DatePrecision.Month);
            }
            var yr = RxY.Match(raw);
            return yr.Success
                ? new NormalizedDate($"{int.Parse(yr.Groups[1].Value):0000}-07-01", DatePrecision.Year)
                : new NormalizedDate(null, DatePrecision.None);
        }

        private static readonly Regex RxOrdinal = new(@"(\d)(st|nd|rd|th)\b", RegexOptions.Compiled);

        /// <summary>A 2000 AD prog cover date ("8th December, 2021") into ISO. Day precision, or null.</summary>
        public static string? NormalizeProgDate(string? coverDate)
        {
            if (string.IsNullOrWhiteSpace(coverDate)) return null;
            var cleaned = RxOrdinal.Replace(coverDate, "$1");
            return DateTime.TryParse(cleaned, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var dt)
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
        }
    }

    /// <summary>
    /// How much a book COLLECTS, from the format, the filename and the page count — the axis the whole
    /// containment model turns on. Keyword first (a name or format tag beats raw size); page count is the
    /// fallback for the default "single issue" and for spellings the map does not know.
    /// </summary>
    public static class CollectionLevels
    {
        public static CollectionLevel Resolve(ComicFormat format, string? formatRaw, string? fileName, int pageCount)
        {
            var f = (formatRaw ?? "").ToLowerInvariant().Trim();
            var fn = (fileName ?? "").ToLowerInvariant();

            if (format == ComicFormat.Omnibus || f.Contains("omnibus", StringComparison.Ordinal)
                || fn.Contains("omnibus", StringComparison.Ordinal) || fn.Contains("compendium", StringComparison.Ordinal))
                return CollectionLevel.Omnibus;
            if (fn.Contains("deluxe", StringComparison.Ordinal) || fn.Contains("absolute", StringComparison.Ordinal)
                || fn.Contains("library edition", StringComparison.Ordinal) || format == ComicFormat.Hardcover)
                return CollectionLevel.Book;
            if (format is ComicFormat.Tpb or ComicFormat.GraphicNovel or ComicFormat.Collection)
                return CollectionLevel.Volume;

            // Explicitly issue-grade formats stay at level 0 whatever their size — an annual or a giant-size
            // special legitimately runs 60–100 pages. "SingleIssue" is NOT here: it is the DEFAULT the parser
            // assigns when nothing matched, so it is not a reliable size signal.
            if (format is ComicFormat.Annual or ComicFormat.Special or ComicFormat.OneShot
                or ComicFormat.LimitedSeries or ComicFormat.Weekly)
                return CollectionLevel.Issue;

            // A 500-page "single issue" is a collection, not a floppy.
            if (pageCount >= 600) return CollectionLevel.Omnibus;
            if (pageCount >= 300) return CollectionLevel.Book;
            if (pageCount >= 100) return CollectionLevel.Volume;
            return CollectionLevel.Issue;
        }
    }
}
