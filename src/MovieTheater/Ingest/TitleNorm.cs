using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Shared title/folder normalization for the inventory file-mapping passes (movies + series). Kept in one
    /// place so both mappers fold identically. ASCII-folding matters: DB titles carry real glyphs (e.g. the
    /// AE-ligature in "AEon Flux", the accent in "Pokemon") while the NAS folders use plain ASCII.
    /// </summary>
    public static class TitleNorm
    {
        // Keys are written as numeric code points (NOT literal glyphs and NOT \u escapes) so this source stays
        // pure-ASCII and the fold can't be broken by how the compiler reads the file encoding -- a literal
        // 'ae'-ligature key silently broke the AEon Flux match. NFKD (below) handles plain diacritics like the
        // accent in "Pokemon"; this table is only the ligatures NFKD won't decompose.
        private static readonly Dictionary<char, string> Lig = new()
        {
            [(char)0x00E6] = "ae",  // ae ligature
            [(char)0x0153] = "oe",  // oe ligature
            [(char)0x00F8] = "o",   // o with stroke
            [(char)0x00DF] = "ss",  // sharp s
            [(char)0x00F0] = "d",   // eth
            [(char)0x00FE] = "th",  // thorn
            [(char)0x0111] = "d",   // d with stroke
            [(char)0x0142] = "l",   // l with stroke
        };

        /// <summary>Lowercase + ASCII-fold: ligatures via the table, other diacritics via NFKD mark-stripping.</summary>
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                var lc = char.ToLowerInvariant(c);
                sb.Append(Lig.TryGetValue(lc, out var r) ? r : lc.ToString());
            }
            var n = sb.ToString().Normalize(NormalizationForm.FormKD);
            var o = new StringBuilder();
            foreach (var c in n)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) o.Append(c);
            return o.ToString();
        }

        /// <summary>Move a leading "The " to a trailing ", The" (the library's A-Z sort convention).
        /// The article belongs to the MAIN title, so when a subtitle follows ("The X: Y") the inverted
        /// article is re-attached right before the first colon -> "X, The: Y", NOT dumped after the
        /// subtitle ("X: Y, The"). An interior article inside the subtitle ("The X: The Y") is left
        /// untouched. Null/empty-safe; leaves "A"/"An" alone (dropped by hand, not auto-inverted) and
        /// won't double-invert a title that already carries ", The".</summary>
        public static string InvertLeadingThe(string title)
        {
            var t = (title ?? "").Trim();
            if (!t.StartsWith("The ", System.StringComparison.OrdinalIgnoreCase)) return title;
            // Already inverted somewhere (end of main title or end of whole string) -> don't double-invert.
            if (t.EndsWith(", The", System.StringComparison.OrdinalIgnoreCase) ||
                t.Contains(", The:", System.StringComparison.OrdinalIgnoreCase)) return title;

            var rest = t.Substring(4).Trim(); // drop leading "The "
            if (rest.Length == 0) return title;

            var colon = rest.IndexOf(':');
            if (colon < 0) return rest + ", The";

            var head = rest.Substring(0, colon).TrimEnd();
            var tail = rest.Substring(colon); // includes the ':' and the rest of the subtitle
            if (head.Length == 0) return rest + ", The"; // pathological "The : foo" -> fall back
            return head + ", The" + tail;
        }
    }
}
