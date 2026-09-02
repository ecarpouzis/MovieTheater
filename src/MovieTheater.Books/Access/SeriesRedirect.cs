using Microsoft.EntityFrameworkCore;
using MovieTheater.Books.Db;

namespace MovieTheater.Books.Access
{
    /// <summary>
    /// <b>The old-id redirect.</b> <c>books-resolve --series</c> merges minority series onto their survivor and
    /// DELETES the merged-away row, logging <c>SeriesMerge(OldSeriesId → NewSeriesId)</c> so a bookmark, a
    /// shared link or a stale client tab that still names the old id lands on the survivor instead of on an
    /// empty run. Every per-series endpoint resolves through here before it reads; the response carries the id
    /// it actually answered for so the client can repair its URL.
    ///
    /// <para>A chain is followed (a survivor can itself be merged later), bounded so a cyclic log — which the
    /// rebuild never writes, but a hand edit could — cannot spin. An id that still exists is returned as is;
    /// an id with no row and no redirect is returned unchanged and the caller's own not-found path applies.</para>
    /// </summary>
    public static class SeriesRedirect
    {
        public const int MaxHops = 8;

        public static async Task<int> FollowAsync(BooksDb db, int seriesId, CancellationToken ct = default)
        {
            var id = seriesId;
            for (var hop = 0; hop < MaxHops; hop++)
            {
                if (await db.Series.AsNoTracking().AnyAsync(s => s.Id == id, ct)) return id;
                var next = await db.SeriesMerges.AsNoTracking()
                    .Where(m => m.OldSeriesId == id && m.NewSeriesId != null)
                    .Select(m => m.NewSeriesId)
                    .FirstOrDefaultAsync(ct);
                if (next is not int n || n == id) return id;
                id = n;
            }
            return id;
        }
    }
}
