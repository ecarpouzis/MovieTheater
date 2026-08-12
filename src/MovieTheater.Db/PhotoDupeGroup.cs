using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A set of assets asserted to be the same picture (photos-plan.md §2.6). Resolving a group means
    /// picking a master — a flag on a <see cref="PhotoDupeMember"/> row. NO file is copied, moved,
    /// renamed or deleted, ever; the "merge needed" folder gets merged exactly this way, with the disk
    /// left untouched and the master simply winning the timeline.
    ///
    /// <para>Browse surfaces collapse a group to its master, and tags/dates/captions written against any
    /// member redirect to the master, so dissolving a group or changing masters is a re-point of those
    /// attachments in one transaction — again, nothing on disk.</para>
    /// </summary>
    [Table("PhotoDupeGroup")]
    public class PhotoDupeGroup
    {
        [Key]
        public int Id { get; set; }

        public PhotoDupeGroupKind Kind { get; set; }

        public PhotoDupeGroupStatus Status { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? ResolvedUtc { get; set; }

        /// <summary>Who settled the master. Restrict-deleted for the same reason as everywhere else in
        /// this vertical: a curation record must outlive account housekeeping.</summary>
        public int? ResolvedByUserId { get; set; }

        [ForeignKey(nameof(ResolvedByUserId))]
        public User? ResolvedByUser { get; set; }

        public ICollection<PhotoDupeMember> Members { get; set; } = new List<PhotoDupeMember>();
    }
}
