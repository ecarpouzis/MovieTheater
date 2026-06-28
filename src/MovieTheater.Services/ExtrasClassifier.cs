using System.Text.RegularExpressions;

namespace MovieTheater.Services
{
    /// <summary>
    /// Shared recognition of "extra" content (featurettes, deleted scenes, behind-the-scenes, interviews,
    /// trailers, outtakes…) by THIS library's on-disk convention — used by the file-mapping ingest, the
    /// Jellyfin sync, and the per-movie re-link so all three classify identically.
    ///
    /// The convention: extras live in a SUBFOLDER of the movie whose name CONTAINS an extras keyword, e.g.
    /// "Extras Content", "Featurettes Content", "MST3K Extras", "Deleted Scenes Content". The " Content"
    /// suffix is deliberate — Jellyfin special-cases folders literally named "Extras"/"Featurettes"/etc. and
    /// HIDES their files as special features; renaming to "X Content" makes Jellyfin scan them as ordinary
    /// videos so they stay visible/streamable. So the keyword is matched as a CONTAINS within a path segment,
    /// not anchored to the segment end (that older anchoring missed "Extras Content").
    /// </summary>
    public static class ExtrasClassifier
    {
        // Whole-word extras keyword anywhere in a path segment. Separators in multi-word keywords are loose
        // (space/dot/underscore/dash) to match "Behind.The.Scenes" etc.
        // NB: deliberately NO "shorts" — this library has real short-film content ("Short Subject",
        // "Classic Shorts") that it would wrongly flag; the only extras folder using it ("Shorts and extras")
        // still matches via "extras".
        private static readonly Regex Keyword = new(
            @"(?i)\b(extras?|featurettes?|behind[ ._\-]*the[ ._\-]*scenes|deleted[ ._\-]*scenes?|interviews?|bonus|making[ ._\-]*of|trailers?|outtakes?|gag[ ._\-]*reels?)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// The matched extras keyword if <paramref name="relativePath"/> — a path RELATIVE to the movie's own
        /// folder (never the title folder itself, so a title like "Extra Ordinary" is never mistaken) — puts
        /// the file inside an extras-type SUBFOLDER; else null. FOLDER-only by design: matching the filename
        /// would mis-flag a movie whose title starts with a keyword ("Interview With The Vampire", "Trailer
        /// Park Boys") — the loose-filename extras stay hand-classified, same as the file-mapping ingest.
        /// Backslash-split MANUALLY (prod is Linux; the DB stores Windows-style paths).
        /// </summary>
        public static string? ExtraKeyword(string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            var segs = relativePath.Replace('/', '\\').Trim('\\').Split('\\');
            // Folder segments only (everything but the filename, segs[^1]).
            for (int i = 0; i < segs.Length - 1; i++)
            {
                var m = Keyword.Match(segs[i]);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }
    }
}
