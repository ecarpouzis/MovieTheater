using System;
using System.Text.RegularExpressions;

namespace MovieTheater.Music
{
    /// <summary>
    /// Parses the music library's curated folder grammar (music-plan.md §1) — the source of truth
    /// for artist/album identity (§2.3): artist folders are <c>Artist (YearRange)</c> and KEEP a
    /// leading "The" ("The Beatles (1963-1988)") — the <c>, The</c> inversion is the MOVIE
    /// library's convention, NOT this one (applying it here was a real shipped error, since
    /// reverted; <see cref="RestoreArticle"/> only tolerates stragglers). Album folders are
    /// <c>Artist - Album (Year)</c> with optional <c>[Tag]</c> curation brackets, track files are
    /// optionally prefixed <c>NN - Title.ext</c>.
    /// </summary>
    public static class MusicNaming
    {
        private static readonly Regex YearRangeSuffix =
            new(@"^(?<base>.+?)\s*\((?<years>\d{4}(?:\s*-\s*(?:\d{4}|TODO))?)\)$", RegexOptions.Compiled);

        private static readonly Regex YearSuffix =
            new(@"^(?<base>.+?)\s*\((?<year>\d{4})\)$", RegexOptions.Compiled);

        private static readonly Regex BracketTag =
            new(@"\s*\[(?<tag>[^\]]+)\]", RegexOptions.Compiled);

        // Only "NN - Title" / "NN. Title" count as a track-number prefix. A bare leading number with
        // just a space ("99 Problems") is ambiguous with real titles, so it is deliberately NOT parsed.
        private static readonly Regex TrackPrefix =
            new(@"^(?<no>\d{1,3})(?:\s*-\s*|\.\s+)(?<title>.+)$", RegexOptions.Compiled);

        public sealed record ArtistName(string Display, string Sort, string? YearRange);

        public sealed record AlbumName(string Title, int? Year, string? Tag);

        public static ArtistName ParseArtistFolder(string folderName)
        {
            var baseName = folderName.Trim();
            string? years = null;
            var m = YearRangeSuffix.Match(baseName);
            if (m.Success)
            {
                baseName = m.Groups["base"].Value.Trim();
                years = m.Groups["years"].Value.Replace(" ", "");
            }
            return new ArtistName(RestoreArticle(baseName), baseName, years);
        }

        /// <summary>Tolerance shim, not the library grammar: the library keeps a leading "The", but a
        /// stray inverted folder ("Wanted, The (2010-2011)" is the one on disk) still displays right.
        /// Only "The" is restored; "A"/"An" stay literal.</summary>
        public static string RestoreArticle(string sortName)
        {
            const string suffix = ", The";
            return sortName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? "The " + sortName.Substring(0, sortName.Length - suffix.Length)
                : sortName;
        }

        /// <summary>
        /// Parses an album folder name. <paramref name="artistBase"/> is the artist folder's base name
        /// (year range stripped); a leading "<c>artistBase - </c>" prefix is removed so
        /// "AC-DC - Back in Black (1980)" titles as "Back in Black". A prefix that names some OTHER
        /// artist (compilations, splits) is kept verbatim.
        /// </summary>
        public static AlbumName ParseAlbumFolder(string folderName, string artistBase)
        {
            var name = folderName.Trim();

            string? tag = null;
            name = BracketTag.Replace(name, m =>
            {
                tag = tag == null ? m.Groups["tag"].Value : tag + ", " + m.Groups["tag"].Value;
                return "";
            }).Trim();

            int? year = null;
            var y = YearSuffix.Match(name);
            if (y.Success)
            {
                name = y.Groups["base"].Value.Trim();
                year = int.Parse(y.Groups["year"].Value);
            }

            var prefix = artistBase + " - ";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                name = name.Substring(prefix.Length).Trim();

            return new AlbumName(name.Length > 0 ? name : folderName.Trim(), year, tag);
        }

        public static (int? TrackNo, string Title) ParseTrackFileName(string fileNameNoExt)
        {
            var m = TrackPrefix.Match(fileNameNoExt.Trim());
            if (!m.Success) return (null, fileNameNoExt.Trim());
            return (int.Parse(m.Groups["no"].Value), m.Groups["title"].Value.Trim());
        }
    }
}
