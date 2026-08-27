using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Web
{
    /// <summary>
    /// The two movie-browse memory-cache keys, in one place, so a REQUEST and the out-of-request
    /// <see cref="CatalogWarmupService"/> compute the same string — a warm nobody can hit is worse
    /// than no warm at all.
    ///
    /// The load-bearing fact: <see cref="CatalogQueries"/>' base queries depend on exactly ONE viewer
    /// fact, the age restriction. So the group index and the facet counts are identical for every
    /// viewer at a given age — EXCEPT when the filter reads the caller's own lists (`my=seen,want`),
    /// which is the only user-dependent part of <see cref="BrowseFilter"/>. The key therefore carries
    /// the user id only in that case; every other scope is shared, which is what makes it warmable.
    /// </summary>
    public static class BrowseCacheKeys
    {
        public const string SharedViewer = "any";

        private static string Types(IEnumerable<NormalizedTitleType> typeScope) =>
            string.Join(",", typeScope.OrderBy(t => t));

        /// <summary>The light group index for one scope + group axis.</summary>
        public static string Groups(int? userId, int age, IEnumerable<NormalizedTitleType> typeScope,
            string? mode, string? value, string filterSig, bool userDependent, string groupBy)
        {
            var viewer = userDependent ? (userId?.ToString() ?? "anon") : SharedViewer;
            var v = (value ?? "").Trim().ToLowerInvariant();
            return $"browse:groups:{viewer}:{age}:{Types(typeScope)}:{(mode ?? "").Trim().ToLowerInvariant()}:{v}:{filterSig}:{groupBy}";
        }

        /// <summary>The facet option lists + counts for one scope. Never user-dependent: the counts
        /// pass a null user id to <see cref="BrowseFilter.Apply"/>, so no personal list can reach them.</summary>
        public static string Facets(int age, IEnumerable<NormalizedTitleType> typeScope, string? text) =>
            $"browse:facets:{SharedViewer}:{age}:{Types(typeScope)}:{(text ?? "").Trim().ToLowerInvariant()}";
    }
}
