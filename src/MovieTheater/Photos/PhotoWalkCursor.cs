using System;
using System.Collections.Generic;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The inventory walk's ordering (photos-plan.md §2.5 phase 1 + §6): the cursor is the last
    /// COMPLETED directory's root-relative path, and the enumeration that resumes after it must page in
    /// EXACTLY that order. Both are defined by the one key function below, so they cannot drift — the
    /// cheats-import cursor bug was precisely a cursor ordered one way and a query ordered another,
    /// which silently skips everything between the two orderings.
    ///
    /// <para><b>The audit (why a plain ordinal compare of the paths is wrong).</b> Take the directories
    /// <c>a</c>, <c>a b</c> and <c>a/z</c>. Ordinal string order is <c>a</c> &lt; <c>a b</c> &lt;
    /// <c>a/z</c>, because space (0x20) sorts below slash (0x2F). A depth-first walk visits <c>a</c>,
    /// then <c>a/z</c>, then <c>a b</c>. Those are DIFFERENT sequences, so a depth-first enumeration
    /// resumed with an ordinal cursor skips <c>a b</c> permanently, and it does so only for trees that
    /// happen to contain a space-bearing sibling — which this collection is full of.</para>
    ///
    /// <para>So the order is defined once, by <see cref="SortKey"/>: the path separator replaced by
    /// U+0000, which sorts below every character that can legally appear in a file name (space very
    /// much included). Ordering by that key IS depth-first with ordinal-sorted children — a directory
    /// precedes its own contents, its contents precede every later sibling — and the resume comparison
    /// uses the same key, so "same ordering" holds by construction rather than by inspection.</para>
    ///
    /// <para>The cursor is a PATH, not an index: directories appearing or vanishing between runs shift
    /// every index but leave the path ordering intact, so a resume after a real change to the tree
    /// still starts exactly where it stopped.</para>
    /// </summary>
    public static class PhotoWalkCursor
    {
        /// <summary>Sorts below every character legal in a path segment.</summary>
        private const char SeparatorKey = '\0';

        /// <summary>The one key both the ordering and the cursor comparison are defined in terms of.</summary>
        public static string SortKey(string relativeDirectory) =>
            relativeDirectory.Replace('/', SeparatorKey);

        public static readonly IComparer<string> Comparer = new WalkComparer();

        /// <summary>True when <paramref name="relativeDirectory"/> comes strictly after the cursor —
        /// i.e. it still has to be done. An empty cursor means "start at the root", and the root itself
        /// is the empty relative path, which is why the root is only ever visited when the cursor is
        /// null rather than empty.</summary>
        public static bool IsAfter(string relativeDirectory, string? cursor) =>
            cursor == null || string.CompareOrdinal(SortKey(relativeDirectory), SortKey(cursor)) > 0;

        private sealed class WalkComparer : IComparer<string>
        {
            public int Compare(string? x, string? y) =>
                string.CompareOrdinal(SortKey(x ?? string.Empty), SortKey(y ?? string.Empty));
        }
    }
}
