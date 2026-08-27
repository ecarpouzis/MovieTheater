import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { lazyWithReload as lazy } from "../../lazyWithReload";
import { MovieCard, SimpleMovieCard, MOVIE_GRID_CELL } from "./MovieCard";
import { useViewingToggles } from "./UserMovieOptions";
import NowOnTvRail from "./NowOnTvRail";
import PlaylistPickerModal from "../Tv/PlaylistPickerModal";
import useIsMobile from "../../hooks/useIsMobile";
import LoadFailure from "../../Components/LoadFailure";
import CardGridSkeleton from "../../Components/CardGridSkeleton";
import CatalogHost from "../../catalog/CatalogHost";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { parseFacetState } from "../../catalog/rail/facetUrl";
import useSectionRail from "../../catalog/rail/useSectionRail";
import sectionRailSurfaces from "../../catalog/rail/sectionRailSurfaces";
import useRailSheet from "../../catalog/rail/useRailSheet";
import { createMoviesListSource, createMoviesSource } from "../../catalog/sources/moviesSource";
import { CATALOG_PARAM_KEYS } from "../../catalog/state/useCatalogView";
import { MOVIES_PARSE_SPEC, browseSearchFor, isPlainMoviesSearch } from "./moviesFacetSpec";
import { MOVIES_ENTITY_PARAMS, moviesViewerIdentity, useMoviesFacetSpec, useMoviesResultTotal } from "./useMoviesBrowse";

// The detail modal (917 lines + FileMappingEditor, SubtitlePicker, …) only renders after a card
// click + network fetch, so its chunk load hides behind that — keeping it out of the entry bundle.
const MovieModal = lazy(() => import("./MovieModal"));

// Page size for the dense id-list fill below. Matches the server default in GetMoviesByType.
const DENSE_PAGE_SIZE = 120;
// A bounded fill: no request loop may be unbounded (the house rule for anything iterating a big
// set). Past this the list simply stops growing — 10k tracked titles is far beyond any real list.
const DENSE_MAX_ROWS = 12_000;

// The viewer's list (`?my=`) whose membership each Viewing action defines — turning that action OFF
// is what removes a card from the grid (see handleToggleViewing).
const LIST_ACTION = {
  seen: "SetWatched",
  want: "SetWantToWatch",
};

/**
 * The open title, read out of ?title=<kind>:<id> (the photos ?photo= pattern): a card click pushes
 * it, ✕ replaces it away, the browser's Back closes the modal because the modal state IS the URL —
 * and the link is shareable/reload-safe for the first time. Anything that doesn't parse is treated
 * as no title at all.
 */
function titleFromUrl(search) {
  const raw = new URLSearchParams(search).get("title");
  const m = /^(movie|series|misc):([0-9]+)$/.exec(raw || "");
  if (!m) return null;
  const id = Number(m[2]);
  return Number.isSafeInteger(id) && id > 0 ? { kind: m[1], id } : null;
}

/**
 * "The landing" = nothing narrows the browse beyond the Type scope. The catalog's own params
 * (?view=&group=&items=&sort=) describe how the grid is shown, not what it shows — a `?view=wall`
 * landing still gets the Now-on-TV rail and the back-nav snapshot; so does the seeded `?f=type:Movies`.
 */
function isLandingSearch(search) {
  const p = new URLSearchParams(search);
  for (const k of CATALOG_PARAM_KEYS) p.delete(k);
  if ([...p.keys()].some((k) => k !== "f")) return false;
  return isPlainMoviesSearch(parseFacetState(search, MOVIES_PARSE_SPEC));
}

// Identity of the CURRENT result set. Used to reset the catalog stream (band cache, measured
// heights, scroll position) when the list becomes a different list rather than merely a longer one.
function listKeyOf(search) {
  if (search.pending) return "pending";
  if (search.movieIds) return `ids:${search.movieIds.length}:${search.sort || ""}:${search.restoreOrder ? "restore" : ""}`;
  return search.url || "empty";
}

