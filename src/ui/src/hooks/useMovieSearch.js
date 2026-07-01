import { useState, useCallback } from "react";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";
// One random seed per page load → a STABLE random order for the landing grid across its infinite-scroll
// pages and effect re-runs (a hard reload reshuffles). The GetMoviesByType seed path orders by a
// deterministic permutation of (id+seed), so the same seed reproduces the same shuffle on every page.
const LANDING_SEED = Math.floor(Math.random() * 2000000000);
const TITLE_TYPES_KEY = "BrowseTitleTypes";
const SORT_KEY = "BrowseSort";

// The Browse "Sort by" control, like the Type scope, is a persistent overarching setting applied
// across every browse mode. Values: "alpha" (A→Z by SimpleTitle — the default), "added" (Recently
// Added, by UploadedDate desc), "imdb", "rt" (Tomatometer), "popcorn" (Popcornmeter). Persisted so
// it survives reloads/sessions.
export const BROWSE_SORTS = ["alpha", "added", "imdb", "rt", "popcorn"];

export function loadSort() {
  try {
    const raw = window.localStorage.getItem(SORT_KEY);
    return BROWSE_SORTS.includes(raw) ? raw : "alpha";
  } catch {
    return "alpha";
  }
}

export function saveSort(sort) {
  try {
    window.localStorage.setItem(SORT_KEY, BROWSE_SORTS.includes(sort) ? sort : "alpha");
  } catch {
    /* ignore — a stale persisted sort just means the default next time */
  }
}

