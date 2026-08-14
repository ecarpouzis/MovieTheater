using System;
using System.Text.RegularExpressions;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// Reads a movie identity out of an on-disk folder name for the sync's candidate classification.
    /// This library's convention (enforced by the download-sorting pipeline) is
    /// <c>[NN - ]Title (Year)[ tags]</c>, so a parse only succeeds when a parenthesized year is
    /// present — a folder without one is not confidently a movie folder and stays unclassified.
    ///
    /// <para>It also carries the episodic half the series lane needs: whether a name is an episode,
    /// which season/episode it is, whether a folder is a season/release container to climb past, and
    /// the title/year(-range) of a series folder.</para>
    /// </summary>
    public static class MovieFolderParser
    {
        private static readonly Regex Ordinal = new(@"^\s*\d{1,3}[a-z]?\s*-\s*", RegexOptions.Compiled);
        private static readonly Regex YearParen = new(@"\(((?:19|20)\d\d)\)", RegexOptions.Compiled);
        private static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex QualityTags = new(
            @"(?i)\b(480p|540p|576p|720p|1080p|2160p|4k|uhd|hdr|bluray|blu-ray|web-?dl|webrip|hdtv|x264|x265|h264|h265|hevc|remux|dvdrip|xvid|brrip|bdrip|proper|extended|unrated|remastered)\b",
            RegexOptions.Compiled);
        private static readonly Regex Episodic = new(@"(?i)\bS\d{1,2}\s*[.\-_ ]?\s*E\d{1,3}\b|\b\d{1,2}x\d{1,3}\b", RegexOptions.Compiled);
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

        /// <summary>Whether a file name carries an episode marker (S01E02 / 1x02).</summary>
        public static bool LooksEpisodic(string? fileName) =>
            !string.IsNullOrEmpty(fileName) && Episodic.IsMatch(fileName);

        // ── Video-file gate ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Container extensions that are actually a VIDEO. Jellyfin enumerates a DVD rip's sidecars
        /// (.ifo/.bup) as library items too, and those sailed into the upgrade lane as "same-folder"
        /// replacements for the movie whose .avi sits beside them — approving one would re-point a
        /// working title at a 60 KB index file. Anything not on this list can still surface as an
        /// unclassified oddity; it may never be offered as an upgrade or resolved as a new title.
        /// </summary>
        private static readonly string[] VideoExtensions =
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mpg", ".mpeg", ".m2v", ".mov", ".wmv", ".flv",
            ".ts", ".m2ts", ".mts", ".divx", ".ogm", ".ogv", ".rm", ".rmvb", ".vob", ".iso",
            ".webm", ".asf", ".3gp", ".mpv", ".dv", ".f4v",
        };

        /// <summary>Whether the path's extension names a video container (see <see cref="VideoExtensions"/>).</summary>
        public static bool IsVideoFile(string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName)) return false;
            var s = pathOrName.Replace('/', '\\').TrimEnd('\\');
            var dot = s.LastIndexOf('.');
            var slash = s.LastIndexOf('\\');
            if (dot < 0 || dot < slash) return false;
            var ext = s.Substring(dot);
            foreach (var e in VideoExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── Episode identity ──────────────────────────────────────────────────────────────────────

        // "S01E02" / "S1.E2" / "s01 e02", and the older "1x02". Season 0 is legal (Specials).
        private static readonly Regex SxxExx = new(
            @"(?i)\bS(?<s>\d{1,2})\s*[.\-_ ]?\s*E(?<e>\d{1,3})(?:\s*[-–]\s*E?(?<e2>\d{1,3}))?\b", RegexOptions.Compiled);
        private static readonly Regex NxNN = new(
            @"(?<![\d.])(?<s>\d{1,2})x(?<e>\d{1,3})(?:\s*[-–]\s*(?<e2>\d{1,3}))?(?![\d])", RegexOptions.Compiled);

        /// <summary>
        /// Season/episode from a file name. <c>Spans</c> is the last episode of a multi-episode file
        /// ("S01E01-E02" → Episode 1, Spans 2) and equals <c>Episode</c> for the normal single case —
        /// the caller must decide what to do with a file covering two episodes rather than silently
        /// mapping it to the first.
        /// </summary>
        public static (int Season, int Episode, int Spans)? ParseEpisode(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var m = SxxExx.Match(fileName);
            if (!m.Success) m = NxNN.Match(fileName);
            if (!m.Success) return null;
            if (!int.TryParse(m.Groups["s"].Value, out var s) || !int.TryParse(m.Groups["e"].Value, out var e))
                return null;
            var spans = e;
            if (m.Groups["e2"].Success && int.TryParse(m.Groups["e2"].Value, out var e2) && e2 > e && e2 - e < 20)
                spans = e2;
            return (s, e, spans);
        }

        // ── Series folder structure ───────────────────────────────────────────────────────────────

        // A folder that holds episodes but is NOT the series itself: "Season 3", "Season 10 1080p",
        // "Specials", "Extras", a scene release dir carrying an .S0n. token
        // ("Nick.Arcade.S01.540p.PMTP.WEB-DL...-BTN"), or a nested "…Voyager Season 5".
        private static readonly Regex SeasonFolder = new(
            @"(?i)^(season|series|specials?|extras?|saison|staffel)\b|(?i)\bseason\s*\d{1,2}\b|(?i)(?:^|[.\s_\-])S\d{2}(?:[.\s_\-]|$)",
            RegexOptions.Compiled);

        // Structural library folders the climb must never walk into: the alpha/section buckets and the
        // top-level category dirs. Without this, a series stored as one flat release folder directly
        // under "…\Series\" would climb up and group every unrelated show into a single bogus card.
        private static readonly Regex ContainerRoot = new(
            @"(?i)^(!?(series|anime|movies|video|tv|shows|misc|documentaries|cartoons|kids)|\d+\s*-\s*.*|[a-z0-9#]|.:\\?)$",
            RegexOptions.Compiled);

        /// <summary>Whether a folder leaf is a season/release container to climb past when looking for
        /// the series root.</summary>
        public static bool IsSeasonFolder(string? leaf) =>
            !string.IsNullOrWhiteSpace(leaf) && SeasonFolder.IsMatch(leaf);

        /// <summary>Whether a folder leaf is a structural library container that is never a series.</summary>
        public static bool IsContainerRoot(string? leaf) =>
            !string.IsNullOrWhiteSpace(leaf) && ContainerRoot.IsMatch(leaf.Trim());

        /// <summary>Last path segment, extension INTACT — a folder name may legitimately contain dots
        /// ("Nick.Arcade.S01.540p…-BTN"), and trimming after the last one mangles it.</summary>
        private static string Segment(string path)
        {
            var s = path.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i < 0 ? s : s.Substring(i + 1);
        }

        private static string? Parent(string? p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            var s = p.Replace('/', '\\').TrimEnd('\\');
            var i = s.LastIndexOf('\\');
            return i <= 0 ? null : s.Substring(0, i);
        }

        /// <summary>
        /// Climbs from a file to the folder that represents its SERIES — past season folders
        /// ("Season 3", "Specials") and scene release dirs ("Nick.Arcade.S01.540p…-BTN"), stopping at
        /// the first folder that is neither.
        ///
        /// <para>Two guards keep the climb honest. It never walks INTO a structural library container,
        /// so a show stored as one flat release folder directly under "…\Series\" is its own root
        /// rather than dragging every unrelated show on the shelf into one group; and it is capped at
        /// three levels, so no pathological path walks to the drive letter. A nested show
        /// ("…\Star Trek (1966)\Voyager\…Voyager Season 5\") correctly roots at Voyager, because
        /// "Voyager" is not a season folder and the climb stops there.</para>
        /// </summary>
        public static string? SeriesRootOf(string? filePath)
        {
            var root = Parent(filePath);
            if (root == null) return null;
            for (int i = 0; i < 3; i++)
            {
                var parent = Parent(root);
                if (parent == null) break;
                if (IsContainerRoot(Segment(parent))) break;
                if (!IsSeasonFolder(Segment(root))) break;
                root = parent;
            }
            return root;
        }

        /// <summary>The series folder's own name — what <see cref="ParseSeriesFolder"/> reads.</summary>
        public static string SeriesFolderLeaf(string path) => Segment(path);

        private static readonly Regex YearRange = new(
            @"\(((?:19|20)\d\d)\s*(?:[-–]\s*((?:19|20)\d\d)?)?\)", RegexOptions.Compiled);

        /// <summary>
        /// "SpongeBob SquarePants (1999-2020)" → ("SpongeBob SquarePants", 1999). Unlike
        /// <see cref="Parse"/> a series folder may carry a year RANGE or no year at all — the folder
        /// name is the only identity a never-ingested series has, so a missing year is not fatal here.
        /// </summary>
        public static (string Title, int? Year)? ParseSeriesFolder(string? folderLeaf)
        {
            if (string.IsNullOrWhiteSpace(folderLeaf)) return null;
            var s = Ordinal.Replace(folderLeaf, "");
            int? year = null;
            var y = YearRange.Match(s);
            if (y.Success)
            {
                year = int.Parse(y.Groups[1].Value);
                s = s.Substring(0, y.Index);
            }
            s = Brackets.Replace(s, " ");
            s = QualityTags.Replace(s, " ");
            s = MultiSpace.Replace(s, " ").Trim(' ', '.', '-', '_');
            return s.Length == 0 ? null : (s, year);
        }
    }
}
