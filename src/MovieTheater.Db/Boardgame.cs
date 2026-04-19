using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    [Table("Boardgame")]
    [Index(nameof(BggThingId), IsUnique = true)]
    public class Boardgame
    {
        [Key]
        public int id { get; set; }

        public int BggThingId { get; set; }

        public string? ThingType { get; set; }

        public string? Name { get; set; }

        public string? AlternateNamesJson { get; set; }

        public int? YearPublished { get; set; }

        public int? MinPlayers { get; set; }

        public int? MaxPlayers { get; set; }

        public int? PlayingTime { get; set; }

        public int? MinPlayTime { get; set; }

        public int? MaxPlayTime { get; set; }

        public int? MinAge { get; set; }

        public string? Description { get; set; }

        public int? UsersRated { get; set; }

        public decimal? AverageRating { get; set; }

        public decimal? BayesAverageRating { get; set; }

        public decimal? StdDev { get; set; }

        public decimal? Median { get; set; }

        public int? Owned { get; set; }

        public int? Trading { get; set; }

        public int? Wanting { get; set; }

        public int? Wishing { get; set; }

        public int? NumComments { get; set; }

        public int? NumWeights { get; set; }

        public decimal? AverageWeight { get; set; }

        public string? RanksJson { get; set; }

        public string? LinksJson { get; set; }

        public string? PollsJson { get; set; }

        public string? VersionsXml { get; set; }

        public string? VideosJson { get; set; }

        public string? MarketplaceXml { get; set; }

        public string? RawXml { get; set; }

        public DateTime LastSyncedUtc { get; set; }

        public string? RulesPdfCandidateUrl { get; set; }

        public string? RulesPdfUrl { get; set; }

        public string? HowToPlayVideoUrlsJson { get; set; }

        public DateTime? RulesSyncedUtc { get; set; }

        [NotMapped]
        public List<string> HowToPlayVideoUrls
        {
            get => string.IsNullOrWhiteSpace(HowToPlayVideoUrlsJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(HowToPlayVideoUrlsJson) ?? [];
            set => HowToPlayVideoUrlsJson = value.Count > 0 ? JsonSerializer.Serialize(value) : null;
        }

        public BoardgameImageDetails? ImageDetails { get; set; }

        private string? _imageUrl;
        [NotMapped]
        public string? ImageUrl
        {
            get => _imageUrl ?? ImageDetails?.ImageUrl;
            set => _imageUrl = value;
        }

        private string? _thumbnailUrl;
        [NotMapped]
        public string? ThumbnailUrl
        {
            get => _thumbnailUrl ?? ImageDetails?.ThumbnailUrl;
            set => _thumbnailUrl = value;
        }

        [NotMapped]
        public int ImageVersion => ImageDetails?.ImageVersion ?? 0;
    }
}
