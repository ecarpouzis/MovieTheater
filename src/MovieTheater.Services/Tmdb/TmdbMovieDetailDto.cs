using System.Collections.Generic;
using Newtonsoft.Json;

namespace MovieTheater.Services.Tmdb
{
    /// <summary>
    /// Subset of TMDB's <c>/3/movie/{id}?append_to_response=videos</c> detail response carrying the
    /// Phase-A enrichment fields the flat <see cref="MovieDto"/> (from <c>/find</c>) doesn't return.
    /// The detail endpoint is the single source for all enrichment columns once we have the TMDB id;
    /// <c>/find</c> is used only to resolve that id from an IMDB id. See docs/metadata-enrichment-plan.md §4.
    /// </summary>
    public class TmdbMovieDetailDto
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("imdb_id")]
        public string ImdbId { get; set; }

        [JsonProperty("tagline")]
        public string Tagline { get; set; }

        [JsonProperty("budget")]
        public long Budget { get; set; }

        [JsonProperty("revenue")]
        public long Revenue { get; set; }

        [JsonProperty("original_language")]
        public string OriginalLanguage { get; set; }

        [JsonProperty("backdrop_path")]
        public string BackdropPath { get; set; }

        [JsonProperty("popularity")]
        public decimal Popularity { get; set; }

        [JsonProperty("vote_count")]
        public int VoteCount { get; set; }

        [JsonProperty("production_countries")]
        public List<TmdbCountry> ProductionCountries { get; set; }

        [JsonProperty("videos")]
        public TmdbVideos Videos { get; set; }
    }

    /// <summary>
    /// Subset of TMDB's <c>/3/tv/{id}?append_to_response=videos</c> detail response. TV uses different
    /// shapes than movie (no budget/revenue, country is <c>origin_country</c> codes, dates are
    /// <c>first_air_date</c>), so the trailer backfill only pulls the fields that map cleanly onto the
    /// shared Series enrichment columns — the trailer key above all.
    /// </summary>
    public class TmdbTvDetailDto
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("tagline")]
        public string Tagline { get; set; }

        [JsonProperty("original_language")]
        public string OriginalLanguage { get; set; }

        [JsonProperty("backdrop_path")]
        public string BackdropPath { get; set; }

        [JsonProperty("popularity")]
        public decimal Popularity { get; set; }

        [JsonProperty("vote_count")]
        public int VoteCount { get; set; }

        [JsonProperty("videos")]
        public TmdbVideos Videos { get; set; }
    }

    public class TmdbCountry
    {
        [JsonProperty("iso_3166_1")]
        public string Iso { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class TmdbVideos
    {
        [JsonProperty("results")]
        public List<TmdbVideo> Results { get; set; }
    }

    public class TmdbVideo
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("site")]
        public string Site { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("official")]
        public bool Official { get; set; }
    }
}
