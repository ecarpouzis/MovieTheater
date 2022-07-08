using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MovieTheater.Services.Tmdb
{
    //Auto-generated class of properties for the movie API
    //generated using https://json2csharp.com/
    public class MovieDto
    {
        [JsonProperty("adult")]
        public bool Adult { get; set; }

        [JsonProperty("backdrop_path")]
        public string BackdropPath { get; set; }

        [JsonProperty("genre_ids")]
        public List<int> GenreIds { get; set; }

        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("original_language")]
        public string OriginalLanguage { get; set; }

        [JsonProperty("original_title")]
        public string OriginalTitle { get; set; }

        [JsonProperty("overview")]
        public string Overview { get; set; }

        [JsonProperty("release_date")]
        public DateTime ReleaseDate { get; set; }

        [JsonProperty("poster_path")]
        public string PosterPath { get; set; }

        [JsonProperty("popularity")]
        public decimal Popularity { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("video")]
        public bool Video { get; set; }

        [JsonProperty("vote_average")]
        public decimal VoteAverage { get; set; }

        [JsonProperty("vote_count")]
        public int VoteCount { get; set; }
    }

    public class Root
    {
        [JsonProperty("movie_results")]
        public List<MovieDto> MovieResults { get; set; }

        [JsonProperty("person_results")]
        public List<object> PersonResults { get; set; }

        [JsonProperty("tv_results")]
        public List<object> TvResults { get; set; }

        [JsonProperty("tv_episode_results")]
        public List<object> TvEpisodeResults { get; set; }

        [JsonProperty("tv_season_results")]
        public List<object> TvSeasonResults { get; set; }
    }
}
