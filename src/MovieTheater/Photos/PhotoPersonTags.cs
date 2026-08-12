using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Db;

namespace MovieTheater.Photos
{
    /// <summary>
    /// Every write to <see cref="PhotoPersonTag"/> goes through here (docs/photos-plan.md §2.8).
    ///
    /// <para><b>Tags attach to the group MASTER</b> (§2.6: "tagging or dating any member redirects the
    /// write to the master … browse surfaces collapse to masters, so one tagging pass covers every
    /// copy"). The redirect is <see cref="PhotoDupeMasters.MasterMapAsync"/> — the same predicate that
    /// decides what browse collapses — so "the copy the timeline shows" and "the copy the tag lands on"
    /// can never be different photographs. A second tagging path that forgot the redirect is exactly how
    /// a family's tags end up on the copies nobody sees, which is why the controller, the batch action
    /// and the Immich sync all call this and none of them touch the table directly.</para>
    ///
    /// <para><b>Nothing here auto-confirms anything</b> (§2.8). A suggestion is a
    /// <see cref="PhotoTagSource.Suggested"/> row a human promotes or refuses; a refusal becomes a
    /// <see cref="PhotoTagSource.Rejected"/> tombstone so the next sync does not propose it again. A
    /// human's Manual/Confirmed tag is never downgraded, overwritten or duplicated by a machine.</para>
    /// </summary>
    public static class PhotoPersonTags
    {
        /// <summary>The sources that COUNT as "this person is in this photo" — what person pages, tag
        /// counts and co-occurrence read. A Suggested row is a question, and a Rejected row is an
        /// answered one; neither is a tag.</summary>
        public static bool IsAffirmed(PhotoTagSource source) =>
            source == PhotoTagSource.Manual || source == PhotoTagSource.Confirmed;

        /// <summary>The same rule as an EF expression, written once so a query and an in-memory check
        /// cannot drift.</summary>
        public static IQueryable<PhotoPersonTag> Affirmed(IQueryable<PhotoPersonTag> tags) =>
            tags.Where(t => t.Source == PhotoTagSource.Manual || t.Source == PhotoTagSource.Confirmed);

        public sealed class TagWriteResult
        {
            /// <summary>New rows written.</summary>
            public int Added;

            /// <summary>Existing rows a human's action promoted (a Suggested or Rejected row becoming
            /// Manual, say). Counted separately from <see cref="Added"/> because "you tagged six and got
            /// two new rows" needs the other four explained.</summary>
            public int Promoted;

            /// <summary>Rows that already said what was being asked. Not a failure — an idempotent
            /// re-post of the same tag is the normal shape of a keyboard-driven queue.</summary>
            public int Unchanged;

            /// <summary>How many of the caller's asset ids were redirected to a group master (§2.6).
            /// REPORTED rather than silent: a member who tagged three copies and sees one tag is owed
            /// the reason.</summary>
            public int RedirectedToMasters;

            /// <summary>Ids that named no live asset — a stale tab, a row the walk lost.</summary>
            public int Missing;
        }

        /// <summary>
        /// Tags a set of assets with one person, by hand.
        ///
        /// <para>Idempotent per (master asset, person): a second call adds nothing. An existing
        /// Suggested row is PROMOTED rather than duplicated — the suggestion and the confirmation are
        /// the same row transitioning, which is the whole reason <see cref="PhotoTagSource"/> is a
        /// state and not two tables. A Rejected tombstone is likewise promoted: a human overruling
        /// their own earlier refusal is allowed, and it must not leave two rows behind.</para>
        /// </summary>
        public static async Task<TagWriteResult> AddAsync(MovieDb db, IReadOnlyCollection<int> assetIds,
            int familyPersonId, PhotoTagSource source = PhotoTagSource.Manual)
        {
            var result = new TagWriteResult();
            if (assetIds.Count == 0) return result;

            var targets = await ResolveTargetsAsync(db, assetIds, result);
            if (targets.Count == 0) return result;

            var existing = await db.PhotoPersonTags
                .Where(t => t.FamilyPersonId == familyPersonId && targets.Contains(t.PhotoAssetId))
                .ToListAsync();
            var byAsset = existing.ToDictionary(t => t.PhotoAssetId);

            var now = DateTime.UtcNow;
            foreach (var assetId in targets)
            {
                if (byAsset.TryGetValue(assetId, out var row))
                {
                    if (row.Source == source) { result.Unchanged++; continue; }
                    row.Source = source;
                    row.ConfirmedUtc = IsAffirmed(source) ? now : null;
                    result.Promoted++;
                    continue;
                }

                db.PhotoPersonTags.Add(new PhotoPersonTag
                {
                    PhotoAssetId = assetId,
                    FamilyPersonId = familyPersonId,
                    Source = source,
                    CreatedUtc = now,
                    ConfirmedUtc = IsAffirmed(source) ? now : null,
                });
                result.Added++;
            }

            if (result.Added > 0 || result.Promoted > 0) await db.SaveChangesAsync();
            return result;
        }

