using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Turns a path Jellyfin reports into the ROOT-RELATIVE key <see cref="MovieTheater.Db.PhotoAsset.Path"/>
    /// is stored as (docs/photos-plan.md §2.3, §3 Phase 0 addendum).
    ///
    /// <para><b>Jellyfin answers with absolute paths; our table holds relative ones.</b> The Phase 0
    /// decision — root-relative, forward slashes — is what lets the gateway resolve the same string
    /// against its own mount, and it is the reason this translation exists at all rather than the sync
    /// simply comparing strings the way the movie sync does against <c>Movie.FilePath</c>.</para>
    ///
    /// <para><b>Both vocabularies, always.</b> The same folder is <c>L:\…</c> to this host and
    /// <c>\\server\share\…</c> to Jellyfin, and which one a given deployment reports is not knowable
    /// from here. So the root is expanded into every form the configured
    /// <see cref="JellyfinPathMapping"/>s can express, an incoming path is additionally translated into
    /// DB form, and a match on ANY pairing wins. Comparison is
    /// <see cref="JellyfinPathMapper.NormalizeForCompare"/>'s — separators unified, case-insensitive.</para>
    ///
    /// <para>A path outside every root form returns null. That is a reportable fact ("Jellyfin knows a
    /// file the album does not"), never a guess: mapping the wrong photograph would attach a family
    /// video to somebody else's row.</para>
    /// </summary>
    public sealed class PhotoJellyfinPaths
    {
        /// <summary>Normalized root forms, longest first, so a nested root wins over its parent.</summary>
        private readonly List<string> roots;

        private readonly IReadOnlyList<JellyfinPathMapping> mappings;

        private PhotoJellyfinPaths(List<string> roots, IReadOnlyList<JellyfinPathMapping> mappings)
        {
            this.roots = roots;
            this.mappings = mappings;
        }

        public IReadOnlyList<string> Roots => roots;

        public bool Configured => roots.Count > 0;

        /// <summary>
        /// Builds the translator from the collection root as this host mounts it plus the configured
        /// prefix mappings. <paramref name="extraRoots"/> carries the family Jellyfin library's own
        /// folder locations when they are known — a library whose folders are SUBTREES of the
        /// collection (§2.3 allows exactly that) reports paths that are still under the root, so this
        /// is additive rather than necessary.
        /// </summary>
        public static PhotoJellyfinPaths Build(string? photosRoot, IReadOnlyList<JellyfinPathMapping>? mappings,
            IEnumerable<string>? extraRoots = null)
        {
            var maps = mappings ?? Array.Empty<JellyfinPathMapping>();
            var set = new List<string>();

            foreach (var candidate in Candidates(photosRoot, extraRoots))
            {
                // Checked on the CANDIDATE before expansion, the JellyfinFamilyExclusion rule: a bare
                // drive translates to a deep-looking share, so a guard applied only to the expanded
                // forms would let a whole volume through.
                if (!JellyfinFamilyExclusion.IsMeaningfulRoot(candidate)) continue;
                Add(set, candidate);
                if (JellyfinPathMapper.TryTranslateToJellyfin(candidate, maps, out var jellyfinForm))
                    Add(set, jellyfinForm);
                if (JellyfinPathMapper.TryTranslateToDb(candidate, maps, out var dbForm, out _))
                    Add(set, dbForm);
            }

            // Longest first: with both "L:\7" and "L:\7\Video" configured, the deeper root is the one
            // that produces the correct relative key, and taking the shallower one would silently
            // prefix every path with a folder the table does not carry.
            set.Sort((a, b) => b.Length.CompareTo(a.Length));
            return new PhotoJellyfinPaths(set, maps);
        }

        private static IEnumerable<string> Candidates(string? photosRoot, IEnumerable<string>? extraRoots)
        {
            if (!string.IsNullOrWhiteSpace(photosRoot)) yield return photosRoot!;
            foreach (var root in extraRoots ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(root)) yield return root!;
        }

        private static void Add(List<string> set, string path)
        {
            var normalized = JellyfinPathMapper.NormalizeForCompare(path);
            if (normalized.Length == 0) return;
            if (!set.Contains(normalized, StringComparer.Ordinal)) set.Add(normalized);
        }

        /// <summary>
        /// The root-relative, forward-slashed key for an absolute path, or null when it lies outside
        /// every known form of the collection root.
        /// </summary>
        /// <remarks>
        /// The ORIGINAL casing is preserved from the incoming path: the comparison is
        /// case-insensitive, but <c>PhotoAsset.Path</c> was written from a filesystem walk and the
        /// lookup that follows must be able to match it exactly on a case-sensitive server collation.
        /// </remarks>
        public string? ToRootRelative(string? absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || roots.Count == 0) return null;

            var direct = Relative(absolutePath!);
            if (direct != null) return direct;

            // The path as the DB side would name it — the case where the root is configured in
            // drive-letter form and Jellyfin reported the UNC one (or the reverse).
            return JellyfinPathMapper.TryTranslateToDb(absolutePath!, mappings, out var dbPath, out _)
                ? Relative(dbPath)
                : null;
        }

        private string? Relative(string absolutePath)
        {
            var unified = absolutePath.Replace('/', '\\').TrimEnd('\\');
            var normalized = unified.ToLowerInvariant();
            foreach (var root in roots)
            {
                if (normalized.Length <= root.Length) continue;
                if (!normalized.StartsWith(root + "\\", StringComparison.Ordinal)) continue;
                return unified.Substring(root.Length + 1).Replace('\\', '/');
            }
            return null;
        }
    }
}
