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
    }
}
