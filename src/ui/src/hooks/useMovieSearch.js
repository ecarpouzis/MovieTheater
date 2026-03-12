import { useState, useCallback } from "react";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";

export function escapeODataString(value) {
  return value.replace(/'/g, "''");
}

export function buildODataUrl(filter) {
  return `/odata/Movies?$filter=${encodeURIComponent(filter)}&$orderby=simpleTitle asc`;
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

  const actorSearch = useCallback((actor) => {
    const escaped = escapeODataString(actor);
    setSearch({ url: buildODataUrl(`contains(actors,'${escaped}')`) });
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
    firstLetterSearch,
    movieIDListSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  };
}
