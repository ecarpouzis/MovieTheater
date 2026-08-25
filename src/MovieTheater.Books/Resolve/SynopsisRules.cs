using System.Net;
using System.Text.RegularExpressions;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The standalone site's <c>prepSynopsis</c> / <c>resolveComicSynopsis</c> / <c>resolveSeriesSynopsis</c>,
    /// ported from dataTransform.ts. v2 resolves ONCE, server-side, and stores only WHICH leg won
    /// (<see cref="SynopsisSource"/>) — the text itself is read from that leg's table at display time, so no
    /// synopsis is ever copied. <see cref="Prepare"/> is the same quality gate the client applied, so the pointer
    /// never names a leg whose text would have been rejected.
    /// </summary>
    public static class SynopsisRules
    {
        private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        // LOCG appends a physical-spec tail ("… Comic • 32 pages • $0.75 Cover Date Mar 1984 UPC …")
        private static readonly Regex SpecTail1 = new(@"\s*(?:Comic|Hardcover|Paperback|Trade Paperback|Graphic Novel|Magazine)\b[^.]{0,4}\d+\s*pages\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SpecTail2 = new(@"\s*[•·|]\s*\d+\s*pages\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex SpecTail3 = new(@"\s*(?:Cover Date|UPC|Distributor SKU|Diamond ID)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex CollectionBoilerplate = new(@"^(?:trade paperback |hardcover |tpb |omnibus )?collect(?:s|ing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MetaCruft = new(@"^(?:issues?\s+#?\d|continued (?:in|from)\b|.{0,80}\bis indexed in\b|in the early days of comic)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Flatten HTML (ComicInfo/ComicVine descriptions arrive as HTML with entities) to one-line prose.</summary>
        public static string StripHtml(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var text = WebUtility.HtmlDecode(Tags.Replace(s, " "));
            return Spaces.Replace(text, " ").Trim();
        }

        public static string StripSpecTail(string s)
        {
            s = SpecTail1.Replace(s, "");
            s = SpecTail2.Replace(s, "");
            s = SpecTail3.Replace(s, "");
            return s.Trim();
        }

        /// <summary>Clean + quality-gate one candidate: the display-ready text, or "" when the resolver must fall through.</summary>
        public static string Prepare(SynopsisSource source, string? raw)
        {
            var s = StripHtml(raw);
            if (s.Length == 0) return "";
            if (source == SynopsisSource.Locg) s = StripSpecTail(s);
            if (s.Length == 0) return "";
            if ((source == SynopsisSource.Cv || source == SynopsisSource.Embedded) && s.Length < 200 && CollectionBoilerplate.IsMatch(s)) return "";
            if (source == SynopsisSource.Cv && MetaCruft.IsMatch(s)) return "";
            var min = source == SynopsisSource.AI ? 1 : source == SynopsisSource.CvDeck ? 8 : 40;
            return s.Length >= min ? s : "";
        }

        public static bool Passes(SynopsisSource source, string? raw) => Prepare(source, raw).Length > 0;

        /// <summary>Per-ISSUE order: CV volume → ComicInfo/book description → LOCG → external → MU → CV deck → AI.</summary>
        public static SynopsisSource ResolveItem(string? cv, string? embedded, string? locg, string? ext, string? mu, string? deck, string? ai)
        {
            if (Passes(SynopsisSource.Cv, cv)) return SynopsisSource.Cv;
            if (Passes(SynopsisSource.Embedded, embedded)) return SynopsisSource.Embedded;
            if (Passes(SynopsisSource.Locg, locg)) return SynopsisSource.Locg;
            if (Passes(SynopsisSource.External, ext)) return SynopsisSource.External;
            if (Passes(SynopsisSource.Mu, mu)) return SynopsisSource.Mu;
            if (Passes(SynopsisSource.CvDeck, deck)) return SynopsisSource.CvDeck;
            if (Passes(SynopsisSource.AI, ai)) return SynopsisSource.AI;
            return SynopsisSource.None;
        }

        /// <summary>Per-SERIES order (never a per-issue leg): CV volume → MU → external → AI → CV deck.</summary>
        public static SynopsisSource ResolveSeries(string? cv, string? mu, string? ext, string? ai, string? deck)
        {
            if (Passes(SynopsisSource.Cv, cv)) return SynopsisSource.Cv;
            if (Passes(SynopsisSource.Mu, mu)) return SynopsisSource.Mu;
            if (Passes(SynopsisSource.External, ext)) return SynopsisSource.External;
            if (Passes(SynopsisSource.AI, ai)) return SynopsisSource.AI;
            if (Passes(SynopsisSource.CvDeck, deck)) return SynopsisSource.CvDeck;
            return SynopsisSource.None;
        }
    }
}
