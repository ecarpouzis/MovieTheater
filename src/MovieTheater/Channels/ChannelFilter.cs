using System.Collections.Generic;
using System.Text.Json;

namespace MovieTheater.Channels
{
    /// <summary>
    /// The eligibility rule for a channel, serialized into <see cref="Db.Channel.FilterJson"/>
    /// (streaming-plan.md §8). All fields optional; an empty filter means "everything that
    /// has a playable file and isn't excluded from random".
    /// </summary>
    public class ChannelFilter
    {
        public List<int> GenreIds { get; set; } = new();

        /// <summary>"any" (default) or "all" — whether a movie must match one or every listed genre.</summary>
        public string GenreMode { get; set; } = "any";

        public int? YearMin { get; set; }
        public int? YearMax { get; set; }

        /// <summary>Inclusive MPA rating-id ceiling (1=G … 7=Unknown). Null = no ceiling.</summary>
        public int? MaxMpaRatingId { get; set; }

        /// <summary>When set, exclude movies this user has already marked Seen.</summary>
        public int? UnwatchedByUserId { get; set; }

        public bool ExcludeRemoveFromRandom { get; set; } = true;

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static ChannelFilter Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ChannelFilter();
            try
            {
                return JsonSerializer.Deserialize<ChannelFilter>(json, JsonOptions) ?? new ChannelFilter();
            }
            catch (JsonException)
            {
                return new ChannelFilter();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    }
}
