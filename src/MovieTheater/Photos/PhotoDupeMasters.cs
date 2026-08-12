using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// The one place that answers "which copy represents this photo" (docs/photos-plan.md §2.6).
    ///
    /// <para>§2.6 asks for two things that must never drift apart: browse surfaces COLLAPSE a group to
    /// its master, and tags/dates/captions written against any member REDIRECT to the master. Both are
    /// the same predicate, so both are computed here — a second definition somewhere else is how a
    /// photo ends up collapsed out of the timeline while its tags land on the copy nobody sees.</para>
    ///
    /// <para><b>What collapses.</b> A non-master member of a group that has been settled: any
    /// <see cref="PhotoDupeGroupStatus.Resolved"/> group (a human picked, or a Variant pair that needs
    /// no human — §2.6), plus <see cref="PhotoDupeGroupKind.Exact"/> groups while they wait for
    /// confirmation, because byte-identical files have no judgement left in them. A PENDING
    /// <see cref="PhotoDupeGroupKind.Near"/> group does NOT collapse: nobody has yet agreed those are
    /// the same picture, and hiding half a family's scans on a hash's say-so is exactly the mistake the
    /// review UI exists to prevent. A <see cref="PhotoDupeGroupStatus.Rejected"/> group never collapses
    /// anything — it is a tombstone recording "not the same photo", not a membership.</para>
    ///
    /// <para>Nothing here moves, renames or deletes a file (§6). Collapsing is a WHERE clause.</para>
    /// </summary>
    public static class PhotoDupeMasters
    {
        /// <summary>
        /// Members that a settled group has taken out of browse — the ids the timeline and album pages
        /// subtract. Left as an <see cref="IQueryable{T}"/> so callers compose it into their own page
        /// query as a subquery: materializing it would pull every duplicate id into memory to draw one
        /// screenful.
        /// </summary>
        public static IQueryable<int> CollapsedAssetIds(MovieDb db) =>
            db.PhotoDupeMembers.Where(Collapsed).Select(m => m.PhotoAssetId);

        /// <summary>
        /// The collapse predicate, written ONCE. An <see cref="Expression"/> rather than a method,
        /// because EF cannot translate a call into SQL — and a predicate that only worked in memory
        /// would quietly turn every timeline page into a table scan.
        /// </summary>
        public static readonly Expression<Func<PhotoDupeMember, bool>> Collapsed =
            m => !m.IsMaster
                 && (m.PhotoDupeGroup.Status == PhotoDupeGroupStatus.Resolved
                     || (m.PhotoDupeGroup.Status == PhotoDupeGroupStatus.Pending
                         && m.PhotoDupeGroup.Kind == PhotoDupeGroupKind.Exact));

        /// <summary>
        /// "The master for asset X" for a set of assets — identity when X is ungrouped or is itself the
        /// master, which is the overwhelmingly common case and costs nothing to say.
        ///
        /// <para>Phase 4's tagging routes its writes through this; Phase 3 already routes album-entry
        /// creation through it, so adding a duplicate to an album adds the copy the album will actually
        /// show. Bounded by the caller's id list — never a scan.</para>
        /// </summary>
        public static async Task<Dictionary<int, int>> MasterMapAsync(MovieDb db, IReadOnlyCollection<int> assetIds)
        {
            var map = new Dictionary<int, int>();
            foreach (var id in assetIds) map[id] = id;
            if (assetIds.Count == 0) return map;

            var ids = assetIds.ToList();
            var nonMasters = await db.PhotoDupeMembers
                .Where(Collapsed)
                .Where(m => ids.Contains(m.PhotoAssetId))
                .Select(m => new { m.PhotoAssetId, m.PhotoDupeGroupId })
                .ToListAsync();
            if (nonMasters.Count == 0) return map;

            var groupIds = nonMasters.Select(m => m.PhotoDupeGroupId).Distinct().ToList();
            var masters = await db.PhotoDupeMembers
                .Where(m => groupIds.Contains(m.PhotoDupeGroupId) && m.IsMaster)
                .Select(m => new { m.PhotoDupeGroupId, m.PhotoAssetId })
                .ToListAsync();
            var masterByGroup = masters.ToDictionary(m => m.PhotoDupeGroupId, m => m.PhotoAssetId);

            foreach (var member in nonMasters)
                if (masterByGroup.TryGetValue(member.PhotoDupeGroupId, out var master))
                    map[member.PhotoAssetId] = master;

            return map;
        }

        /// <summary>Single-asset form. Identity when the asset is ungrouped.</summary>
        public static async Task<int> MasterForAsync(MovieDb db, int assetId) =>
            (await MasterMapAsync(db, new[] { assetId }))[assetId];

        // ── Default master pick (§2.6) ───────────────────────────────────────────────────────────

        /// <summary>
        /// §2.6's heuristic, in its stated order: highest resolution → largest file → EXIF-bearing.
        /// The id is the final tie-break, and it is not decoration: without it two identical copies
        /// would swap the master flag between runs, and "re-running the pass changes nothing" — the
        /// property the whole grouping lane is judged on — would be false.
        /// </summary>
        public static PhotoAsset PickMaster(IEnumerable<PhotoAsset> members) =>
            members
                .OrderByDescending(Pixels)
                .ThenByDescending(a => a.SizeBytes)
                .ThenByDescending(a => HasExif(a) ? 1 : 0)
                .ThenBy(a => a.Id)
                .First();

        /// <summary>
        /// The Variant master (§2.6): the DISPLAY half wins outright — the JPEG beside a RAW, the still
        /// beside a motion-photo's video, the .heic beside a Live Photo's .mov — and §2.6's heuristic
        /// only breaks ties within the same half. A RAW is usually the larger, higher-resolution file,
        /// so running the plain heuristic here would master the copy no browser can show.
        /// </summary>
        public static PhotoAsset PickVariantMaster(IEnumerable<PhotoAsset> members) =>
            members
                .OrderByDescending(DisplayRank)
                .ThenByDescending(Pixels)
                .ThenByDescending(a => a.SizeBytes)
                .ThenByDescending(a => HasExif(a) ? 1 : 0)
                .ThenBy(a => a.Id)
                .First();

        private static long Pixels(PhotoAsset a) => (long)(a.Width ?? 0) * (a.Height ?? 0);

        /// <summary>EXIF-bearing, decided from what the metadata pass already persisted — no file is
        /// reopened to answer it (§2.5's persist-the-measurement rule).</summary>
        private static bool HasExif(PhotoAsset a) =>
            a.TakenAtSource == TakenAtSource.Exif
            || a.CameraMake != null
            || (a.RawMetadataJson != null && a.RawMetadataJson.Length > 2);

        /// <summary>2 = a still a browser renders, 1 = a still it does not (RAW, HEIC), 0 = a video.</summary>
        private static int DisplayRank(PhotoAsset a)
        {
            if (a.Kind != PhotoAssetKind.Photo) return 0;
            return a.OriginalRenderable ? 2 : 1;
        }
    }
}
