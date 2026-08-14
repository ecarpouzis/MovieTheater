using System.Text.RegularExpressions;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Reads a movie identity out of an on-disk folder name for the sync's candidate classification.
    /// This library's convention (enforced by the download-sorting pipeline) is
    /// <c>[NN - ]Title (Year)[ tags]</c>, so a parse only succeeds when a parenthesized year is
    /// present — a folder without one is not confidently a movie folder and stays unclassified.
    /// </summary>
    public static class MovieFolderParser
    {
        private static readonly Regex Ordinal = new(@"^\s*\d{1,3}[a-z]?\s*-\s*", RegexOptions.Compiled);
        private static readonly Regex YearParen = new(@"\(((?:19|20)\d\d)\)", RegexOptions.Compiled);
        private static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex QualityTags = new(
            @"(?i)\b(480p|576p|720p|1080p|2160p|4k|uhd|hdr|bluray|blu-ray|web-?dl|webrip|hdtv|x264|x265|h264|h265|hevc|remux|dvdrip|xvid|brrip|bdrip|proper|extended|unrated|remastered)\b",
            RegexOptions.Compiled);
        private static readonly Regex Episodic = new(@"(?i)\bS\d{1,2}\s*E\d{1,3}\b|\b\d{1,2}x\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>"12 - Title (1984) [1080p]" → ("Title", 1984); null when no (Year) is present or no
        /// title text survives the cleanup.</summary>
        public static (string Title, int Year)? Parse(string? folderLeaf)
        {
            if (string.IsNullOrWhiteSpace(folderLeaf)) return null;
            var s = Ordinal.Replace(folderLeaf, "");
            var y = YearParen.Match(s);
            if (!y.Success) return null;
            var title = s.Substring(0, y.Index);
            title = Brackets.Replace(title, " ");
            title = QualityTags.Replace(title, " ");
            title = MultiSpace.Replace(title, " ").Trim(' ', '.', '-', '_');
            if (title.Length == 0) return null;
            return (title, int.Parse(y.Groups[1].Value));
        }

        /// <summary>Whether a file name carries an episode marker (S01E02 / 1x02) — routed away from
        /// the new-movie lane, since those belong to the series-mapping pipeline.</summary>
        public static bool LooksEpisodic(string? fileName) =>
            !string.IsNullOrEmpty(fileName) && Episodic.IsMatch(fileName);
    }
}
