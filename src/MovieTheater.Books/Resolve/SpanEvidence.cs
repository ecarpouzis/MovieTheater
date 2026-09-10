using System.Text.RegularExpressions;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// Whether a collected-edition range can point at the book's own words for its proof.
    ///
    /// <para><b>Why this exists.</b> The de-duplication used to exempt every `Curated` row that this pass did
    /// not write — "gold" — from the confidence floor, on the theory that v1 had read the book. Measured
    /// against issue-level ground truth, gold confirms at 47.8% against 83.7% for the model rows, so the
    /// label earns nothing. What earns trust is the QUOTATION: of 988 gold rows, 564 carry an indicia line
    /// ("Originally published in single magazine form as FABLES 1-5") whose issue list exactly equals the
    /// range. Those are self-proving. The rest are covers, inference, or a note with no quote at all, and
    /// they are worth no more than any other judged answer.</para>
    ///
    /// <para>So trust is granted per row, by evidence, and never by provenance.</para>
    /// </summary>
    public static class SpanEvidence
    {
        // The note stores the indicia verbatim inside quotes; � appears where an en dash was mangled
        // on the way in, and it has to count as a range separator or the range reads as two singles.
        private static readonly Regex RxQuoted =
            new("['‘“](.*?)['’”]", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex RxYear = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
        private static readonly Regex RxRange =
            new(@"#?(\d{1,4})\s*(?:-|–|—|�|through|thru|to)\s*#?(\d{1,4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxSingle = new(@"#?(?<![\d.])(\d{1,4})(?![\d.])", RegexOptions.Compiled);

        /// <summary>The widest range a single quotation may name before it is treated as noise rather than a list.</summary>
        public const int MaxRangeWidth = 500;

        /// <summary>
        /// The issue numbers the note's quoted indicia names, or null when the note carries no quotation.
        /// An empty set is returned as null too: a quote naming no issues proves nothing.
        /// </summary>
        public static HashSet<double>? QuotedIssues(string? note)
        {
            if (string.IsNullOrWhiteSpace(note)) return null;
            var m = RxQuoted.Match(note);
            if (!m.Success) return null;

            var text = RxYear.Replace(m.Groups[1].Value, " ");
            var set = new HashSet<double>();
            foreach (Match r in RxRange.Matches(text))
            {
                var a = int.Parse(r.Groups[1].Value);
                var b = int.Parse(r.Groups[2].Value);
                if (b < a || b - a > MaxRangeWidth) continue;
                for (var i = a; i <= b; i++) set.Add(i);
            }
            foreach (Match s in RxSingle.Matches(RxRange.Replace(text, " ")))
                set.Add(double.Parse(s.Groups[1].Value));

            return set.Count == 0 ? null : set;
        }

        /// <summary>
        /// The issues inside <paramref name="start"/>..<paramref name="end"/> that the note's quotation
        /// DENIES — the gap in a bounding range, as the book's own indicia states it.
        ///
        /// <para><b>Why a gap is a denial and not a hole in the transcription.</b> A `CollectedEditionSpan`
        /// stores two numbers, so an edition that collects "CHECKMATE 13-19, 26-31" is stored as 13-31 and
        /// the six issues it explicitly does not contain are indistinguishable from the thirteen it does.
        /// Everything downstream then treats #20-25 as redundant with a book that never printed them, and
        /// over-claiming is the direction that loses files. The quotation already in the note settles it.</para>
        ///
        /// <para><b>The anchor rule is what makes this safe.</b> The quotation must name BOTH endpoints
        /// before its gaps count. Most curated notes quote a DIFFERENT numbering than the range — Baltimore's
        /// per-arc indicia ("The Red Kingdom #1-#5") under a continuous 36-40, an Essential Groo volume
        /// quoting "Groo v2 #76 to #88" under its own ordinal, a sibling volume's range quoted as the reason
        /// this one starts where it does. Every one of those names neither endpoint, and reading its gaps as
        /// denials would empty correct editions. Requiring both ends drops all 56 of them and keeps the 37
        /// rows that really are quoting this range with a hole in it.</para>
        ///
        /// <para>Returns null when there is no quotation, no anchor, or no gap. A gap wider than the part
        /// that is named is still a gap: "Collecting Amazing Spider-Man #88-92 and #121-122" denies #93-120,
        /// and refusing to believe it only means claiming issues the book does not hold.</para>
        /// </summary>
        public static HashSet<double>? ExcludedIssues(string? note, double start, double end)
        {
            if (end < start || start != Math.Floor(start) || end != Math.Floor(end)) return null;
            var quoted = QuotedIssues(note);
            if (quoted == null || !quoted.Contains(start) || !quoted.Contains(end)) return null;
            var missing = new HashSet<double>();
            for (var i = start; i <= end; i++)
                if (!quoted.Contains(i)) missing.Add(i);
            return missing.Count == 0 ? null : missing;
        }

        /// <summary>
        /// True when the note quotes the book and the quote names exactly the issues the range claims.
        /// This — not the `gold` label — is what exempts a row from the de-duplication's confidence floor.
        /// </summary>
        public static bool SelfProving(string? note, double start, double end)
        {
            var quoted = QuotedIssues(note);
            if (quoted == null || end < start) return false;
            if (quoted.Count != (int)(end - start) + 1) return false;
            for (var i = start; i <= end; i++)
                if (!quoted.Contains(i)) return false;
            return true;
        }
    }
}
