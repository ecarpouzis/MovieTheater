import { useState, useCallback } from "react";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";

export function escapeODataString(value) {
  return value.replace(/'/g, "''");
}

export function buildODataUrl(filter) {
  return `/odata/Movies?$filter=${encodeURIComponent(filter)}&$orderby=simpleTitle asc`;
}

// Filters that traverse navigation properties (Credits, MovieGenres) require PascalCase
// property names — including in $orderby — unlike the lenient simple-property filters.
export function buildNavODataUrl(filter) {
  return `/odata/Movies?$filter=${encodeURIComponent(filter)}&$orderby=SimpleTitle asc`;
}

export function useMovieSearch() {
  const [search, setSearch] = useState({ url: RANDOM_MOVIES_URL });

  const resetSearch = useCallback(() => {
    setSearch({ url: RANDOM_MOVIES_URL });
  }, []);

  // These now hit unified API endpoints that return BOTH movies and series (kind-tagged),
  // since series live in their own table — OData over /Movies alone would miss them.
  const titleSearch = useCallback((title) => {
    setSearch({ url: `/API/BrowseTitle?q=${encodeURIComponent(title)}` });
  }, []);

  // People search across movies + series (normalized credits, with a legacy string fallback server-side).
  const actorSearch = useCallback((person) => {
    setSearch({ url: `/API/BrowsePerson?q=${encodeURIComponent(person)}`, actor: person });
  }, []);

  // Genre filter (AND semantics across selected genres), movies + series.
  const genreSearch = useCallback((genres) => {
    const list = (Array.isArray(genres) ? genres : String(genres).split(","))
      .map((g) => g.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL });
      return;
    }
    setSearch({ url: `/API/BrowseGenre?genres=${encodeURIComponent(list.join(","))}`, genre: list });
  }, []);

  const firstLetterSearch = useCallback((firstLetter) => {
    setSearch({ url: `/API/BrowseLetter?letter=${encodeURIComponent(firstLetter)}`, startsWith: firstLetter });
  }, []);

  // Filter the grid by IMDB-aware TitleType (Movie / Short / TvSeries / TvMiniSeries / ...).
  // Uses a dedicated API endpoint (OData here has no EDM model, so enum $filter is unreliable).
  const titleTypeSearch = useCallback((type) => {
    if (!type) {
      setSearch({ url: RANDOM_MOVIES_URL });
      return;
    }
    setSearch({ url: `/API/GetMoviesByType?type=${encodeURIComponent(type)}`, titleType: type });
  }, []);

  const ratingSearch = useCallback((maxRatingId, page = 1) => {
    setSearch({
      url: `/API/GetMoviesByRating?maxRatingId=${maxRatingId}&page=${page}&pageSize=50`,
      maxRatingId: String(maxRatingId),
      page: Number(page),
    });
  }, []);

  const movieIDListSearch = useCallback((movieIds, restoreOrder = null) => {
    if (!movieIds || movieIds.length === 0) {
      setSearch({ url: null, restoreOrder });
      return;
    }
    setSearch({ movieIds, restoreOrder });
  }, []);

  const restoreMovieIdsSearch = useCallback((movieIds) => {
    movieIDListSearch(movieIds, movieIds);
  }, [movieIDListSearch]);

  const moviesSeenSearch = useCallback((userData) => {
    if (userData) movieIDListSearch(userData.moviesSeen);
  }, [movieIDListSearch]);

  const moviesWantToWatchSearch = useCallback((userData) => {
    if (userData) movieIDListSearch(userData.moviesToWatch);
  }, [movieIDListSearch]);

  return {
    search,
    resetSearch,
    titleSearch,
    actorSearch,
    genreSearch,
    firstLetterSearch,
    titleTypeSearch,
    ratingSearch,
    movieIDListSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  };
}
