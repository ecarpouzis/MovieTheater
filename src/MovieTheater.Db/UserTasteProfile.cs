using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// A snapshot of one user's learned taste profile from the last <c>compute-recommendations</c> run —
    /// the signature features, slider preferences and acclaim-affinity the engine derived. Kept for
    /// three reasons: (1) a cheap <see cref="RatingsStamp"/> staleness check so the background refresh
    /// can skip users whose ratings and the library haven't changed (the resumability/idempotency
    /// signal), (2) explanations / a future "your taste" surface, and (3) debugging the algorithm.
    /// </summary>
    [Table("UserTasteProfile")]
    [Index(nameof(UserId), IsUnique = true)]
    public class UserTasteProfile
    {
        [Key]
        public int Id { get; set; }

        public int UserId { get; set; }

        /// <summary>Serialized taste profile (top signature features, slider centers, acclaim affinity).</summary>
        public string? ProfileJson { get; set; }

        /// <summary>Fingerprint of the inputs this profile was built from — the user's max
        /// <see cref="Viewing.ViewingID"/> + rating count, the library's max title id, and the algo
        /// version. When it matches, nothing has changed and the user can be skipped.</summary>
        [MaxLength(128)]
        public string? RatingsStamp { get; set; }

        public DateTime GeneratedUtc { get; set; }
    }
}
