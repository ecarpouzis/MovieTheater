using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// The durable home of one file's complete ffprobe keyframe list, keyed by CONTENT, not by path
    /// or by Jellyfin item id (.claude/skills/hls-copy-freeze; the 2026-08-13 custody work).
    ///
    /// <para><b>Why this table exists.</b> Jellyfin's own <c>KeyframeData</c> rows cascade-delete with
    /// their items, so a renamed folder physically destroys the lists for everything under it — before
    /// any sync runs — and a server reinstall destroys all of them. The four-day, 25 TB backfill
    /// (backfill-marathon) therefore lived in exactly the database most exposed to routine cleanup.
    /// This table is the master copy; Jellyfin's repository becomes a cache the sync can refill via
    /// the patched <c>ImportKeyframes</c> endpoint whenever a re-point lands the same bytes on a new
    /// item id.</para>
    ///
    /// <para><b>Keyed by <see cref="Fingerprint"/></b> (<c>MediaFile.ContentFingerprint</c>) because a
    /// keyframe list is a property of the encoded bytes and of nothing else: the same bytes under any
    /// name, in any folder, on any item id, cut at the same frames. A row is never invalidated by a
    /// rename; it simply stops being referenced when no file carries its fingerprint any more.</para>
    /// </summary>
    [Table("MediaKeyframes")]
    public class MediaKeyframes
    {
        /// <summary>The content fingerprint the list belongs to (hex SHA-256, see <c>MediaFingerprint</c>).</summary>
        [Key]
        [MaxLength(64)]
        public string Fingerprint { get; set; } = default!;

        /// <summary>Total duration in ticks, as Jellyfin's repository stores beside the list.</summary>
        public long TotalDurationTicks { get; set; }

        /// <summary>The complete keyframe tick list, JSON array — the exact text Jellyfin's
        /// <c>KeyframeData.KeyframeTicks</c> column holds, so import is a byte-faithful round trip.</summary>
        public string KeyframeTicks { get; set; } = default!;

        /// <summary>File size the list was captured against — a cheap sanity check at restore time:
        /// a size mismatch means the fingerprint collided or the row is stale, and refusing beats
        /// re-introducing the exact playlist divergence this data exists to prevent.</summary>
        public long SizeBytes { get; set; }

        /// <summary>The Jellyfin item the list was captured from (provenance only; item ids die with
        /// their paths and are never used for lookup).</summary>
        [MaxLength(64)]
        public string? SourceItemId { get; set; }

        public DateTime CapturedUtc { get; set; }
    }
}
