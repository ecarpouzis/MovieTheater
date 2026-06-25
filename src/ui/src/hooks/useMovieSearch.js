import { useState, useCallback } from "react";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";
const TITLE_TYPES_KEY = "BrowseTitleTypes";

// The Browse "Type" filter is a persistent, overarching scope applied across every browse mode (it
// keeps its value until the user changes it). Stored in localStorage so it survives reloads/sessions.
// Never-set defaults to Movies only; an explicit empty array means "all types" (no scope).
export function loadTitleTypes() {
  try {
    const raw = window.localStorage.getItem(TITLE_TYPES_KEY);
    if (raw == null) return ["Movies"];
    const arr = JSON.parse(raw);
    return Array.isArray(arr) ? arr.filter((t) => typeof t === "string") : ["Movies"];
  } catch {
    return ["Movies"];
  }
}

export function saveTitleTypes(types) {
  try {
    window.localStorage.setItem(TITLE_TYPES_KEY, JSON.stringify(Array.isArray(types) ? types : []));
  } catch {
    /* ignore — a stale persisted scope just means the default next time */
  }
}

// Append the Type scope to a browse endpoint URL (omitted when the scope is empty = all types).
function scopeSuffix(types) {
  return types && types.length ? `&types=${encodeURIComponent(types.join(","))}` : "";
}

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
  // titleTypes rides along on every search so the Type selector always reflects the active scope,
  // regardless of which other filter is in play.
  const [search, setSearch] = useState({ url: RANDOM_MOVIES_URL, titleTypes: loadTitleTypes() });

  // An empty scope ("all types") with no other filter is the random discovery grid.
  const resetSearch = useCallback(() => {
    setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [] });
  }, []);

  // These now hit unified API endpoints that return BOTH movies and series (kind-tagged), each
  // narrowed to the current Type scope.
  const titleSearch = useCallback((title, types) => {
    setSearch({ url: `/API/BrowseTitle?q=${encodeURIComponent(title)}${scopeSuffix(types)}`, titleTypes: types, infinite: true });
  }, []);

  const actorSearch = useCallback((person, types) => {
    setSearch({ url: `/API/BrowsePerson?q=${encodeURIComponent(person)}${scopeSuffix(types)}`, actor: person, titleTypes: types, infinite: true });
  }, []);

  // Genre filter (AND across genres), within the Type scope.
  const genreSearch = useCallback((genres, types) => {
    const list = (Array.isArray(genres) ? genres : String(genres).split(","))
      .map((g) => g.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: types });
      return;
    }
    setSearch({ url: `/API/BrowseGenre?genres=${encodeURIComponent(list.join(","))}${scopeSuffix(types)}`, genre: list, titleTypes: types, infinite: true });
  }, []);

  const firstLetterSearch = useCallback((firstLetter, types) => {
    setSearch({ url: `/API/BrowseLetter?letter=${encodeURIComponent(firstLetter)}${scopeSuffix(types)}`, startsWith: firstLetter, titleTypes: types, infinite: true });
  }, []);

  // The Type scope on its own — the default browse when no other filter is active. `types` is the
  // scope (multi-select, OR semantics); an empty scope falls back to the random discovery grid.
  const titleTypeSearch = useCallback((types) => {
    const list = (Array.isArray(types) ? types : String(types).split(","))
      .map((t) => t.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [] });
      return;
    }
    // infinite: this endpoint is paginated server-side; Browse streams it page-by-page.
    setSearch({ url: `/API/GetMoviesByType?type=${encodeURIComponent(list.join(","))}`, titleTypes: list, infinite: true });
  }, []);

  const ratingSearch = useCallback((maxRatingId, types) => {
    setSearch({
      url: `/API/GetMoviesByRating?maxRatingId=${maxRatingId}${scopeSuffix(types)}`,
      maxRatingId: String(maxRatingId),
      titleTypes: types,
      infinite: true,
    });
  }, []);

  // Personal lists (Seen / Want) are id-based; `types` rides along only so the Type selector keeps
  // its displayed value here — the lists themselves are not type-filtered.
  const movieIDListSearch = useCallback((movieIds, restoreOrder = null, types = null) => {
    if (!movieIds || movieIds.length === 0) {
      setSearch({ url: null, restoreOrder, titleTypes: types });
      return;
    }
    // Seen/Want (no restore order) stream as infinite scroll; the back-nav restore path
    // (restoreOrder set) stays a single fetch so it can re-apply the remembered order.
    setSearch({ movieIds, restoreOrder, titleTypes: types, infinite: !restoreOrder });
  }, []);

  const restoreMovieIdsSearch = useCallback((movieIds, types = null) => {
    movieIDListSearch(movieIds, movieIds, types);
  }, [movieIDListSearch]);

  const moviesSeenSearch = useCallback((userData, types = null) => {
    if (userData) movieIDListSearch(userData.moviesSeen, null, types);
  }, [movieIDListSearch]);

  const moviesWantToWatchSearch = useCallback((userData, types = null) => {
    if (userData) movieIDListSearch(userData.moviesToWatch, null, types);
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
