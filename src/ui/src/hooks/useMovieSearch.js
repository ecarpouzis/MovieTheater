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

  const titleSearch = useCallback((title) => {
    const escaped = escapeODataString(title);
    setSearch({ url: buildODataUrl(`contains(simpleTitle,'${escaped}') or contains(title,'${escaped}')`) });
  }, []);

  // Search the full normalized cast (MovieCredit -> Person), falling back to the legacy
  // Actors string for any movie not yet normalized.
  const actorSearch = useCallback((actor) => {
    const escaped = escapeODataString(actor);
    setSearch({
      url: buildNavODataUrl(
        `Credits/any(c: contains(c/Person/DisplayName,'${escaped}')) or contains(Actors,'${escaped}')`
      ),
      actor,
    });
  }, []);

  // Filter by one or more normalized genres (Genre table), with a legacy Genre-string
  // fallback. Multiple genres use AND semantics (a movie must have every selected genre),
  // so each genre gets its own any() clause with a distinct range variable.
  const genreSearch = useCallback((genres) => {
    const list = (Array.isArray(genres) ? genres : String(genres).split(","))
      .map((g) => g.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL });
      return;
    }
    const filter = list
      .map((g, i) => {
        const e = escapeODataString(g);
        return `(MovieGenres/any(g${i}: g${i}/Genre/Name eq '${e}') or contains(Genre,'${e}'))`;
      })
      .join(" and ");
    setSearch({ url: buildNavODataUrl(filter), genre: list });
  }, []);

  const firstLetterSearch = useCallback((firstLetter) => {
    if (firstLetter === "#") {
      const digitFilters = "0123456789".split("").map((d) => `startswith(simpleTitle,'${d}')`).join(" or ");
      setSearch({ url: buildODataUrl(digitFilters), startsWith: firstLetter });
    } else {
      const escaped = escapeODataString(firstLetter);
      setSearch({ url: buildODataUrl(`startswith(simpleTitle,'${escaped}')`), startsWith: firstLetter });
    }
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
    ratingSearch,
    movieIDListSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  };
}
