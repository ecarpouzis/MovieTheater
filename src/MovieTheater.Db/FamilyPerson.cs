using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A person who appears in family photos (photos-plan.md §2.8). Deliberately its OWN table and not
    /// the IMDb <see cref="Person"/> credits table: that one is populated by scrapers, joined into
    /// browse and search, and shared with every site surface — exactly what §6's privacy invariant
    /// forbids for family data. The two are never mixed.
    ///
    /// <para>Names live in rows here and nowhere else — never hardcoded in code, comments or seed
    /// data (§6).</para>
    /// </summary>
    [Table("FamilyPerson")]
    public class FamilyPerson
    {
        [Key]
        public int Id { get; set; }

        [MaxLength(200)]
        public string Name { get; set; } = default!;

        /// <summary>Optional. Feeds the date-estimation HINT for undated scans (§2.7): a tagged subject
        /// born in year N implies the photo is not older than N. Surfaced to the human as bounds to
        /// consider — it never writes a date by itself.</summary>
        public int? BirthYear { get; set; }

        /// <summary>Optional link to a site login, so "photos of me" can exist. Restrict-deleted: a
        /// person row must not vanish because an account was cleaned up.</summary>
        public int? UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        /// <summary>Which asset supplies this person's face crop in pickers and person pages.</summary>
        public int? CoverAssetId { get; set; }

        [ForeignKey(nameof(CoverAssetId))]
        public PhotoAsset? CoverAsset { get; set; }

        /// <summary>The Immich face cluster this person was named from (§2.4). Naming a cluster once is
        /// what fans suggestions across the whole library — the highest-leverage flow in the feature —
        /// and this is the only reference our schema keeps into the sidecar. Re-derivable, so pulling
        /// Immich costs nothing but the suggestions.</summary>
        [MaxLength(64)]
        public string? ImmichPersonId { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
