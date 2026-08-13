using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Music
{
    /// <summary>
    /// The ONE spelling of "a tracklist in the order a listener expects it" (music-plan.md §2.2).
    /// </summary>
    /// <remarks>
    /// Both columns it sorts on are NULLABLE and SQL Server sorts NULLs FIRST, which is the wrong end
    /// in both cases — and the reason Dan Le Sac vs Scroobius Pip's <i>Angles</i> opened on track 2.
    ///
    /// <para><b>Disc.</b> <see cref="MusicTrack.DiscNo"/> comes from the file's tag and from nowhere
    /// else, so a folder whose files were tagged unevenly ends up with some rows saying disc 1 and
    /// some saying nothing at all. 100 of the library's 2,921 albums are in that state. Ordering on
    /// the raw column puts every untagged file ahead of track 1 — so treat an absent disc as disc 1,
    /// which is what a single-disc album's missing tag means anyway.</para>
    ///
    /// <para><b>Track.</b> An absent track number is the opposite case: nothing is known about where
    /// the file belongs, so it goes at the END of its disc rather than at the front of the album,
    /// and falls back to the file name — which carries the library's own "NN - Title" prefix and is
    /// therefore the next best ordering key there is. Id last, so the order is total and a page
    /// re-fetch can't shuffle two otherwise-identical rows.</para>
    ///
    /// <para>Note this does NOT translate to a numeric-vs-string question: TrackNo/DiscNo are ints
    /// in the database, so the comparison was always numeric. The bug was entirely about nulls.</para>
    /// </remarks>
    public static class MusicTrackOrdering
    {
        /// <summary>Sorts a track query the way a tracklist reads: by disc, then track, then name.</summary>
        public static IOrderedQueryable<MusicTrack> InTrackOrder(this IQueryable<MusicTrack> tracks) =>
            tracks
                .OrderBy(t => t.DiscNo ?? 1)
                .ThenBy(t => t.TrackNo ?? int.MaxValue)
                .ThenBy(t => t.FileName)
                .ThenBy(t => t.Id);
    }
}
