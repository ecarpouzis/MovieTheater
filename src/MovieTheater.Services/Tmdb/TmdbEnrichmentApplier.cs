using System;
using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Services.Tmdb
{
    /// <summary>
    /// Writes a <see cref="TmdbMovieDetailDto"/> into a <see cref="Movie"/>'s Phase-A enrichment
    /// columns. Shared by the <c>backfill-tmdb</c> command and (later) the movie-insert flow so both
    /// produce identical data. Mirrors the role <see cref="Omdb.OmdbApi.OmdbToMovie"/> plays for OMDB
    /// and the IMDB applier plays for the scrape. Only non-empty values are written, so it never
    /// blanks a column TMDB happens not to know; TMDB-authoritative fields (ISO language, worldwide
    /// revenue) intentionally supersede the OMDB fallbacks set at insert time.
    /// </summary>
    public static class TmdbEnrichmentApplier
    {
        public static bool Apply(Movie movie, TmdbMovieDetailDto detail)
        {
            if (detail == null || detail.Id <= 0)
                return false;

            movie.TmdbId = detail.Id;

            if (!string.IsNullOrWhiteSpace(detail.Tagline)) movie.Tagline = detail.Tagline.Trim();
            if (detail.Budget > 0) movie.BudgetUsd = detail.Budget;
            if (detail.Revenue > 0) movie.RevenueUsd = detail.Revenue;                 // worldwide; supersedes OMDB domestic
            if (!string.IsNullOrWhiteSpace(detail.OriginalLanguage)) movie.OriginalLanguage = detail.OriginalLanguage.Trim(); // ISO-639-1; supersedes OMDB name
            if (!string.IsNullOrWhiteSpace(detail.BackdropPath)) movie.BackdropPath = detail.BackdropPath.Trim();
            if (detail.Popularity > 0) movie.TmdbPopularity = detail.Popularity;
            if (detail.VoteCount > 0) movie.TmdbVoteCount = detail.VoteCount;

            var country = FormatCountries(detail.ProductionCountries);
            if (country != null) movie.Country = country;

            var trailer = PickTrailerKey(detail.Videos);
            if (trailer != null) movie.TrailerKey = trailer;

            return true;
        }

        private static string? FormatCountries(List<TmdbCountry> countries)
        {
            if (countries == null || countries.Count == 0) return null;
            var joined = string.Join(", ", countries
                .Where(c => !string.IsNullOrWhiteSpace(c?.Name))
                .Select(c => c.Name.Trim()));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        // Prefer an official YouTube trailer, then any YouTube trailer, then any YouTube video.
        private static string? PickTrailerKey(TmdbVideos videos)
        {
            var all = videos?.Results;
            if (all == null || all.Count == 0) return null;

            static bool IsYouTube(TmdbVideo v) => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase);
            static bool IsTrailer(TmdbVideo v) => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase);

            var pick = all.FirstOrDefault(v => IsYouTube(v) && IsTrailer(v) && v.Official)
                    ?? all.FirstOrDefault(v => IsYouTube(v) && IsTrailer(v))
                    ?? all.FirstOrDefault(IsYouTube);

            return string.IsNullOrWhiteSpace(pick?.Key) ? null : pick.Key.Trim();
        }
    }
}
