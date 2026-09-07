using System.Globalization;
using System.Text.RegularExpressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Resolve
{
    /// <summary>One provider's claim "&lt;edition title&gt; collects #Start-#End", with the provider's own key.</summary>
    public readonly record struct EditionCandidate(string Title, double Start, double End, string? Ref);

    /// <summary>The one collection book a producer is trying to place: everything the match may look at.</summary>
    public sealed class CollectionBook
    {
        public int ItemId;
        public int SeriesId;
        public string FileName = "";
        public int? VolumeNo;
        public string? IssueNo;
        public CollectionLevel Level;
        public int PageCount;
        public int? Year;
    }

    /// <summary>
    /// The ComicVine "Collected Editions" prose parser, ported from the standalone's
    /// <c>CollectedEditionService.ParseEditions</c> (which is intact in <c>F:\Work\MyBooks</c> and was the only
    /// thing that ever read this block). A ComicVine volume description carries the publisher's own list of the
    /// collected editions of that run — "&lt;edition title&gt; (#start-end)" — which is a CONTAINER record, not
    /// an issue record, and is therefore the only ComicVine answer a collected edition may be matched to.
    /// </summary>
    public static class CvEditionParser
    {
        private static readonly Regex RxTag = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex RxWs = new(@"\s+", RegexOptions.Compiled);
        // "<title> (#12-18)" or "<title> (#96)". Title = the text before "(#".
        private static readonly Regex RxEntry = new(@"([^()]{1,90}?)\s*\(#\s*(\d+)\s*(?:[-–—]\s*#?\s*(\d+))?\s*\)", RegexOptions.Compiled);
        private static readonly Regex RxSectionHead = new(@"Collected Editions?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // The section HEADING runs straight into the first entry once the markup is stripped, so the first
        // parsed title reads "Collected Editions Deep State: Dark Side of the Moon". Trim it back off.
        private static readonly Regex RxHeadLeak = new(@"^\s*Collected Editions?\b[:\s]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The block-level tags that separate one edition entry from the next. Stripping ALL markup to a space
        // ran the section heading and the per-edition sub-heading into the first entry's title, so the first
        // TPB of every run read "Trade Paperbacks Volume 1". These become line breaks first.
        private static readonly Regex RxBlockTag =
            new(@"</?(?:li|p|ul|ol|h[1-6]|br|div|tr|td|table)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] SectionStops =
            { "Publishing History", "Awards", "Related", "See also", "External links", "Notes" };

        /// <summary>Extract the "&lt;title&gt; (#start-end)" entries from a volume description. Pure.</summary>
        public static List<EditionCandidate> ParseEditions(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return [];
            // Entities survive the tag strip, so "A&amp;A" would become both the stored EditionTitle and an
            // "amp" token in the match.
            var lines = System.Net.WebUtility.HtmlDecode(RxTag.Replace(RxBlockTag.Replace(description, "\n"), " "))
                .Split('\n')
                .Select(l => RxWs.Replace(l, " ").Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var start = lines.FindIndex(l => RxSectionHead.IsMatch(l));
            if (start < 0) return [];

            var list = new List<EditionCandidate>();
            for (var i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                if (i > start && SectionStops.Any(s => line.Contains(s, StringComparison.OrdinalIgnoreCase))) break;
                foreach (Match em in RxEntry.Matches(line))
                {
                    var title = RxHeadLeak.Replace(em.Groups[1].Value, "").Trim(' ', '.', ',', '-', '–', '—');
                    if (title.Length == 0) continue;
                    if (!double.TryParse(em.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) continue;
                    var e = s;
                    if (em.Groups[3].Success) double.TryParse(em.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out e);
                    if (e < s) (s, e) = (e, s);
                    list.Add(new EditionCandidate(title, s, e, null));
                }
            }
            return list;
        }

        /// <summary>Does this description carry the section at all? (the import's cheap flag)</summary>
        public static bool HasCollectedSection(string? description) =>
            !string.IsNullOrEmpty(description) && description.Contains("Collected Edition", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Which CONTAINER record a collection book is. Every producer in the containment repair matches on the
    /// edition's TITLE — never on an issue number — because the fault being repaired is exactly that: a
    /// collected edition was matched to a provider ISSUE record keyed on the volume ordinal parsed off its
    /// filename ("Saga Book 1" → "Saga #1", 28 pages, zero containment edges).
    ///
    /// <para>The scorer is the standalone's, unchanged in shape: edition-title ∩ filename tokens, plus half a
    /// point per series-name token the edition shares, plus five for an agreeing volume/book number (minus one
    /// when the book HAS a number and the edition names a different one), plus two for an edition-type word
    /// that matches the book's collection level. A different-series entry is skipped, but only when it carries
    /// a name-like token of its own — most ComicVine entries are titled just "Volume 7".</para>
    /// </summary>
    public static class EditionMatcher
    {
        /// <summary>Below this the match is not confident enough to publish.</summary>
        public const double MinScore = 4.0;

        /// <summary>A collected edition is at least this many pages; below it, a "TPB" label is v1 noise.</summary>
        public const int CollectionPageFloor = 60;

        private static readonly Regex RxNonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex RxCollectionNumber =
            new(@"\b(?:book|vol(?:ume)?)\.?\s*0*(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
            ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
            ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
            ["nineteen"] = 19, ["twenty"] = 20,
        };

        private static readonly Dictionary<CollectionLevel, string[]> LevelWords = new()
        {
            [CollectionLevel.Omnibus] = ["omnibus", "compendium"],
            [CollectionLevel.Book] = ["deluxe", "absolute", "library edition", "hardcover"],
            [CollectionLevel.Volume] = ["archives", "classic", "epic collection"],
        };

        // Edition/format/connector/number words. A bare edition built only from these ("Volume 7", "Book One")
        // is THIS series' own edition, not a cross-series reference, so it must not trip the different-series skip.
        private static readonly HashSet<string> StructuralWords = new(StringComparer.Ordinal)
        {
            "volume", "vol", "vols", "book", "books", "part", "parts", "deluxe", "edition", "editions", "hardcover",
            "omnibus", "compendium", "tpb", "trade", "paperback", "paperbacks", "softcover", "collection", "collected",
            "absolute", "library", "complete", "new", "the", "and", "of",
            "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
            "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
        };

        public static string Norm(string? s) => RxNonAlnum.Replace((s ?? "").ToLowerInvariant(), " ").Trim();

        public static HashSet<string> Tokens(string norm) =>
            norm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 1).ToHashSet(StringComparer.Ordinal);

        public static HashSet<int> ExtractNumbers(string norm)
        {
            var nums = new HashSet<int>();
            foreach (var tok in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) nums.Add(n);
                else if (NumberWords.TryGetValue(tok, out var w)) nums.Add(w);
            return nums;
        }

        /// <summary>The collection's own ordinal: its `VolumeNo`, else the number after a Book/Vol marker in the
        /// filename, else the stored `IssueNo` — which on a collection is almost always the mis-parsed ordinal.</summary>
        public static int? CollectionNumber(CollectionBook book)
        {
            if (book.VolumeNo is int v) return v;
            var m = RxCollectionNumber.Match(book.FileName ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            if (int.TryParse(book.IssueNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iss) && iss is > 0 and < 1000) return iss;
            return null;
        }

        /// <summary>Is this item shaped like a collected edition at all? The gate every producer applies before
        /// it will match anything: a 25-page file whose v1 `FormatRaw` says "TPB" is not a collection.</summary>
        public static bool IsCollectionShaped(CollectionLevel level, bool isCollection, int pageCount) =>
            (level > CollectionLevel.Issue || isCollection) && pageCount >= CollectionPageFloor;

        private static readonly Regex RxSubtitle =
            new(@"-\s*([A-Za-z0-9'!,&\. ]+?)\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// The story-arc subtitle a collected edition's filename carries after its volume marker — "Fables Vol.
        /// 09 - War and Pieces (2008)…" → "war and pieces". Providers that record an edition as an issue of a
        /// collection series (GCD) name it by exactly that arc, so an EXACT match here is the strongest
        /// title evidence there is. Empty when the filename has no subtitle.
        /// </summary>
        public static string Subtitle(string? fileName)
        {
            var m = RxSubtitle.Match(fileName ?? "");
            return m.Success ? Norm(m.Groups[1].Value) : "";
        }

        /// <summary>One candidate's score against one book. Higher is better; `MinScore` is the publish floor.</summary>
        public static double ScoreTitle(CollectionBook book, string candidateTitle, string seriesNorm,
            bool guardCrossSeries = true)
        {
            var seriesTokens = Tokens(seriesNorm);
            var bookTokens = Tokens(Norm(book.FileName));
            var edNorm = Norm(candidateTitle);
            var edTokens = Tokens(edNorm);

            // Series-name overlap is a SOFT signal. Only skip an edition that clearly names a DIFFERENT series:
            // it shares none of our series tokens AND carries a non-structural token of its own.
            var shared = seriesTokens.Count(t => edTokens.Contains(t));
            if (guardCrossSeries && shared == 0 && edTokens.Any(t => !StructuralWords.Contains(t) && !seriesTokens.Contains(t)))
                return double.NegativeInfinity;

            var score = edTokens.Count(t => bookTokens.Contains(t)) + shared * 0.5;
            var nums = ExtractNumbers(edNorm);
            var bookNo = CollectionNumber(book);
            if (bookNo is int bn && nums.Contains(bn)) score += 5;
            else if (bookNo is not null) score -= 1;
            if (LevelWords.GetValueOrDefault(book.Level, []).Any(w => edNorm.Contains(w, StringComparison.Ordinal))) score += 2;
            return score;
        }

        /// <summary>Confidence from a score, the standalone's scale (8 points = certain).</summary>
        public static double ConfidenceOf(double score) => Math.Min(1.0, Math.Max(0, score) / 8.0);

        /// <summary>
        /// The best RANGED edition for one book (ComicVine's prose), or null when nothing is confident.
        /// A single-issue "(#N)" entry is almost never the right match for a TPB/HC/Omnibus — it is a stray
        /// reprint listed among the editions — so it is penalised and then rejected outright.
        /// </summary>
        public static (EditionCandidate Edition, double Confidence)? MatchRanged(
            CollectionBook book, IReadOnlyList<EditionCandidate> editions, string seriesNorm)
        {
            EditionCandidate? best = null;
            double bestScore = 0, bestSpan = -1;
            foreach (var ed in editions)
            {
                var score = ScoreTitle(book, ed.Title, seriesNorm);
                if (double.IsNegativeInfinity(score)) continue;
                if (ed.Start == ed.End && book.Level >= CollectionLevel.Volume) score -= 3;

                var span = ed.End - ed.Start;
                // Tie-break: a volumeless book (a lone deluxe/omnibus) prefers the WIDEST span; a numbered
                // volume prefers the tighter one.
                var better = score > bestScore
                    || (Math.Abs(score - bestScore) < 0.001 && (book.VolumeNo is null ? span > bestSpan : span < bestSpan));
                if (better) { best = ed; bestScore = score; bestSpan = span; }
            }
            if (best is not EditionCandidate w || bestScore < MinScore) return null;
            if (w.Start == w.End && book.Level >= CollectionLevel.Volume) return null;
            return (w, ConfidenceOf(bestScore));
        }

        /// <summary>
        /// The best CONTAINER RECORD for one book when the provider supplies only a title (GCD's collection
        /// issues, LOCG's edition list) and the range comes from the record itself. Title only — never a number.
        ///
        /// <para>The candidate list is ALREADY narrowed to one provider series, so the cross-series guard that
        /// protects the ComicVine prose match is off here: a GCD collection issue is titled by its story arc
        /// ("War and Pieces"), which shares nothing with the series name and would be rejected outright.
        /// An EXACT match on the filename's own subtitle is the strongest signal and is taken first — it is what
        /// the standalone's GCD matcher did, and it is why `Exact` is reported back.</para>
        /// </summary>
        public static (TKey Key, string Title, double Confidence, bool Exact)? MatchTitleOnly<TKey>(
            CollectionBook book, IReadOnlyList<(TKey Key, string Title)> candidates, string seriesNorm)
        {
            var subtitle = Subtitle(book.FileName);
            if (subtitle.Length > 0)
                foreach (var (key, title) in candidates)
                    if (Norm(title) == subtitle)
                        return (key, title, 0.9, true);

            TKey? bestKey = default;
            string? bestTitle = null;
            double bestScore = 0;
            foreach (var (key, title) in candidates)
            {
                var score = ScoreTitle(book, title, seriesNorm, guardCrossSeries: false);
                if (double.IsNegativeInfinity(score) || score <= bestScore) continue;
                bestKey = key; bestTitle = title; bestScore = score;
            }
            if (bestTitle is null || bestScore < MinScore) return null;
            return (bestKey!, bestTitle, ConfidenceOf(bestScore), false);
        }
    }

    /// <summary>
    /// The page-count arithmetic AUDIT. A collected edition's page count divided by the number of issues its
    /// span claims lands in a narrow band for real comics; far outside it, the span and the book disagree about
    /// what the book is. This FLAGS — it never drops a span: a 1,226-page omnibus claiming six issues is
    /// suspicious, but so is a legitimately thin digest, and the operator decides.
    /// </summary>
    public static class PageArithmetic
    {
        public const double ThinPagesPerIssue = 12;
        public const double ThickPagesPerIssue = 60;

        public static double? PagesPerIssue(int pageCount, double start, double end)
        {
            if (pageCount <= 0 || end < start) return null;
            var issues = end - start + 1;
            return issues <= 0 ? null : pageCount / issues;
        }

        /// <summary>"thin" (fewer than 12 pages per claimed issue), "thick" (more than 60), or null.</summary>
        public static string? Flag(int pageCount, double start, double end) =>
            PagesPerIssue(pageCount, start, end) is not double ppi ? null
            : ppi < ThinPagesPerIssue ? "thin"
            : ppi > ThickPagesPerIssue ? "thick"
            : null;
    }
}
