import { useState, useCallback } from "react";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";
// One shuffle seed per page load → a STABLE random order across the grid's infinite-scroll pages and
// every effect re-run, so a card can't appear twice or be skipped at a page boundary. The server's
// random sort orders by a deterministic permutation of (id + seed), so the same seed reproduces the
// same shuffle on every page — and a hard reload mints a new seed, which is the reshuffle.
const SHUFFLE_SEED = Math.floor(Math.random() * 2000000000);
const TITLE_TYPES_KEY = "BrowseTitleTypes";
const SORT_KEY = "BrowseSort";

// The Browse "Sort by" control, like the Type scope, is a persistent overarching setting applied
// across every browse mode. Values: "random" (the shuffled discovery grid — the DEFAULT, and what the
// site opens on), "alpha" (A→Z by SimpleTitle), "added" (Recently Added, by UploadedDate desc),
// "imdb", "rt" (Tomatometer), "popcorn" (Popcornmeter). Persisted so it survives reloads/sessions.
//
// Random being a sort rather than a special landing mode is what makes the discovery grid ordinary:
// it pages, filters, and scopes exactly like the other five instead of being its own endpoint.
export const BROWSE_SORTS = ["random", "alpha", "added", "imdb", "rt", "popcorn"];
export const DEFAULT_SORT = "random";

export function loadSort() {
  try {
    const raw = window.localStorage.getItem(SORT_KEY);
    return BROWSE_SORTS.includes(raw) ? raw : DEFAULT_SORT;
  } catch {
    return DEFAULT_SORT;
  }
}

export function saveSort(sort) {
  try {
    window.localStorage.setItem(SORT_KEY, BROWSE_SORTS.includes(sort) ? sort : DEFAULT_SORT);
  } catch {
    /* ignore — a stale persisted sort just means the default next time */
  }
}

// Append the active sort to a browse endpoint URL. Sent explicitly even when it matches the server's
// own default, so a browse's order is never inferred from which params happen to be present. The
// random sort carries the page-load seed — without it every page would reshuffle independently.
function sortSuffix(sort) {
  const s = BROWSE_SORTS.includes(sort) ? sort : DEFAULT_SORT;
  return `&sort=${encodeURIComponent(s)}${s === "random" ? `&seed=${SHUFFLE_SEED}` : ""}`;
}

