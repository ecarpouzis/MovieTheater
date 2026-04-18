using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("BoardgameImageDetails")]
    public class BoardgameImageDetails
    {
        [Key]
        public int BoardgameId { get; set; }

        public int ImageVersion { get; set; }

        public string? ImageUrl { get; set; }

        public string? ThumbnailUrl { get; set; }

        [ForeignKey(nameof(BoardgameId))]
        public Boardgame Boardgame { get; set; } = default!;
    }
}
