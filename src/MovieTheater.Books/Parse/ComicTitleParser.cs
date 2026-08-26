using System.Text.RegularExpressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Parse
{
    /// <summary>
    /// The parse pipeline that turns a noisy comic filename + its folder path into a structured reading:
    /// series, issue number, year, volume, publisher, format — each with the SOURCE it came from and an overall
    /// confidence. Ported from the standalone site's `ParsedDetailService`, regex for regex, onto the v2 enums.
    ///
    /// <para><b>Pure.</b> Nothing here touches a database or a file — <see cref="Parse"/> takes strings and
    /// returns a record, which is what makes the 33 spellings, the "2000 AD" trap and every other edge case
    /// testable without a library. <see cref="Services.LibraryScanner"/> is the only caller that persists it.</para>
    ///
    /// <para><b>The load-bearing rules</b>, in the order they matter:</para>
    /// <list type="bullet">
    /// <item>Embedded ComicInfo beats the filename beats the folder — but a filename that STARTS with a
    /// zero-padded sort index ("042 - Red Birds") is a story title, not a series name, so a folder with a
    /// confirmed year outranks it.</item>
    /// <item>Issue extraction is a strict first-match-wins ladder. "01 (of 04)" is issue 1, not 4. A number
    /// with LEADING ZEROS before a "(YYYY)" is a padded index, which is the only reliable way to tell
    /// "2000 AD 0001 (1977)" apart from a title that happens to contain 2000.</item>
    /// <item>A ComicInfo <c>&lt;Volume&gt;</c> in [1900, 2099] is the series START YEAR under a widespread
    /// tagging convention — never a run number. It is dropped as a volume and kept as a last-resort year.</item>
    /// <item>ISO date stamps are stripped BEFORE any number extraction: the month and day look exactly like
    /// bare issue numbers to the right-to-left fallback.</item>
    /// </list>
    /// </summary>
    public static class ComicTitleParser
    {
        // ── filename cleaning ────────────────────────────────────────────────────────────────────────────
        private static readonly Regex RxLeadingNum = new(@"^\s*-?0\d+[a-z]?[\s._-]+", RegexOptions.Compiled);
        private static readonly Regex RxParens = new(@"\([^()]*\)", RegexOptions.Compiled);
        private static readonly Regex RxSquare = new(@"\[[^\[\]]*\]", RegexOptions.Compiled);
        private static readonly Regex RxCurly = new(@"\{[^{}]*\}", RegexOptions.Compiled);
        private static readonly Regex RxScannerTags = new(
            @"\b(c2c|ctc|noads?|tbp|hc|tpb|fcbd|owp?|fixed|repack|retail|web-?rip|scan)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"Book NN" / "Bk NN" enumerate a collected-edition line exactly like "Vol. NN", so they are
        /// stripped from the series name too — otherwise "Book" glues onto the title and shatters the line into a
        /// phantom "&lt;Series&gt;Book" divorced from the issues it collects.</summary>
        private static readonly Regex RxVolumeLabel = new(
            @"\s*[-–]?\s*\b(?:Vol(?:ume)?|Issue|Book|Bk)\.?\s*#?\s*\d+\b(?:\s*[-–]+\s*.+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxVariant = new(
            @"\s*\bVariant\b(?:\s+(?:Edition|Cover|[A-Z]\b))*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxIssueHash = new(@"\s*#\s*\d+\s*$", RegexOptions.Compiled);
        private static readonly Regex RxIssueMarker = new(@"\s*#\s*\d+", RegexOptions.Compiled);
        private static readonly Regex RxVersionNum = new(@"\s+v\d+\b(?:\s*[-–]+\s*.+)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxChapterRange = new(@"\s+c\d{1,3}\s*[-–]\s*c?\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxByAuthor = new(@"\s+by\s+[A-Z].*$", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>The qualifier word is REQUIRED for keywords that legitimately occur inside series names
        /// ("The Acme Novelty Library", "Ultimate Spider-Man", "Absolute Batman") — stripping them bare glued the
        /// following number onto the prior word and shattered those lines into one-issue phantom series. Only
        /// Omnibus / Facsimile / Oversized still strip bare; that is what folds "&lt;Series&gt; Omnibus Book NN"
        /// back into the series.</summary>
        private static readonly Regex RxEdition = new(
            @"\s*[-–]?\s*\b(?:The\s+)?(?:" +
            @"(?:Deluxe|Facsimile|Library|Complete|Omnibus|Definitive|" +
            @"Absolute|Essential|Ultimate|Legendary|Oversized|Collector['’]?s['’]?)" +
            @"\s+(?:Edition|Collection|HC|TP|Series|Cut|Volume|Set)\b" +
            @"|(?:Omnibus|Facsimile|Oversized)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxOfCount = new(@"(?<=\d)\s*(?:of|de|di|von|van|z)\s*#?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxPageCount = new(@"[,.]?\s*\d+\s*(?:p|pg|pgs|pages)\b[,.]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxIsoDate = new(@"\b((19|20)\d{2})[-/]\d{1,2}[-/]\d{1,2}\b", RegexOptions.Compiled);
        private static readonly Regex RxTrailNumEnd = new(@"\s+\d+\s*$", RegexOptions.Compiled);
        private static readonly Regex RxTrailNumFull = new(@"\s+\d+\b.*$", RegexOptions.Compiled);
        private static readonly Regex RxBareLeadNum = new(@"^\d{1,3}\s+(?=[A-Za-z])", RegexOptions.Compiled);
        private static readonly Regex RxCvvTag = new(@"\s*\bcvv-?\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxSpaces = new(@"\s{2,}", RegexOptions.Compiled);

        // ── issue extraction ladder ──────────────────────────────────────────────────────────────────────
        private static readonly Regex RxHashNum = new(@"#\s*0*(\d+)", RegexOptions.Compiled);
        private static readonly Regex RxVolNum = new(@"\b(?:Vol(?:ume)?|Issue|Book|Bk)\.?\s*#?\s*0*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxOfN = new(@"\(\s*0*(\d+)\s+(?:of|de|di|von|van|z)\s+\d+\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNumBeforeOfParen = new(@"\b0*(\d+)\s*\(\s*(?:of|de|di|von|van|z)\s+\d+\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxSlashN = new(@"\(\s*0*(\d+)\s*/\s*(?!(?:19|20)\d{2}\b)\d+\s*\)", RegexOptions.Compiled);
        private static readonly Regex RxZeroPadBeforeYear = new(@"\b0+(\d+)\b.+?\(\s*(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNumBeforeYear = new(@"\b0*(\d+)\b.+?\(\s*(?:19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNumBeforeBareYear = new(@"\b0*(\d+)\s+(?:19|20)\d{2}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxNumBeforeParenOrEnd = new(@"\b0*(\d+)\s*(?:\[|\(|$)", RegexOptions.Compiled);
        private static readonly Regex RxAnyNum = new(@"(?<![.\d])\b0*(\d+)\b(?![.\d])", RegexOptions.Compiled);
        private static readonly Regex RxVolExtract = new(@"\b(?:Vol(?:ume)?|Book|Bk)\.?\s*#?\s*0*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxVPrefixExtract = new(@"\sv(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxParenYear = new(@"\((\d{4})\)", RegexOptions.Compiled);
        private static readonly Regex RxTrailingYear = new(@"\b((?:19|20)\d{2})\s*$", RegexOptions.Compiled);
        private static readonly Regex RxMetaYear = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

        // ── folder parsing ───────────────────────────────────────────────────────────────────────────────
        private static readonly Regex RxFolderYear = new(@"\(\s*((19|20)\d{2})\s*\)", RegexOptions.Compiled);
        private static readonly Regex RxFolderVolume = new(@"\b(?:v(?:ol(?:ume)?)?\.?\s*)(\d+)\b(?!\s*\.\s*\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxFolderPrefix = new(@"^[\s_#]*(?:0\d+|[0-9]+)[\s._-]+", RegexOptions.Compiled);
        private static readonly Regex RxFolderNoiseBracket = new(@"\(\s*(?!(?:19|20)\d{2}\s*\))[^()]*\)", RegexOptions.Compiled);

        // ── format detection ─────────────────────────────────────────────────────────────────────────────
        private static readonly Regex RxFormatVol = new(@"\b(?:Vol(?:ume)?|v)\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxFormatAnnual = new(@"\bannual\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxHc = new(@"\b(hc|hardcover)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxTpb = new(@"\b(tpb|trade paperback)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxOneShot = new(@"\bone[-\s]?shot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxSpecial = new(@"\bspecial\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>What the embedded ComicInfo (if any) said. Every field is optional.</summary>
        public sealed record Embedded(
            string? Series = null, string? Number = null, string? AltSeries = null, string? AltNumber = null,
            int? Volume = null, string? PublicationDate = null, string? Publisher = null, string? Format = null);

        /// <summary>The parse pipeline's reading of one file — exactly the `ComicDetail` row shape.</summary>
        public sealed record Parsed(
            string? ParsedSeriesKey, string? IssueNo, int? Year, int? VolumeNo, string? Publisher,
            ComicFormat Format, string? FormatRaw, bool IsCollection,
            Confidence Confidence, ParseSource SeriesSource, ParseSource IssueSource, ParseSource YearSource, ParseSource PublisherSource,
            string? FolderSeries, int? FolderYear, string? ParseNotes);

        /// <summary>
        /// Parse one file. <paramref name="filePath"/> is the full path and <paramref name="libraryRoots"/> the
        /// configured roots, so the folder components are taken RELATIVE to a root (the publisher is the first
        /// component after it) rather than from whatever the absolute path happens to start with.
        /// </summary>
        public static Parsed Parse(string fileName, string filePath, Embedded? meta, IReadOnlyList<string> libraryRoots)
        {
            meta ??= new Embedded();
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);

            var fnClean = CleanTitle(stem);
            var fnIssue = ExtractIssueNo(stem);
            var fnYear = ExtractYearFromFilename(stem);
            var fnVolume = ExtractVolumeNo(stem);
            var hasSortPrefix = RxLeadingNum.IsMatch(stem) || (RxBareLeadNum.IsMatch(stem) && RxHashNum.IsMatch(stem));

            var components = RelativeComponents(filePath, libraryRoots);
            var folderPublisher = components.Length > 0 ? components[0] : null;
            var (_, folderClean, folderYear, folderVolume) = BestSeriesComponent(components);

            var notes = new List<string>();

            // Volume: a value in [1900, 2099] is a YEAR signal under the widespread ComicInfo convention.
            int? volumeYear = null;
            string? bestVolumeNo = null;
            foreach (var cand in new[] { meta.Volume?.ToString(), fnVolume, folderVolume })
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                if (int.TryParse(cand, out var cv) && cv is >= 1900 and <= 2099) { volumeYear ??= cv; continue; }
                bestVolumeNo = cand;
                break;
            }
            if (volumeYear.HasValue) notes.Add($"Volume '{volumeYear}' looks like a year, not a run number — dropped");

            int? metaYear = null;
            if (meta.PublicationDate != null)
            {
                var ym = RxMetaYear.Match(meta.PublicationDate);
                if (ym.Success && int.TryParse(ym.Value, out var y)) metaYear = y;
            }
            var bestYear = metaYear ?? fnYear ?? folderYear ?? volumeYear;
            var yearSource = metaYear.HasValue ? ParseSource.Metadata
                : fnYear.HasValue ? ParseSource.Filename
                : folderYear.HasValue ? ParseSource.Folder
                : volumeYear.HasValue ? ParseSource.Volume
                : ParseSource.None;

            string bestSeries;
            ParseSource seriesSource;
            if (!string.IsNullOrWhiteSpace(meta.Series)) { bestSeries = meta.Series.Trim(); seriesSource = ParseSource.Metadata; }
            else if (!string.IsNullOrWhiteSpace(meta.AltSeries)) { bestSeries = meta.AltSeries.Trim(); seriesSource = ParseSource.MetadataAlt; }
            else if (folderClean != null && folderYear.HasValue && hasSortPrefix)
            {
                bestSeries = folderClean;
                seriesSource = ParseSource.Folder;
                notes.Add($"Series from folder (sort-prefix in filename: '{stem}')");
            }
            else if (!string.IsNullOrWhiteSpace(fnClean)) { bestSeries = fnClean; seriesSource = ParseSource.Filename; }
            else if (folderClean != null) { bestSeries = folderClean; seriesSource = ParseSource.Folder; }
            else { bestSeries = stem; seriesSource = ParseSource.None; }

            // Some taggers leave a stray ComicVine volume-id token in the series field; it must never leak
            // into the series name, because the name IS the resolution key.
            bestSeries = RxCvvTag.Replace(bestSeries, "").Trim();

            string? bestIssue;
            ParseSource issueSource;
            if (!IsGarbageIssueNumber(meta.Number)) { bestIssue = meta.Number!.Trim(); issueSource = ParseSource.Metadata; }
            else if (!IsGarbageIssueNumber(meta.AltNumber)) { bestIssue = meta.AltNumber!.Trim(); issueSource = ParseSource.MetadataAlt; }
            else if (fnIssue != null && !IsGarbageIssueNumber(fnIssue))
            {
                bestIssue = fnIssue;
                issueSource = fnIssue.StartsWith('0') ? ParseSource.FilenameLeadingIndex : ParseSource.Filename;
            }
            else { bestIssue = null; issueSource = ParseSource.None; }

            // Folder-series + a number that came from the sort prefix: flag it so a reviewer is sceptical.
            if (seriesSource == ParseSource.Folder && hasSortPrefix && issueSource == ParseSource.Filename)
                issueSource = ParseSource.FilenameLeadingIndex;

            string? bestPublisher;
            ParseSource pubSource;
            if (!string.IsNullOrWhiteSpace(meta.Publisher)) { bestPublisher = meta.Publisher.Trim(); pubSource = ParseSource.Metadata; }
            else if (folderPublisher != null) { bestPublisher = folderPublisher; pubSource = ParseSource.Folder; }
            else { bestPublisher = null; pubSource = ParseSource.None; }

            var (format, formatRaw, isCollection) = DetectFormat(stem, meta.Format);

            var confidence = seriesSource is ParseSource.Metadata or ParseSource.MetadataAlt ? Confidence.High
                : bestIssue != null ? Confidence.Medium
                : Confidence.Low;

            return new Parsed(
                string.IsNullOrWhiteSpace(bestSeries) ? null : bestSeries,
                bestIssue, bestYear,
                bestVolumeNo != null && int.TryParse(bestVolumeNo, out var vn) ? vn : null,
                bestPublisher, format, formatRaw, isCollection,
                confidence, seriesSource, issueSource, yearSource, pubSource,
                folderClean, folderYear,
                notes.Count > 0 ? string.Join("; ", notes) : null);
        }

        /// <summary>A ComicInfo &lt;Number&gt; that says nothing. Every one of these spellings occurs in the library.</summary>
        public static bool IsGarbageIssueNumber(string? s) =>
            string.IsNullOrWhiteSpace(s) ||
            s.Trim().ToLowerInvariant() is "none" or "n/a" or "na" or "null" or "nil" or "tbd" or "tba" or "-" or "--" or "?" or "#";

        /// <summary>The 16-step cleaning pipeline. Order matters: every later pattern assumes the earlier noise is gone.</summary>
        public static string CleanTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var s = raw.Replace(" & ", " and ").Replace('_', ' ');
            string prev;
            do { prev = s; s = RxLeadingNum.Replace(s, ""); } while (s != prev);
            if (RxHashNum.IsMatch(raw) && RxBareLeadNum.IsMatch(s)) s = RxBareLeadNum.Replace(s, "");
            for (var i = 0; i < 3; i++)
            {
                s = RxParens.Replace(s, "");
                s = RxSquare.Replace(s, "");
                s = RxCurly.Replace(s, "");
            }
            s = RxIsoDate.Replace(s, "");
            s = RxScannerTags.Replace(s, "");
            s = RxPageCount.Replace(s, "");
            s = RxOfCount.Replace(s, "");
            s = RxVolumeLabel.Replace(s, "");
            s = RxVariant.Replace(s, "");
            // The series name ends at the first "#NN" — the issue number AND any trailing subtitle go with it,
            // unless nothing precedes it (a bare "#5" has no series to keep).
            var issueMarker = RxIssueMarker.Match(s);
            s = issueMarker.Success && issueMarker.Index > 0 ? s[..issueMarker.Index] : RxIssueHash.Replace(s, "");
            s = RxVersionNum.Replace(s, "");
            s = RxChapterRange.Replace(s, "");
            s = RxByAuthor.Replace(s, "");
            s = RxEdition.Replace(s, "");
            var stripped = RxTrailNumEnd.Replace(s, "");
            s = stripped != s ? stripped : RxTrailNumFull.Replace(s, "");
            s = RxSpaces.Replace(s, " ").Trim().TrimStart('-', '–', '_').TrimEnd('-', '–', '_').Trim();
            return string.IsNullOrWhiteSpace(s) ? raw.Trim() : s;
        }

        /// <summary>The issue ladder: strict first-match-wins, ending in a right-to-left scan of the CLEANED name.</summary>
        public static string? ExtractIssueNo(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var hm = RxHashNum.Matches(raw);
            if (hm.Count > 0) return NormNumber(hm[^1].Groups[1].Value);        // rightmost explicit #N
            foreach (var rx in new[] { RxVolNum, RxOfN, RxNumBeforeOfParen, RxSlashN, RxZeroPadBeforeYear, RxNumBeforeYear, RxNumBeforeBareYear, RxNumBeforeParenOrEnd })
            {
                var m = rx.Match(raw);
                if (m.Success) return NormNumber(m.Groups[1].Value);
            }
            var cleaned = CleanTitle(raw);
            var nm = RxAnyNum.Matches(cleaned);
            for (var i = nm.Count - 1; i >= 0; i--)
            {
                var val = nm[i].Groups[1].Value;
                if (int.TryParse(val, out var n) && (n < 1900 || n > 2099)) return NormNumber(val);
            }
            return null;
        }

        public static int? ExtractYearFromFilename(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            foreach (Match m in RxParenYear.Matches(raw))
                if (int.TryParse(m.Groups[1].Value, out var y) && y is >= 1900 and <= 2099) return y;
            var iso = RxIsoDate.Match(raw);
            if (iso.Success && int.TryParse(iso.Groups[1].Value, out var iy) && iy is >= 1900 and <= 2099) return iy;
            var bm = RxTrailingYear.Match(raw);
            return bm.Success && int.TryParse(bm.Groups[1].Value, out var by) ? by : null;
        }

        public static string? ExtractVolumeNo(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = RxVolExtract.Match(raw);
            if (m.Success) return NormNumber(m.Groups[1].Value);
            var vm = RxVPrefixExtract.Match(raw);
            return vm.Success ? NormNumber(vm.Groups[1].Value) : null;
        }

        /// <summary>The folder components BELOW a library root, file name excluded. Longest root wins.</summary>
        public static string[] RelativeComponents(string filePath, IReadOnlyList<string> libraryRoots)
        {
            var norm = filePath.Replace('/', '\\');
            foreach (var root in libraryRoots.OrderByDescending(r => r.Length))
            {
                var nr = root.Replace('/', '\\').TrimEnd('\\');
                if (nr.Length == 0 || !norm.StartsWith(nr, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = norm[nr.Length..].TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[..^1] : Array.Empty<string>();
            }
            var all = norm.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return all.Length > 1 ? all[..^1] : Array.Empty<string>();
        }

        /// <summary>
        /// The series folder: the FIRST component after the publisher that carries a "(YYYY)" marker, falling back
        /// to component[1]. That is what skips organisational groupers ("_Daredevil", "#DC Events") in favour of
        /// the folder that actually names a run.
        /// </summary>
        public static (string? Raw, string? Clean, int? Year, string? Volume) BestSeriesComponent(string[] components)
        {
            if (components.Length <= 1) return (null, null, null, null);
            string? bestRaw = null;
            for (var i = 1; i < components.Length; i++)
                if (RxFolderYear.IsMatch(components[i])) { bestRaw = components[i]; break; }
            bestRaw ??= components[1];

            var ym = RxFolderYear.Match(bestRaw);
            var year = ym.Success && int.TryParse(ym.Groups[1].Value, out var y) ? y : (int?)null;

            var vm = RxFolderVolume.Match(bestRaw);
            string? volume = null;
            if (vm.Success) { var r = vm.Groups[1].Value.TrimStart('0'); volume = r.Length > 0 ? r : "0"; }

            var clean = CleanFolderName(bestRaw);
            return (bestRaw, string.IsNullOrWhiteSpace(clean) ? null : clean, year, volume);
        }

        public static string CleanFolderName(string raw)
        {
            var s = raw;
            string prev;
            do { prev = s; s = RxFolderPrefix.Replace(s, "").TrimStart('_', '#', ' '); } while (s != prev);
            s = RxFolderNoiseBracket.Replace(s, "");
            s = RxFolderYear.Replace(s, "");
            s = RxFolderVolume.Replace(s, "");
            s = RxSpaces.Replace(s, " ").Trim().TrimEnd('-', '–', '_').Trim();
            return string.IsNullOrWhiteSpace(s) ? raw.Trim() : s;
        }

        /// <summary>
        /// The format enum plus the RAW spelling it came from. v1 stored 33 free-text spellings in one column;
        /// v2 keeps the enum for querying and `FormatRaw` for the ones the map does not know.
        /// </summary>
        public static (ComicFormat Format, string? Raw, bool IsCollection) DetectFormat(string fileName, string? metaFormat)
        {
            if (!string.IsNullOrWhiteSpace(metaFormat))
            {
                var raw = metaFormat.Trim();
                return raw.ToLowerInvariant() switch
                {
                    "trade paperback" or "tpb" => (ComicFormat.Tpb, raw, true),
                    "hardcover" or "hc" => (ComicFormat.Hardcover, raw, true),
                    "omnibus" => (ComicFormat.Omnibus, raw, true),
                    "annual" => (ComicFormat.Annual, raw, false),
                    "one-shot" or "one shot" => (ComicFormat.OneShot, raw, false),
                    "special" => (ComicFormat.Special, raw, false),
                    "limited series" => (ComicFormat.LimitedSeries, raw, false),
                    "graphic novel" or "gn" => (ComicFormat.GraphicNovel, raw, true),
                    "single issue" => (ComicFormat.SingleIssue, raw, false),
                    "collection" => (ComicFormat.Collection, raw, true),
                    "magazine" => (ComicFormat.Magazine, raw, false),
                    "weekly" => (ComicFormat.Weekly, raw, false),
                    "reprint" => (ComicFormat.Reprint, raw, false),
                    _ => (ComicFormat.Unknown, raw, false),
                };
            }

            var low = fileName.ToLowerInvariant();
            if (low.Contains("omnibus", StringComparison.Ordinal)) return (ComicFormat.Omnibus, null, true);
            if (RxHc.IsMatch(low)) return (ComicFormat.Hardcover, null, true);
            if (RxTpb.IsMatch(low)) return (ComicFormat.Tpb, null, true);
            if (RxFormatAnnual.IsMatch(fileName)) return (ComicFormat.Annual, null, false);
            if (RxOneShot.IsMatch(low)) return (ComicFormat.OneShot, null, false);
            if (RxSpecial.IsMatch(low)) return (ComicFormat.Special, null, false);
            // A "Vol. N" with no explicit #N is a collected volume, not an issue.
            if (RxFormatVol.IsMatch(fileName) && !RxHashNum.IsMatch(fileName)) return (ComicFormat.Tpb, null, true);
            return (ComicFormat.SingleIssue, null, false);
        }

        /// <summary>"007" → "7"; "6.5" stays "6.5"; anything non-numeric is kept as written.</summary>
        public static string NormNumber(string s)
        {
            s = s.Trim();
            if (s.Contains('.')) { var d = s.TrimStart('0').TrimStart('.'); return d.Length > 0 ? d : "0"; }
            return int.TryParse(s, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : s;
        }
    }
}