        /// <summary>
        /// Removes a person's tag from a set of assets — the redirect applies here too, so untagging the
        /// copy you are looking at removes the tag that actually exists.
        ///
        /// <para>A DELETE, not a Rejected tombstone: "I tagged the wrong person" is not the same
        /// statement as "the recognizer's guess is wrong", and recording the first as the second would
        /// permanently bar a machine from ever proposing a person who really is in the picture.</para>
        /// </summary>
        public static async Task<int> RemoveAsync(MovieDb db, IReadOnlyCollection<int> assetIds, int familyPersonId)
        {
            if (assetIds.Count == 0) return 0;
            var result = new TagWriteResult();
            var targets = await ResolveTargetsAsync(db, assetIds, result);
            if (targets.Count == 0) return 0;

            var rows = await db.PhotoPersonTags
                .Where(t => t.FamilyPersonId == familyPersonId && targets.Contains(t.PhotoAssetId))
                .ToListAsync();
            if (rows.Count == 0) return 0;

            db.PhotoPersonTags.RemoveRange(rows);
            await db.SaveChangesAsync();
            return rows.Count;
        }

        /// <summary>Promotes a suggestion to <see cref="PhotoTagSource.Confirmed"/> — one keystroke in
        /// the tag queue. A row that is already a human's tag is left alone and reported unchanged.</summary>
        public static async Task<PhotoPersonTag?> ConfirmAsync(MovieDb db, int tagId)
        {
            var tag = await db.PhotoPersonTags.FirstOrDefaultAsync(t => t.Id == tagId);
            if (tag == null) return null;
            if (IsAffirmed(tag.Source)) return tag;

            tag.Source = PhotoTagSource.Confirmed;
            tag.ConfirmedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return tag;
        }

        /// <summary>
        /// Refuses a suggestion. The row SURVIVES as a <see cref="PhotoTagSource.Rejected"/> tombstone —
        /// deleting it would let the next sync re-propose the identical face, and a queue that re-asks
        /// an answered question is a queue nobody opens (§2.4/§2.8).
        ///
        /// <para>A human's own Manual/Confirmed tag is never rejected by this path: that is an untag, and
        /// it goes through <see cref="RemoveAsync"/> so it does not leave a tombstone barring the person
        /// from the photograph forever.</para>
        /// </summary>
        public static async Task<PhotoPersonTag?> RejectAsync(MovieDb db, int tagId)
        {
            var tag = await db.PhotoPersonTags.FirstOrDefaultAsync(t => t.Id == tagId);
            if (tag == null) return null;
            if (IsAffirmed(tag.Source)) return tag;

            tag.Source = PhotoTagSource.Rejected;
            tag.ConfirmedUtc = null;
            tag.Confidence = null;
            await db.SaveChangesAsync();
            return tag;
        }

        /// <summary>
        /// Writes a SUGGESTION from the sidecar (§2.4), obeying the no-clobber rules that make Immich
        /// safe to run against irreplaceable curation:
        ///
        /// <list type="bullet">
        /// <item>never overwrite or duplicate an existing Manual/Confirmed tag for the same (asset,
        /// person) — the human already answered, and a machine re-asking would un-answer it;</item>
        /// <item>never revive a Rejected tombstone — that is the "do not propose this again" record;</item>
        /// <item>refresh an existing Suggested row's box/confidence in place rather than adding a second
        /// one, so re-running the sync converges instead of accumulating.</item>
        /// </list>
        ///
        /// <para>Returns which of those happened, so the pass can report it per chunk.</para>
        /// </summary>
        public static async Task<string> SuggestAsync(MovieDb db, int assetId, int familyPersonId,
            string? immichPersonId, double? confidence, double? boxX, double? boxY, double? boxW, double? boxH)
        {
            var master = await PhotoDupeMasters.MasterForAsync(db, assetId);
            var existing = await db.PhotoPersonTags
                .FirstOrDefaultAsync(t => t.PhotoAssetId == master && t.FamilyPersonId == familyPersonId);

            if (existing != null)
            {
                if (IsAffirmed(existing.Source)) return "suggestion-skipped-human-tag";
                if (existing.Source == PhotoTagSource.Rejected) return "suggestion-skipped-rejected";

                existing.Confidence = confidence;
                existing.BoxX = boxX;
                existing.BoxY = boxY;
                existing.BoxW = boxW;
                existing.BoxH = boxH;
                existing.ImmichPersonId = immichPersonId;
                return "suggestions-refreshed";
            }

            db.PhotoPersonTags.Add(new PhotoPersonTag
            {
                PhotoAssetId = master,
                FamilyPersonId = familyPersonId,
                Source = PhotoTagSource.Suggested,
                Confidence = confidence,
                BoxX = boxX,
                BoxY = boxY,
                BoxW = boxW,
                BoxH = boxH,
                ImmichPersonId = immichPersonId,
                CreatedUtc = DateTime.UtcNow,
            });
            return "suggestions-added";
        }