// Append the active sort to a browse endpoint URL. "alpha" is the server default, but we send it
// explicitly anyway so a type-scope browse is alphabetical (not the legacy random seed).
function sortSuffix(sort) {
  return `&sort=${encodeURIComponent(BROWSE_SORTS.includes(sort) ? sort : "alpha")}`;
}

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
  // Start with a non-fetchable sentinel: Browse stays in its skeleton (no fetch) until NavBar's URL
  // effect dispatches the real search for the current URL. That dispatch happens on mount — BEFORE
  // /API/Me resolves — so with the auth gate removed in Browse, the movie grid loads in parallel with
  // auth instead of waiting on it, and there's no wasted placeholder fetch. `pending` distinguishes
  // this initial state from a legitimately empty search.
  const [search, setSearch] = useState({ url: null, titleTypes: loadTitleTypes(), pending: true });

  // An empty scope ("all types") with no other filter is the random discovery grid.
  const resetSearch = useCallback(() => {
    setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [] });
  }, []);

  // These now hit unified API endpoints that return BOTH movies and series (kind-tagged), each
  // narrowed to the current Type scope.
  const titleSearch = useCallback((title, types, sort) => {
    setSearch({ url: `/API/BrowseTitle?q=${encodeURIComponent(title)}${scopeSuffix(types)}${sortSuffix(sort)}`, titleTypes: types, sort, infinite: true });
  }, []);

  const actorSearch = useCallback((person, types, sort) => {
    setSearch({ url: `/API/BrowsePerson?q=${encodeURIComponent(person)}${scopeSuffix(types)}${sortSuffix(sort)}`, actor: person, titleTypes: types, sort, infinite: true });
  }, []);

  // Genre filter (AND across genres), within the Type scope.
  const genreSearch = useCallback((genres, types, sort) => {
    const list = (Array.isArray(genres) ? genres : String(genres).split(","))
      .map((g) => g.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: types, sort });
      return;
    }
    setSearch({ url: `/API/BrowseGenre?genres=${encodeURIComponent(list.join(","))}${scopeSuffix(types)}${sortSuffix(sort)}`, genre: list, titleTypes: types, sort, infinite: true });
  }, []);

  // All titles in a model-tagged franchise / shared universe, within the Type scope.
  const franchiseSearch = useCallback((franchise, types, sort) => {
    const fx = (franchise ?? "").trim();
    if (!fx) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: types, sort });
      return;
    }
    setSearch({ url: `/API/BrowseFranchise?franchise=${encodeURIComponent(fx)}${scopeSuffix(types)}${sortSuffix(sort)}`, franchise: fx, titleTypes: types, sort, infinite: true });
  }, []);

  const firstLetterSearch = useCallback((firstLetter, types, sort) => {
    setSearch({ url: `/API/BrowseLetter?letter=${encodeURIComponent(firstLetter)}${scopeSuffix(types)}${sortSuffix(sort)}`, startsWith: firstLetter, titleTypes: types, sort, infinite: true });
  }, []);

  // The Type scope on its own — the default browse when no other filter is active. `types` is the
  // scope (multi-select, OR semantics); an empty scope falls back to the random discovery grid.
  const titleTypeSearch = useCallback((types, sort) => {
    const list = (Array.isArray(types) ? types : String(types).split(","))
      .map((t) => t.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [], sort });
      return;
    }
    // infinite: this endpoint is paginated server-side; Browse streams it page-by-page. The Sort-by
    // control drives the order (Alphabetical by default), so the result is a stable, deterministic
    // ordering across pages rather than the former random assortment.
    setSearch({ url: `/API/GetMoviesByType?type=${encodeURIComponent(list.join(","))}${sortSuffix(sort)}`, titleTypes: list, sort, infinite: true });
  }, []);

  // The clean landing / home grid: the active Type scope in RANDOM order (the discovery grid). Unlike
  // titleTypeSearch it sends NO sort — the persisted Alphabetical/IMDb/RT sort is a *browse* setting,
  // not the landing. The seed gives a stable shuffle (see LANDING_SEED). An empty scope ("all types")
  // falls back to the dedicated all-types random endpoint.
  const landingSearch = useCallback((types) => {
    const list = (Array.isArray(types) ? types : String(types).split(","))
      .map((t) => t.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [] });
      return;
    }
    setSearch({
      url: `/API/GetMoviesByType?type=${encodeURIComponent(list.join(","))}&seed=${LANDING_SEED}`,
      titleTypes: list,
      infinite: true,
    });
  }, []);

  const ratingSearch = useCallback((maxRatingId, types, sort) => {
    setSearch({
      url: `/API/GetMoviesByRating?maxRatingId=${maxRatingId}${scopeSuffix(types)}${sortSuffix(sort)}`,
      maxRatingId: String(maxRatingId),
      titleTypes: types,
      sort,
      infinite: true,
    });
  }, []);

  // Personal lists (Seen / Want) are id-based; `types` rides along only so the Type selector keeps
  // its displayed value here — the lists themselves are not type-filtered.
  const movieIDListSearch = useCallback((movieIds, restoreOrder = null, types = null, sort = null) => {
    if (!movieIds || movieIds.length === 0) {
      setSearch({ url: null, restoreOrder, titleTypes: types, sort });
      return;
    }
    // Seen/Want (no restore order) stream as infinite scroll; the back-nav restore path
    // (restoreOrder set) stays a single fetch so it can re-apply the remembered order.
    setSearch({ movieIds, restoreOrder, titleTypes: types, sort, infinite: !restoreOrder });
  }, []);

  const restoreMovieIdsSearch = useCallback((movieIds, types = null) => {
    movieIDListSearch(movieIds, movieIds, types);
  }, [movieIDListSearch]);

  const moviesSeenSearch = useCallback((userData, types = null, sort = null) => {
    if (userData) movieIDListSearch(userData.moviesSeen, null, types, sort);
  }, [movieIDListSearch]);

  const moviesWantToWatchSearch = useCallback((userData, types = null, sort = null) => {
    if (userData) movieIDListSearch(userData.moviesToWatch, null, types, sort);
  }, [movieIDListSearch]);

  return {
    search,
    resetSearch,
    titleSearch,
    actorSearch,
    genreSearch,
    franchiseSearch,
    firstLetterSearch,
    titleTypeSearch,
    landingSearch,
    ratingSearch,
    movieIDListSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  };
}
