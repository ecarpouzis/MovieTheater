using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Join row relating a <see cref="Movie"/> to a credited <see cref="Person"/> in a
    /// specific <see cref="CreditRole"/>. One person can hold several roles on one movie
    /// (e.g. director + writer), hence the (MovieID, PersonId, Role) uniqueness.
    /// </summary>
    [Table("MovieCredit")]
    public class MovieCredit
    {
        [Key]
        public int Id { get; set; }

        public int MovieID { get; set; }

        [ForeignKey(nameof(MovieID))]
        public Movie Movie { get; set; } = default!;

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
