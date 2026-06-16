using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Join row relating a <see cref="Series"/> to a credited <see cref="Person"/> in a specific
    /// <see cref="CreditRole"/> (peer of <see cref="MovieCredit"/>). Unique on (SeriesId, PersonId, Role).
    /// </summary>
    [Table("SeriesCredit")]
    public class SeriesCredit
    {
        [Key]
        public int Id { get; set; }

        public int SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public Series Series { get; set; } = default!;

        public int PersonId { get; set; }

        [ForeignKey(nameof(PersonId))]
        public Person Person { get; set; } = default!;

        public CreditRole Role { get; set; }

        /// <summary>Billing/credit order within the role (0-based).</summary>
        public int Ordering { get; set; }

        /// <summary>Character played (actors only); null for directors/writers.</summary>
        public string? Character { get; set; }
    }
}
