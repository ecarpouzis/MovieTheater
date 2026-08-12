using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// The one place the movie-side sync learns what the FAMILY photo library is, so it can refuse to
    /// look at it (docs/photos-plan.md §2.3).
    ///
    /// <para><b>Why this ships before the Jellyfin library exists.</b> §2.3 states the order plainly:
    /// "the movie-side <c>sync-jellyfin</c> must exclude the family library … that exclusion ships
    /// BEFORE the Jellyfin library is created". A family video that reaches
    /// <see cref="MovieTheater.Db.MediaFile"/> is not a display bug — it is a home video in the movie
    /// grid, in a channel pool, in the review queue and in a recommendation, and every one of those
    /// surfaces reads MediaFile/Movie rows. Making the row impossible is the only fix that covers all
    /// of them at once.</para>
    ///
    /// <para><b>Path prefix is the primary mechanism, and it works with nothing else configured.</b>
    /// A library id is a fact about a Jellyfin server that may not exist yet, may be recreated, and
    /// which the item listings do not carry per item anyway. A path prefix is a fact about the
    /// collection, and the collection is the thing being protected. The id (when set) contributes its
    /// library's own on-disk locations as ADDITIONAL prefixes — it widens the net, it is never the
    /// net.</para>
    ///
    /// <para><b>Both path forms are tested.</b> Jellyfin reports a UNC/POSIX path while the DB stores a
    /// drive-letter one, so a root configured in either form is expanded through
    /// <see cref="JellyfinPathMapping"/> in both directions and an item is tested as reported AND as
    /// translated. Comparison is <see cref="JellyfinPathMapper.NormalizeForCompare"/>'s — separators
    /// unified, case-insensitive — because that is where every path bug this repo has had was born.</para>
    /// </summary>
    public sealed class JellyfinFamilyExclusion
    {
        /// <summary>Normalized prefixes (no trailing separator). A path is excluded when it EQUALS one
        /// or sits under it — never a bare <c>StartsWith</c>, which would make "…/Photos" exclude a
        /// sibling "…/Photos Extra".</summary>
        private readonly List<string> prefixes;

        private readonly IReadOnlyList<JellyfinPathMapping> mappings;

        private JellyfinFamilyExclusion(List<string> prefixes, IReadOnlyList<JellyfinPathMapping> mappings)
        {
            this.prefixes = prefixes;
            this.mappings = mappings;
        }

        /// <summary>Nothing to exclude — no photo root configured and no library locations supplied.
        /// The sync then behaves exactly as it did before this existed.</summary>
        public static JellyfinFamilyExclusion None { get; } =
            new JellyfinFamilyExclusion(new List<string>(), Array.Empty<JellyfinPathMapping>());

        /// <summary>The prefixes actually in force, for the sync's report line — an exclusion nobody
        /// can see the shape of is one nobody can tell is misconfigured.</summary>
        public IReadOnlyList<string> Prefixes => prefixes;

        public bool Configured => prefixes.Count > 0;

        /// <summary>
        /// Builds the exclusion from the configured photo root plus (optionally) the on-disk locations
        /// of the family Jellyfin library.
        /// </summary>
        /// <param name="photosRoot">
        /// <c>PhotosLibraryDir</c> — the collection root as the CALLING host mounts it. May be a
        /// drive-letter path or a UNC one; both are expanded to the other form through
        /// <paramref name="mappings"/> so an item is caught whichever way Jellyfin reports it.
        /// </param>
        /// <param name="libraryLocations">
        /// The family library's own folder paths, when <c>PhotosJellyfinLibraryId</c> is set and the
        /// server could be asked. Null/empty is the normal state and costs nothing.
        /// </param>
        public static JellyfinFamilyExclusion Build(
            string? photosRoot,
            IReadOnlyList<JellyfinPathMapping>? mappings,
            IEnumerable<string>? libraryLocations = null)
        {
            var maps = mappings ?? Array.Empty<JellyfinPathMapping>();
            var set = new List<string>();

            foreach (var candidate in Roots(photosRoot, libraryLocations))
            {
                // Checked on the CANDIDATE, before expansion: a misconfigured `Q:\` translates to a
                // perfectly deep-looking `\\server\share`, so testing only the expanded forms would let
                // the whole library through the guard that exists to stop exactly that.
                if (!IsMeaningfulRoot(candidate)) continue;
                Add(set, candidate);
                // The same folder as the OTHER side would name it. Both directions are attempted
                // because the root may be configured in either form, and only one of the two can
                // possibly apply to any given string.
                if (JellyfinPathMapper.TryTranslateToJellyfin(candidate, maps, out var jellyfinForm))
                    Add(set, jellyfinForm);
                if (JellyfinPathMapper.TryTranslateToDb(candidate, maps, out var dbForm, out _))
                    Add(set, dbForm);
            }

            return set.Count == 0 ? None : new JellyfinFamilyExclusion(set, maps);
        }

        private static IEnumerable<string> Roots(string? photosRoot, IEnumerable<string>? libraryLocations)
        {
            if (!string.IsNullOrWhiteSpace(photosRoot)) yield return photosRoot!;
            foreach (var location in libraryLocations ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(location)) yield return location;
        }

        /// <summary>
        /// Whether a configured root names a FOLDER rather than a whole volume. A bare drive
        /// (<c>Q:\</c>), a bare share (<c>\\server\share</c>) or an empty string would exclude the
        /// entire library — that is a misconfiguration, not an instruction, and silently emptying the
        /// movie site is a worse outcome than an un-excluded family library. Refused rather than obeyed.
        /// </summary>
        public static bool IsMeaningfulRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = JellyfinPathMapper.NormalizeForCompare(path!);
            var isUnc = normalized.StartsWith(@"\\", StringComparison.Ordinal);
            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            // UNC needs server + share + at least one folder; everything else needs a volume/leading
            // segment plus at least one folder.
            return isUnc ? parts.Length >= 3 : parts.Length >= 2;
        }

        private static void Add(List<string> set, string path)
        {
            var normalized = JellyfinPathMapper.NormalizeForCompare(path);
            if (normalized.Length == 0) return;
            if (!set.Contains(normalized, StringComparer.Ordinal)) set.Add(normalized);
        }

        /// <summary>
        /// Whether a Jellyfin-reported path belongs to the family collection. Tests the path as
        /// reported AND as translated into DB form, so one configured root covers both vocabularies.
        /// A null/empty path is NOT excluded — the sync already handles pathless items, and treating
        /// "unknown" as "family" would silently drop real titles.
        /// </summary>
        public bool IsExcluded(string? jellyfinPath)
        {
            if (prefixes.Count == 0 || string.IsNullOrEmpty(jellyfinPath)) return false;
            if (Under(jellyfinPath!)) return true;
            return JellyfinPathMapper.TryTranslateToDb(jellyfinPath!, mappings, out var dbPath, out _)
                   && Under(dbPath);
        }

        public bool IsExcluded(JellyfinItem item) => IsExcluded(item?.Path);

        private bool Under(string path)
        {
            var normalized = JellyfinPathMapper.NormalizeForCompare(path);
            foreach (var prefix in prefixes)
                if (normalized.Length == prefix.Length
                    ? string.Equals(normalized, prefix, StringComparison.Ordinal)
                    : normalized.StartsWith(prefix + "\\", StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>Filters a fetched item list, reporting how many were dropped. The count is returned
        /// rather than logged here so the sync can put it in its own report beside everything else it
        /// counted.</summary>
        public List<JellyfinItem> Filter(IReadOnlyList<JellyfinItem> items, out int excluded)
        {
            if (prefixes.Count == 0)
            {
                excluded = 0;
                return items as List<JellyfinItem> ?? items.ToList();
            }

            var kept = items.Where(i => !IsExcluded(i.Path)).ToList();
            excluded = items.Count - kept.Count;
            return kept;
        }
    }
}
