using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("BoardgameExtraDetails")]
    public class BoardgameExtraDetails
    {
        [Key]
        public int BoardgameId { get; set; }

        public string? AlternateNamesJson { get; set; }

        public string? RanksJson { get; set; }

        public string? LinksJson { get; set; }

        public string? PollsJson { get; set; }

        public string? VersionsXml { get; set; }

        public string? VideosJson { get; set; }

        public string? MarketplaceXml { get; set; }

        public string? RawXml { get; set; }
    }
}
