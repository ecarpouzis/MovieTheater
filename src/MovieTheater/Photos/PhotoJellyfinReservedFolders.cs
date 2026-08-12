using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Jellyfin's RESERVED extras folder names, and the audit that finds them in the family collection
    /// (docs/photos-plan.md §2.3's ⚠ trap).
    ///
    /// <para><b>What actually happens.</b> These names are special-cased in Jellyfin's CORE folder walk
    /// regardless of the library's collection type: a folder so named is treated as an extras container
    /// for its parent, and in a homevideos library — where there is no parent title to attach an extra
    /// to — its contents get DROPPED. They produce no item, so <c>photos-sync-jellyfin</c> can never
    /// stamp them and the videos inside are unplayable in <c>/photos</c> while remaining perfectly
    /// visible as rows, thumbnails and album members. That gap is invisible without this report, which
    /// is the whole reason it exists.</para>
    ///
    /// <para><b>The list is this library's OWN hard-won one</b>, not a guess: the movie libraries hit
    /// exactly this during the homevideos migration and 64 folders were renamed <c>X</c> → <c>X Content</c>
    /// to escape it. The family tree gets no such treatment — <b>we never rename anything under the
    /// collection root</b> (§6). The audit reports; a human decides between giving the folder its own
    /// library entry path and accepting that it is Jellyfin-invisible.</para>
    ///
    /// <para>Whole-segment match, deliberately. Jellyfin's own test is on the folder NAME, so
    /// "Trailers" is reserved and "Trailers from 2004" is not — and a substring rule here would report
    /// a pile of folders that work fine, which is how a report stops being read.</para>
    /// </summary>
    public static class PhotoJellyfinReservedFolders
    {
        /// <summary>
        /// The reserved names, lower-cased. Ordering is alphabetical for readability only.
        /// </summary>
        /// <remarks>
        /// Verified against this repository's recorded Jellyfin behaviour (the homevideos migration
        /// note: "reserved extras folder names — extras, extra, featurettes, behind the scenes, deleted
        /// scenes, interviews, scenes, samples, shorts, trailers — are special-cased in the CORE folder
        /// walk regardless of collection type"), which is the same set §2.3 names. <c>specials</c> and
        /// the singular <c>sample</c>/<c>trailer</c>/<c>short</c> forms are included as the same
        /// mechanism's near neighbours: a false positive here costs one line in a report a human reads,
        /// while a false negative costs a family video that silently never plays.
        /// </remarks>
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "behind the scenes",
            "deleted scenes",
            "extra",
            "extras",
            "featurette",
            "featurettes",
            "interview",
            "interviews",
            "sample",
            "samples",
            "scene",
            "scenes",
            "short",
            "shorts",
            "specials",
            "trailer",
            "trailers",
        };

        public static IReadOnlyCollection<string> Names => Reserved;

        /// <summary>Whether one path SEGMENT is a reserved folder name.</summary>
        public static bool IsReserved(string? segment) =>
            !string.IsNullOrWhiteSpace(segment) && Reserved.Contains(segment!.Trim());

        /// <summary>
        /// The reserved folder name in a root-relative asset path, or null. Only FOLDER segments are
        /// examined — the filename is never one, because Jellyfin's rule is about directories and a
        /// video called <c>Scenes.mp4</c> indexes perfectly well.
        /// </summary>
        /// <remarks>
        /// The nearest reserved ancestor is returned rather than the outermost: it is the folder a
        /// human would rename or re-point, and reporting <c>Vacation</c> because something twelve
        /// levels down was called <c>Extras</c> would name the wrong folder.
        /// </remarks>
        public static string? ReservedSegment(string? rootRelativePath)
        {
            if (string.IsNullOrEmpty(rootRelativePath)) return null;
            var segments = rootRelativePath!.Replace('\\', '/').Split('/');
            for (var i = segments.Length - 2; i >= 0; i--)
                if (IsReserved(segments[i])) return segments[i];
            return null;
        }

        /// <summary>The folder path (root-relative, forward slashes) whose name is reserved — what the
        /// report groups by, so one collision is one row however many videos sit inside it.</summary>
        public static string? ReservedFolder(string? rootRelativePath)
        {
            if (string.IsNullOrEmpty(rootRelativePath)) return null;
            var segments = rootRelativePath!.Replace('\\', '/').Split('/');
            for (var i = segments.Length - 2; i >= 0; i--)
                if (IsReserved(segments[i]))
                    return string.Join("/", segments.Take(i + 1));
            return null;
        }
    }
}
