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

        /// <summary>
        /// Cached boardgame-similarity result for this game: a JSON-serialized list of
        /// the top similar games (see <c>SimilarGameDto</c>). Computed when a new game is
        /// added and persisted here so the compare does not have to re-run on every startup.
        /// </summary>
        public string? SimilarGamesJson { get; set; }
    }
}