// The letter-strip source for a browse, or undefined when it has none. /API/BrowseLetters buckets the
// SAME rows the search pages (same mode/value/types — one shared filter server-side), so choosing
// Alphabetical gets the movie grid the music library's A–Z jump strip over whatever is being browsed,
// not just over the unfiltered library. Anything else falls back to the pager's page numbers.
//
// Only the alpha sort has letters worth jumping to, and only a scoped browse: BrowseLetters needs a
// type scope, and a Misc-inclusive one is a curated in-memory merge with no DB row order to walk.
function lettersUrlFor(types, sort, mode, value) {
  if (sort !== "alpha") return undefined;
  const list = (Array.isArray(types) ? types : []).filter(Boolean);
  if (list.length === 0 || list.includes("Misc")) return undefined;
  const p = new URLSearchParams({ type: list.join(",") });
  if (mode) {
    p.set("mode", mode);
    p.set("value", value);
  }
  return `/API/BrowseLetters?${p.toString()}`;
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
  // narrowed to the current Type scope. Every one carries its own lettersUrl so the A–Z strip follows
  // the alphabetical sort into a filtered browse, not just the unfiltered library.
  const titleSearch = useCallback((title, types, sort) => {
    setSearch({ url: `/API/BrowseTitle?q=${encodeURIComponent(title)}${scopeSuffix(types)}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(types, sort, "title", title), titleTypes: types, sort, infinite: true });
  }, []);

  const actorSearch = useCallback((person, types, sort) => {
    setSearch({ url: `/API/BrowsePerson?q=${encodeURIComponent(person)}${scopeSuffix(types)}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(types, sort, "actor", person), actor: person, titleTypes: types, sort, infinite: true });
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
    setSearch({ url: `/API/BrowseGenre?genres=${encodeURIComponent(list.join(","))}${scopeSuffix(types)}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(types, sort, "genre", list.join(",")), genre: list, titleTypes: types, sort, infinite: true });
  }, []);

  // All titles in a model-tagged franchise / shared universe, within the Type scope.
  const franchiseSearch = useCallback((franchise, types, sort) => {
    const fx = (franchise ?? "").trim();
    if (!fx) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: types, sort });
      return;
    }
    setSearch({ url: `/API/BrowseFranchise?franchise=${encodeURIComponent(fx)}${scopeSuffix(types)}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(types, sort, "franchise", fx), franchise: fx, titleTypes: types, sort, infinite: true });
  }, []);

  // No rail control writes ?mode=letter any more — the A–Z grid in the sidebar was replaced by the
  // on-page CatalogPager strip, which SCROLLS the alphabetical list instead of re-querying it (the
  // music/boardgames convention). The mode is kept because existing links and bookmarks use it.
  const firstLetterSearch = useCallback((firstLetter, types, sort) => {
    setSearch({ url: `/API/BrowseLetter?letter=${encodeURIComponent(firstLetter)}${scopeSuffix(types)}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(types, sort, "letter", firstLetter), startsWith: firstLetter, titleTypes: types, sort, infinite: true });
  }, []);

  // The Type scope on its own — the default browse when no other filter is active, and so also the
  // site's landing grid. `types` is the scope (multi-select, OR semantics); an empty scope falls back
  // to the one-shot all-types random endpoint (there is no paged endpoint that spans every table).
  //
  // There is no separate "landing" search any more. The landing used to be its own mode that ignored
  // the persisted sort and sent a bare seed; now that Random is one of the six sorts and the default
  // one, the landing is simply this browse under whichever sort the user last chose.
  const titleTypeSearch = useCallback((types, sort) => {
    const list = (Array.isArray(types) ? types : String(types).split(","))
      .map((t) => t.trim())
      .filter(Boolean);
    if (list.length === 0) {
      setSearch({ url: RANDOM_MOVIES_URL, titleTypes: [], sort });
      return;
    }
    // infinite: this endpoint is paginated server-side; Browse streams it page-by-page. The Sort-by
    // control drives the order, so the result is a stable, deterministic ordering across pages —
    // including under Random, which is a seeded permutation rather than a per-page reshuffle.
    setSearch({ url: `/API/GetMoviesByType?type=${encodeURIComponent(list.join(","))}${sortSuffix(sort)}`, lettersUrl: lettersUrlFor(list, sort), titleTypes: list, sort, infinite: true });
  }, []);

  // The combinable browse (R9 S2): the facet rail's whole state as `/API/Browse`'s query — the Type
  // scope, the BrowseFilterQuery params (`fqParams`, already serialized by moviesFilterParams) and
  // the sort. Every facet URL is a paged scope, so the catalog views and the letter strip ride it
  // like any other; `facet` keeps the parsed state on the search for the grid (the active person).
  // `forUser` (the URL's `for=<username>`) rides the query so `my=` reads THAT person's lists — on the
  // page URL and the letters URL alike, and `scopeOf` forwards it to the grouped views from there.
  const facetSearch = useCallback((fqParams, types, sort, facet = null, forUser = null) => {
    const list = (Array.isArray(types) ? types : String(types ?? "").split(","))
      .map((t) => t.trim())
      .filter(Boolean);
    const forQs = forUser ? `&for=${encodeURIComponent(forUser)}` : "";
    const fq = (fqParams ? `&${fqParams}` : "") + forQs;
    const typesQs = `types=${encodeURIComponent(list.join(","))}`;
    const lettersUrl = list.length && sort === "alpha" && !list.includes("Misc")
      ? `/API/BrowseLetters?type=${encodeURIComponent(list.join(","))}${fq}`
      : undefined;
    setSearch({ url: `/API/Browse?${typesQs}${fq}${sortSuffix(sort)}`, lettersUrl, titleTypes: list, sort, infinite: true, facet });
  }, []);

  // Browse ONE MPA rating (the rating itself, not a ceiling). `ratingIds` is the comma-separated set
  // of MPA lookup ids the picked stop stands for — usually one, but NC-17 covers NC-17(5) and X(6).
  // A title is filed under the rating that actually gates it (cert → legacy → inferred).
  const ratingSearch = useCallback((ratingIds, types, sort) => {
    const ids = String(ratingIds ?? "").trim();
    setSearch({
      url: `/API/GetMoviesByRating?ratingIds=${encodeURIComponent(ids)}${scopeSuffix(types)}${sortSuffix(sort)}`,
      ratingIds: ids,
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

  // One of the id lists — Seen / Want to watch / Suggested — whoever's they are (the caller passes the
  // scoped lists' ids; see hooks/useUserLists). `listKey` rides the search so the dense source's
  // identity changes when the list does, even between two lists of the same length.
  const moviesListSearch = useCallback((listKey, ids, types = null, sort = null) => {
    const movieIds = ids ?? [];
    if (movieIds.length === 0) {
      setSearch({ url: null, listKey, titleTypes: types, sort });
      return;
    }
    setSearch({ movieIds, listKey, restoreOrder: null, titleTypes: types, sort, infinite: true });
  }, []);

  return {
    search,
    resetSearch,
    titleSearch,
    actorSearch,
    genreSearch,
    franchiseSearch,
    firstLetterSearch,
    titleTypeSearch,
    ratingSearch,
    facetSearch,
    movieIDListSearch,
    restoreMovieIdsSearch,
    moviesListSearch,
  };
}
