using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// Global display order for the channel-guide shelves (categories). Admin-editable; the guide and
    /// the homepage rail order shelves by this. A category with no row here sorts after the listed ones
    /// (so a newly-added catalog category just appends until the admin places it). Kept separate from the
    /// channel <see cref="Channel.SortOrder"/> so a <c>channel-catalog --apply</c> can't clobber it.
    /// </summary>
    [Table("ChannelShelf")]
    public class ChannelShelf
    {
        [Key]
        [MaxLength(48)]
        public string Category { get; set; } = default!;

        public int SortOrder { get; set; }
    }
}
