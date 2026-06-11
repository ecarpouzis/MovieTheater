using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    /// <summary>
    /// A credited person. Keyed by a synthetic <see cref="Id"/> so people can exist with or
    /// without an IMDB name id: scrape-sourced rows carry <see cref="ImdbNameId"/> (nm…),
    /// while people parsed from APIs/manual text have a null <see cref="ImdbNameId"/> and are
    /// deduplicated by <see cref="NameKey"/>. A name-only person is "upgraded" (its nm filled)
    /// when the scrape later supplies a matching IMDB identity.
    /// </summary>
    [Table("Person")]
    [Index(nameof(ImdbNameId), IsUnique = true)] // SqlServer makes this a filtered unique index (nm not null)
    [Index(nameof(NameKey))]
    public class Person
    {
        [Key]
        public int Id { get; set; }

        /// <summary>IMDB name id (e.g. "nm0000206"); null for people parsed from text/APIs.</summary>
        [MaxLength(20)]
        public string? ImdbNameId { get; set; }

        public string? DisplayName { get; set; }

        /// <summary>Comma-joined IMDB primary professions, e.g. "actor, producer".</summary>
        public string? PrimaryProfessions { get; set; }

        /// <summary>Normalized display name (lower + trimmed) used to dedup nm-less people.</summary>
        [MaxLength(200)]
        public string? NameKey { get; set; }

        [InverseProperty(nameof(MovieCredit.Person))]
        public ICollection<MovieCredit> Credits { get; set; } = [];
    }
}