function Browse({ search, userData, setUserData, isAuthReady, simpleStyle }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);
  // Bumped whenever the DENSE rows change under an unchanged list identity — a Seen/Want removal,
  // a background chunk landing, a modal save. The catalog stream re-reads its bands and keeps the
  // reader where they were (`CatalogSource.dataVersion`); a listKey change is the other thing, and
  // it deliberately resets the window and the scroll.
  const [dataVersion, setDataVersion] = useState(0);
  const bumpData = useCallback(() => setDataVersion((v) => v + 1), []);
  // A failed one-shot/dense fetch used to RE-THROW from inside its .catch — an unhandled
  // rejection with no UI at all, the skeleton sitting there forever. It is a real state now,
  // and retryNonce re-arms the fetch effects.
  const [fetchError, setFetchError] = useState(false);
  const [retryNonce, setRetryNonce] = useState(0);
  const isMobile = useIsMobile();
  const useSimpleStyle = simpleStyle && isMobile;
  const history = useHistory();
  const location = useLocation();

  // ── The facet rail's state (R9 S2): the URL is the filter; the sider rail reads the same URL. ──
  const spec = useMoviesFacetSpec(moviesViewerIdentity(userData));
  const rail = useSectionRail("movies", spec, { entityParams: MOVIES_ENTITY_PARAMS });
  const facetState = rail.state;
  const facetActions = rail.actions;
  const sheet = useRailSheet();
  const facetTotal = useMoviesResultTotal(facetState, sheet.isMobile);
  // A group header scopes in place (adds its facet / year range, regroups a level — one push). The
  // source reaches it through a ref so its identity stays keyed on the search alone.
  const scopeRef = useRef(null);
  scopeRef.current = (patch) => {
    facetActions.apply((d) => {
      if (patch.facet && !hasFacetValue(d.include[patch.facet.key], patch.facet.value)) {
        d.include[patch.facet.key] = [...(d.include[patch.facet.key] ?? []), patch.facet.value];
      }
      if (patch.years) { d.yearMin = patch.years[0]; d.yearMax = patch.years[1]; }
    }, patch.group ? { group: patch.group } : undefined);
  };

  // ── The two shapes of a movie browse (R9 S3 — ONE engine under both) ──────────────────────────
  //
  // - A paged URL browse is a SERVER source: `createMoviesSource` pages `/API/Browse*` straight into
  //   the catalog's bands, and the grouped views ride `/API/BrowseGroups` under the same scope.
  // - Everything else — the Seen / Want to watch id lists, the back-nav restore, a one-shot browse —
  //   is a DENSE list this page holds in memory and hands over as a client source. That is what keeps
  //   removal-on-untoggle honest: un-ticking Seen while browsing Seen edits the array in place (a
  //   sparse page map cannot express that without re-seating every following slot), and `dataVersion`
  //   tells the engine to re-read without throwing the reader back to the top.
  //
  // Either way the Grid is the package's GridView laying THIS page's MovieCard into the shared
  // bands (`renderCard`), so every tweak, the letter strip and the pills work on every path.
  const sparseInfinite = !!search.infinite && !!search.url && !search.movieIds && !search.pending;
  const denseIdList = !!search.movieIds && !!search.infinite;
  const listKey = useMemo(() => listKeyOf(search), [search]);

  // ── The dense fill ────────────────────────────────────────────────────────────────────────────
  // An id list is paged in bounded chunks (never "fetch everything in one request"): the first chunk
  // paints, each following one extends the array and bumps dataVersion. A one-shot URL/restore
  // search is a single fetch — the restore path needs the whole list before it can re-apply the
  // remembered on-screen order, so it must not paint a half-ordered grid.
  useEffect(() => {
    if (sparseInfinite || search.pending) return undefined;
    if (!search.url && !search.movieIds) {
      setMovieDataArray([]);
      setLoading(false);
      bumpData();
      return undefined;
    }
    setLoading(true);
    setFetchError(false);
    setMovieDataArray([]);
    bumpData();
    const controller = new AbortController();
    const { signal } = controller;
    let cancelled = false;

    const oneShot = () => (search.movieIds
      ? fetch("/API/GetMoviesByIds", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(search.movieIds),
          signal,
        })
      : fetch(search.url, { signal }))
      .then((r) => r.json())
      .then((data) => {
        if (cancelled) return;
        if (data != null && Array.isArray(data.movies)) setMovieDataArray(data.movies);
        else setMovieDataArray(Array.isArray(data) ? data : (data?.value ?? []));
        setLoading(false);
        bumpData();
      });

    // Chunked + resumable-by-construction: each pass reports its own progress into the grid, and a
    // pass that returns nothing (or hits the row cap) is the deterministic stop.
    const fill = async () => {
      const rows = [];
      const sortQs = search.sort ? `&sort=${encodeURIComponent(search.sort)}` : "";
      let total = Infinity;
      for (let page = 1; !cancelled && rows.length < Math.min(total, DENSE_MAX_ROWS); page += 1) {
        // eslint-disable-next-line no-await-in-loop
        const r = await fetch(`/API/GetMoviesByIds?page=${page}&pageSize=${DENSE_PAGE_SIZE}${sortQs}`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(search.movieIds),
          signal,
        });
        // eslint-disable-next-line no-await-in-loop
        const data = await r.json();
        if (cancelled) return;
        const more = Array.isArray(data?.movies) ? data.movies : [];
        if (typeof data?.totalCount === "number" && data.totalCount >= 0) total = data.totalCount;
        if (more.length === 0) break;
        rows.push(...more);
        setMovieDataArray(rows.slice());
        setLoading(false);
        bumpData();
        if (more.length < DENSE_PAGE_SIZE) break;
      }
      setLoading(false);
    };

    (denseIdList ? fill() : oneShot()).catch((err) => {
      if (cancelled || err?.name === "AbortError") return;
      setFetchError(true);
      setLoading(false);
    });
    return () => { cancelled = true; controller.abort(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search.url, search.movieIds, search.pending, search.sort, sparseInfinite, denseIdList, retryNonce, bumpData]);

  // The detail modal's state lives in the URL (?title=<kind>:<id>) — see titleFromUrl.
  const openTitle = useMemo(() => titleFromUrl(location.search), [location.search]);
  const selectedMovieId = openTitle?.id ?? null;
  const selectedKind = openTitle?.kind ?? "movie";
  const isModalVisible = openTitle != null;

  // "Add to playlist" surface: pickerRequest = { items:[{playableId,title}], name } (null = closed).
  // playlistsVersion bumps to nudge the My-Playlists shelf to reload after a create/change.
  const [pickerRequest, setPickerRequest] = useState(null);
  const [, setPlaylistsVersion] = useState(0);
  const openPlaylistPicker = useCallback((items, name = "") => {
    setPickerRequest({ items: items || [], name });
  }, []);

  // Restore-order reshuffle (back-nav) — memoized so it doesn't rebuild the Map/Set/concat over the
  // full array (and hand a fresh array identity to the source) on every unrelated render.
  const displayMovies = useMemo(() => {
    if (!Array.isArray(search.restoreOrder) || search.restoreOrder.length === 0) return movieDataArray;
    const movieById = new Map(movieDataArray.map((movie) => [movie.id, movie]));
    const orderedMovies = search.restoreOrder.map((id) => movieById.get(id)).filter(Boolean);
    const orderedIdSet = new Set(orderedMovies.map((movie) => movie.id));
    return [...orderedMovies, ...movieDataArray.filter((movie) => !orderedIdSet.has(movie.id))];
  }, [movieDataArray, search.restoreOrder]);

  // Every handler below is passed down to the memoized cards, so all of them must be identity-stable
  // or React.memo does nothing and each card re-renders on every Browse render (append, modal
  // open/close, Seen/Want toggle). They read the mutable bits they need through refs instead of
  // closing over them. `history` from react-router v5 is already stable.
  const movieDataRef = useRef(movieDataArray);
  const locationRef = useRef(location);
  movieDataRef.current = movieDataArray;
  locationRef.current = location;

  // Open pushes (Back = close, like any full page); ✕ replaces the param away so closing doesn't
  // grow the history. Route state (browseMovieIds — the back-nav grid restore) rides along on both.
  const handleOpenMovie = useCallback((movieId, kind = "movie") => {
    const loc = locationRef.current;
    const params = new URLSearchParams(loc.search);
    params.set("title", `${kind || "movie"}:${movieId}`);
    history.push({ pathname: loc.pathname, search: `?${params.toString()}`, state: loc.state });
  }, [history]);

  const handleCloseModal = useCallback(() => {
    const loc = locationRef.current;
    const params = new URLSearchParams(loc.search);
    if (!params.has("title")) return;
    params.delete("title");
    const search = params.toString();
    history.replace({ pathname: loc.pathname, search: search ? `?${search}` : "", state: loc.state });
  }, [history]);

  const handleActorSearch = useCallback((actor) => {
    // The facet URL is pushed DIRECTLY (R9 S2 normalization): this used to push the legacy
    // `?mode=actor&value=` and let NavBar's entry dispatcher rewrite it a render later — an extra
    // history.replace and an extra dispatch per chip click.
    const targetSearch = browseSearchFor("actor", actor);
    if (targetSearch == null) return;
    const location = locationRef.current;
    const movieDataArray = movieDataRef.current;

    if (location.pathname === "/" && location.search === targetSearch) {
      return;
    }

    // Snapshot the on-screen ids so back-navigation restores this exact grid — but ONLY for
    // movie/series grids. MiscVideo has its OWN id space that overlaps movie/series ids, and the
    // restore path resolves ids through /API/GetMoviesByIds (Movie/Series tables only). Snapshotting
    // a misc grid would therefore restore the unrelated movie that happens to share each misc id
    // (e.g. misc 13 → Movie 13). For any grid containing misc we skip the snapshot and let back-nav
    // re-run the scope query (a stable, materialized list) instead.
    const gridHasMisc = movieDataArray.some((m) => m.kind === "misc");
    if (isLandingSearch(location.search) && !gridHasMisc && movieDataArray.length > 0) {
      const browseMovieIds = movieDataArray.map((movie) => movie.id).filter((id) => Number.isInteger(id) && id > 0);
      if (browseMovieIds.length > 0) {
        history.replace({
          pathname: location.pathname,
          search: location.search,
          state: {
            ...(location.state || {}),
            browseMovieIds,
          },
        });
      }
    }

    history.push({
      pathname: "/",
      search: targetSearch,
    });
  }, [history]);

  // Generic "jump to a browse search" used by the detail modal's insight chips (franchise, comp
  // title). Pushes the facet URL the mode MEANS (`?f=franchise:…`, `?q=…`); the modal closes because
  // the new URL carries no ?title=.
  const handleBrowseSearch = useCallback((mode, value) => {
    const search = browseSearchFor(mode, value);
    if (search == null) return;
    history.push({ pathname: "/", search });
  }, [history]);

  // Called by the cards / MovieModal when a movie's viewing state is toggled.
  // Only removes a movie from the displayed list when the action that was deactivated
  // is the exact criterion that defines membership in the current browse mode.
  // e.g. removing from Seen while on the Want list leaves the card visible,
  // because Want-list membership (SetWantToWatch) was not affected.
  const handleToggleViewing = useCallback((movieId, action, isActive) => {
    if (!isActive) {
      const lists = (new URLSearchParams(locationRef.current.search).get("my") || "").split(",");
      if (lists.some((l) => LIST_ACTION[l] === action) && movieDataRef.current.some((m) => m.id === movieId)) {
        setMovieDataArray((prev) => prev.filter((m) => m.id !== movieId));
        bumpData();
      }
    }
  }, [bumpData]);

  const handleMovieUpdated = useCallback((updatedMovie) => {
    setMovieDataArray((prev) => prev.map((m) => (m.id === updatedMovie.id ? updatedMovie : m)));
    // The server source has the new row too — a re-read is how it picks it up.
    bumpData();
  }, [bumpData]);

  // ── The card the Grid lays into the bands ─────────────────────────────────────────────────────
  // MovieCard / SimpleMovieCard are MODULE-LEVEL components (the BandSlot memo law): a renderer
  // created per render would be a new component type every render and remount the whole band. The
  // live context reaches them through one memoized bundle, so the renderer's identity changes
  // exactly when a card's appearance depends on something that changed — a Seen/Want set, the
  // searched person, the sign-in state — and not on every unrelated Browse render.
  const seenSet = useMemo(() => new Set(userData?.moviesSeen), [userData?.moviesSeen]);
  const wantSet = useMemo(() => new Set(userData?.moviesToWatch), [userData?.moviesToWatch]);
  const { toggleSeen, toggleWant } = useViewingToggles(userData, setUserData, handleToggleViewing);
  const activeName = (search.facet?.include?.person?.[0] ?? "").toString().trim().toLowerCase();
  const cardCtx = useMemo(() => ({
    simple: useSimpleStyle,
    activeName,
    showOptions: !!userData,
    seenSet,
    wantSet,
    onMovieClick: handleOpenMovie,
    onActorSearch: handleActorSearch,
    onToggleSeen: toggleSeen,
    onToggleWant: toggleWant,
  }), [useSimpleStyle, activeName, userData, seenSet, wantSet, handleOpenMovie, handleActorSearch, toggleSeen, toggleWant]);

  const renderCard = useCallback((item, view) => {
    const row = item.raw;
    const Comp = cardCtx.simple ? SimpleMovieCard : MovieCard;
    return (
      <Comp
        item={row}
        eager={view.eager}
        metadata={view.metadata}
        hoverClass={view.hoverClass}
        activeName={cardCtx.activeName}
        showOptions={cardCtx.showOptions}
        isWatched={cardCtx.showOptions ? cardCtx.seenSet.has(row.id) : false}
        isWanted={cardCtx.showOptions ? cardCtx.wantSet.has(row.id) : false}
        onMovieClick={cardCtx.onMovieClick}
        onActorSearch={cardCtx.onActorSearch}
        onToggleSeen={cardCtx.onToggleSeen}
        onToggleWant={cardCtx.onToggleWant}
      />
    );
  }, [cardCtx]);

  // ── The source ────────────────────────────────────────────────────────────────────────────────
  // The open/browse handlers reach the server source through refs so its identity stays keyed on
  // the search alone.
  const openRef = useRef(null);
  const browseRef = useRef(null);
  openRef.current = handleOpenMovie;
  browseRef.current = handleBrowseSearch;
  const serverSource = useMemo(
    () => (sparseInfinite
      ? createMoviesSource({
          search,
          onOpen: (id, kind) => openRef.current?.(id, kind),
          onBrowse: (mode, value) => browseRef.current?.(mode, value),
          onScope: (patch) => scopeRef.current?.(patch),
        })
      : null),
    [sparseInfinite, search]
  );
  const listSource = useMemo(
    () => (serverSource
      ? null
      : createMoviesListSource({
          rows: displayMovies,
          listKey,
          sort: search.sort,
          alphabetical: search.sort === "alpha" && !search.restoreOrder,
          onOpen: (id, kind) => openRef.current?.(id, kind),
        })),
    [serverSource, displayMovies, listKey, search.sort, search.restoreOrder]
  );
  const gridClass = useSimpleStyle ? "bx-grid--simple" : "bx-grid--movies";
  const source = useMemo(() => {
    const base = serverSource ?? listSource;
    return base && { ...base, renderCard, gridClass, gridCell: MOVIE_GRID_CELL, dataVersion };
  }, [serverSource, listSource, renderCard, gridClass, dataVersion]);

  // While the dense list is still on the wire (or failed) the Grid shows the site-wide skeleton /
  // failure surface instead of the stream — the pills, the rail and the chips stay up throughout.
  const gridOverride = serverSource
    ? null
    : search.pending || (loading && displayMovies.length === 0)
      ? <CardGridSkeleton />
      : fetchError
        ? <LoadFailure message="Couldn't load the library." onRetry={() => setRetryNonce((n) => n + 1)} />
        : null;

  // The bar's tools: the phone's Filters pill raising the full-page sheet (the desktop rail is the
  // sider's MoviesSiderRail, which carries the count on its head line), the chips over the results,
  // and the bar's SmartSearch — all from the shared rail surfaces.
  const { pill: filtersPill, chips, surfaces } = sectionRailSurfaces(rail, sheet, {
    total: facetTotal.data,
    placeholder: "Title, person:Pacino, genre:Crime…",
  });

  return (
    <>
      {surfaces}
      {/* Rail mounts regardless of the grid's loading state so its lineup + posters fetch in parallel
          with the movie grid (it self-gates on a streaming-enabled session), rather than only after. */}
      {isLandingSearch(location.search) && <NowOnTvRail userData={userData} setUserData={setUserData} />}
      <CatalogHost
        section="movies"
        source={source}
        overrides={gridOverride ? { grid: gridOverride } : undefined}
        tools={filtersPill}
        beforeResults={chips}
      />
      <Suspense fallback={null}>
        <MovieModal
          movieId={selectedMovieId}
          kind={selectedKind}
          open={isModalVisible}
          onClose={handleCloseModal}
          actorSearch={handleActorSearch}
          onBrowse={handleBrowseSearch}
          onOpenTitle={handleOpenMovie}
          userData={userData}
          setUserData={setUserData}
          onToggleViewing={handleToggleViewing}
          onMovieUpdated={useSimpleStyle ? undefined : handleMovieUpdated}
          onAddToPlaylist={openPlaylistPicker}
        />
      </Suspense>
      <PlaylistPickerModal
        open={!!pickerRequest}
        items={pickerRequest?.items || []}
        defaultName={pickerRequest?.name || ""}
        onClose={() => setPickerRequest(null)}
        onDone={() => setPlaylistsVersion((v) => v + 1)}
      />
    </>
  );
}

export default Browse;
