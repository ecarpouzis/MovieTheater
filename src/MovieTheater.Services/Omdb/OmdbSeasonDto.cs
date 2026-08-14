using Newtonsoft.Json;
using System.Collections.Generic;

namespace MovieTheater.Services.Omdb
{
    /// <summary>OMDB's <c>&amp;Season=n</c> response: IMDb's episode list for one season.</summary>
    public class OmdbSeasonDto
    {
        [JsonProperty("Title")]
        public string Title { get; set; }

        [JsonProperty("Season")]
        public string Season { get; set; }

        [JsonProperty("totalSeasons")]
        public string TotalSeasons { get; set; }

        [JsonProperty("Episodes")]
        public List<OmdbSeasonEpisode> Episodes { get; set; }

        /// <summary>"True"/"False" — OMDB answers a season it doesn't have with False, not a 404.</summary>
        [JsonProperty("Response")]
        public string Response { get; set; }
    }

    public class OmdbSeasonEpisode
    {
        [JsonProperty("Title")]
        public string Title { get; set; }

        /// <summary>Episode number as text ("7"); OMDB stringifies every numeric field.</summary>
        [JsonProperty("Episode")]
        public string Episode { get; set; }

        [JsonProperty("Released")]
        public string Released { get; set; }

        [JsonProperty("imdbRating")]
        public string ImdbRating { get; set; }

        [JsonProperty("imdbID")]
        public string ImdbID { get; set; }
    }
}