        /// <summary>
        /// Moves every tag from one person onto another and deletes the emptied person — how an unnamed
        /// Immich cluster gets MAPPED onto a family member who already exists (§2.8).
        ///
        /// <para>Collisions are resolved in the target's favour and the strongest state wins: a Manual
        /// tag on the target is not weakened by a Suggested one arriving from the cluster, and a
        /// Rejected tombstone on the target is not revived. Merging must never be able to lose a
        /// human's answer.</para>
        /// </summary>
        public static async Task<(int Moved, int Dropped)> MergePersonAsync(MovieDb db, int fromPersonId, int intoPersonId)
        {
            if (fromPersonId == intoPersonId) return (0, 0);

            var moving = await db.PhotoPersonTags.Where(t => t.FamilyPersonId == fromPersonId).ToListAsync();
            if (moving.Count == 0) return (0, 0);

            var assetIds = moving.Select(t => t.PhotoAssetId).Distinct().ToList();
            var target = await db.PhotoPersonTags
                .Where(t => t.FamilyPersonId == intoPersonId && assetIds.Contains(t.PhotoAssetId))
                .ToListAsync();
            var byAsset = target.ToDictionary(t => t.PhotoAssetId);

            var moved = 0;
            var dropped = 0;
            foreach (var tag in moving)
            {
                if (byAsset.TryGetValue(tag.PhotoAssetId, out var already))
                {
                    // Rank: Manual/Confirmed > Rejected > Suggested. The target keeps the stronger claim.
                    if (Rank(tag.Source) > Rank(already.Source))
                    {
                        already.Source = tag.Source;
                        already.ConfirmedUtc = tag.ConfirmedUtc;
                    }
                    db.PhotoPersonTags.Remove(tag);
                    dropped++;
                    continue;
                }
                tag.FamilyPersonId = intoPersonId;
                byAsset[tag.PhotoAssetId] = tag;
                moved++;
            }
            await db.SaveChangesAsync();
            return (moved, dropped);
        }

        /// <summary>
        /// How strong a claim a tag source makes: Manual/Confirmed &gt; Rejected &gt; Suggested. The
        /// authority for every "which of these two rows wins" decision about a person tag.
        ///
        /// <para><b>Rejected outranks Suggested, and that is the whole point of the table.</b> A
        /// rejection is a TOMBSTONE (§2.4): the row survives specifically so the next Immich sync does
        /// not propose the identical face again. Ranking it at or below Suggested means a tombstone can
        /// never be applied over the suggestion it was written to bury — the refusal silently does
        /// nothing, and the queue re-asks a question a human already answered.</para>
        ///
        /// <para>Public because the curation IMPORTER needs the same answer. It used to carry its own
        /// three-line copy that put Rejected at 0, tied with Suggested, so restoring an export into a
        /// rebuilt database dropped every rejection in it — the §2.11 round trip losing exactly the
        /// decisions it exists to preserve. One table, one place.</para>
        /// </summary>
        public static int Rank(PhotoTagSource source) => source switch
        {
            PhotoTagSource.Manual => 3,
            PhotoTagSource.Confirmed => 3,
            PhotoTagSource.Rejected => 2,
            _ => 1,
        };

        /// <summary>The distinct MASTER ids a caller's asset ids resolve to, dropping ids that name no
        /// row. Bounded by the caller's list — never a scan.</summary>
        private static async Task<List<int>> ResolveTargetsAsync(MovieDb db, IReadOnlyCollection<int> assetIds,
            TagWriteResult result)
        {
            var live = await db.PhotoAssets.Where(a => assetIds.Contains(a.Id)).Select(a => a.Id).ToListAsync();
            result.Missing = assetIds.Distinct().Count() - live.Count;
            if (live.Count == 0) return new List<int>();

            var masters = await PhotoDupeMasters.MasterMapAsync(db, live);
            result.RedirectedToMasters = live.Count(id => masters.TryGetValue(id, out var m) && m != id);
            return live.Select(id => masters.TryGetValue(id, out var m) ? m : id).Distinct().ToList();
        }
    }
}
