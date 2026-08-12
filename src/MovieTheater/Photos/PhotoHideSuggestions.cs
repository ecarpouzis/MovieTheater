using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The heuristics behind <c>photos-suggest-hide</c> (docs/photos-plan.md §2.9: curation flags are
    /// "auto-suggested for Screenshots/misc folders at ingest, human-confirmed batch-wise").
    ///
    /// <para><b>Every rule here PROPOSES.</b> Nothing in this file writes <c>Hidden</c>, and the pass
    /// that calls it writes a review artifact rather than a flag — §1 named this clutter as wanting
    /// "a hidden from timeline by default curation flag, not deletion", and a flag a human never saw
    /// applied is halfway back to deletion. Each proposal carries the rule that made it, so a rule that
    /// turns out to be wrong shows up as one rejectable cluster instead of scattered mistakes.</para>
    ///
    /// <para><b>Folder words, never folder names.</b> The keyword lists below are generic clutter words
    /// (screenshots, misc, wallpapers…). No family member's name, device, event or personal path
    /// appears here or ever may (§6) — the tree's real folder names live in DB rows only.</para>
    /// </summary>
    public static class PhotoHideSuggestions
    {
        public const string RuleScreenshotFolder = "screenshot-folder";
        public const string RuleScreenshotName = "screenshot-filename";
        public const string RuleMiscFolder = "misc-folder";
        public const string RuleTinyImage = "tiny-image";
        public const string RuleNonPhotoFormat = "non-photo-format";

        public static readonly IReadOnlyList<string> AllRules = new[]
        {
            RuleScreenshotFolder, RuleScreenshotName, RuleMiscFolder, RuleTinyImage, RuleNonPhotoFormat,
        };

        /// <summary>Folder segments that mean "screen capture pile".</summary>
        private static readonly string[] ScreenshotFolderWords = { "screenshot", "screenshots", "screen shot", "screen shots", "screencap", "screencaps", "screen captures" };

        /// <summary>
        /// Folder segments that mean "reference material that is not a family photo". Deliberately does
        /// NOT include "scan"/"scans": scanned prints are the most irreplaceable content in the
        /// collection (§2.6/§2.7 exist for them), and proposing to hide them would be the single worst
        /// suggestion this pass could make.
        /// </summary>
        private static readonly string[] MiscFolderWords =
        {
            "misc", "miscellaneous", "reference", "references", "papercraft", "papercrafts",
            "wallpaper", "wallpapers", "clipart", "clip art", "template", "templates",
            "printable", "printables", "icons", "memes", "receipts", "manuals",
        };

        private static readonly string[] ScreenshotNamePrefixes = { "screenshot", "screen shot", "screen_shot", "screencap", "scrnshot" };

        /// <summary>Graphics containers a camera does not produce. A photo in one of these that ALSO
        /// carries no camera and no capture date is almost always a saved image rather than a family
        /// picture — but "almost always" is why it is a proposal.</summary>
        private static readonly HashSet<string> GraphicExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".gif", ".bmp", ".webp",
        };

        public sealed class Options
        {
            /// <summary>Longest edge below which a still is proposed as too small to be a photograph.
            /// 320 px is under any camera or phone this collection could contain and comfortably above
            /// a scanned wallet print.</summary>
            public int MinEdge = 320;

            /// <summary>File size below which a still is proposed as clutter, used only when the
            /// dimensions are unknown (the metadata pass has not reached it, or could not decode it).</summary>
            public long MinBytes = 20 * 1024;

            /// <summary>Which rules are live. All of them by default; a run can narrow to one so a
            /// single heuristic's proposals can be reviewed and accepted on their own.</summary>
            public HashSet<string> Rules = new HashSet<string>(AllRules, StringComparer.OrdinalIgnoreCase);

            public bool Enabled(string rule) => Rules.Contains(rule);
        }

        /// <summary>
        /// The rule this asset trips, or null for "leave it alone". First match wins and the order is
        /// meaningful: the folder rules are the confident ones and read best in a review list, so a
        /// screenshot inside a screenshots folder is reported as the folder rule rather than as five
        /// overlapping ones.
        /// </summary>
        public static string? Evaluate(PhotoAsset asset, Options options)
        {
            if (asset.Hidden) return null;

            var segments = FolderSegments(asset.Path);

            if (options.Enabled(RuleScreenshotFolder) && segments.Any(s => MatchesWord(s, ScreenshotFolderWords)))
                return RuleScreenshotFolder;

            if (options.Enabled(RuleScreenshotName) && StartsWithAny(FileName(asset.Path), ScreenshotNamePrefixes))
                return RuleScreenshotName;

            if (options.Enabled(RuleMiscFolder) && segments.Any(s => MatchesWord(s, MiscFolderWords)))
                return RuleMiscFolder;

            // The remaining rules are about the PICTURE, so they say nothing about a video.
            if (asset.Kind != PhotoAssetKind.Photo) return null;

            if (options.Enabled(RuleTinyImage) && IsTiny(asset, options))
                return RuleTinyImage;

            if (options.Enabled(RuleNonPhotoFormat)
                && GraphicExtensions.Contains(Extension(asset.Path))
                && string.IsNullOrEmpty(asset.CameraMake)
                && string.IsNullOrEmpty(asset.CameraModel)
                && asset.TakenAtSource != TakenAtSource.Exif
                && asset.TakenAtSource != TakenAtSource.Manual)
                return RuleNonPhotoFormat;

            return null;
        }

        private static bool IsTiny(PhotoAsset asset, Options options)
        {
            if (asset.Width != null && asset.Height != null)
                return Math.Max(asset.Width.Value, asset.Height.Value) < options.MinEdge;

            // Dimensions unknown: size is the only signal, and it is only trusted downward.
            return asset.SizeBytes > 0 && asset.SizeBytes < options.MinBytes;
        }

        /// <summary>Folder segments of a root-relative, forward-slash path — the file name is not one
        /// of them, so a file called "misc.jpg" is not mistaken for a misc folder.</summary>
        private static List<string> FolderSegments(string path)
        {
            var parts = (path ?? "").Split('/');
            var segments = new List<string>();
            for (var i = 0; i < parts.Length - 1; i++)
                if (parts[i].Length > 0) segments.Add(parts[i]);
            return segments;
        }

        private static string FileName(string path)
        {
            var slash = (path ?? "").LastIndexOf('/');
            return slash < 0 ? (path ?? "") : path!.Substring(slash + 1);
        }

        private static string Extension(string path)
        {
            var name = FileName(path);
            var dot = name.LastIndexOf('.');
            return dot < 0 ? "" : name.Substring(dot);
        }

        /// <summary>
        /// Whole-segment word match, not substring: "Misc Pics" and "Screenshots" match, and a folder
        /// whose name merely CONTAINS a keyword inside a longer word does not. Punctuation and case are
        /// folded, because these folders were named by hand over twenty years.
        /// </summary>
        private static bool MatchesWord(string segment, string[] words)
        {
            var normalized = Normalize(segment);
            if (normalized.Length == 0) return false;
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Contains(' '))
                {
                    if (normalized.Contains(word, StringComparison.Ordinal)) return true;
                    continue;
                }
                if (tokens.Contains(word, StringComparer.Ordinal)) return true;
            }
            return false;
        }

        private static bool StartsWithAny(string name, string[] prefixes)
        {
            var normalized = Normalize(name);
            return prefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal));
        }

        private static string Normalize(string value)
        {
            var chars = (value ?? "").ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (chars[i] == '_' || chars[i] == '-' || chars[i] == '.') chars[i] = ' ';
            return new string(chars).Trim();
        }
    }
}
