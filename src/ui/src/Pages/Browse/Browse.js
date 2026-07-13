import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CardList from "./CardList";
import SimpleCardList from "./SimpleCardList";
import NowOnTvRail from "./NowOnTvRail";
import PlaylistPickerModal from "../Tv/PlaylistPickerModal";
import useIsMobile from "../../hooks/useIsMobile";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";

// The detail modal (917 lines + FileMappingEditor, SubtitlePicker, …) only renders after a card
// click + network fetch, so its chunk load hides behind that — keeping it out of the entry bundle.
const MovieModal = lazy(() => import("./MovieModal"));

// Page size for infinite-scroll modes. Matches the server default in GetMoviesByType.
const INFINITE_PAGE_SIZE = 60;

// The browse mode whose membership each Viewing action defines — turning that action OFF is what
// removes a card from the grid (see handleToggleViewing).
const MODE_ACTION = {
  seen: "SetWatched",
  want: "SetWantToWatch",
};

// Hoisted: a fresh object literal in JSX is a new prop identity on every render.
const SENTINEL_STYLE = { height: 1 };
const LOADING_MORE_STYLE = { textAlign: "center", padding: "16px", color: "#8fa8c0", fontSize: "13px" };

// Append page/pageSize to a browse URL (preserving its existing query string).
function withPage(url, page, pageSize) {
  const u = new URL(url, window.location.origin);
  u.searchParams.set("page", String(page));
  u.searchParams.set("pageSize", String(pageSize));
  return u.pathname + u.search;
}

// Fetch one page of an infinite-scroll search. Id-list searches (Seen/Want) POST the id
// set to GetMoviesByIds with paging params; the rest are GET endpoints that take page/pageSize.
function fetchInfinitePage(search, page, signal) {
  if (search.movieIds) {
    const sortQs = search.sort ? `&sort=${encodeURIComponent(search.sort)}` : "";
    return fetch(`/API/GetMoviesByIds?page=${page}&pageSize=${INFINITE_PAGE_SIZE}${sortQs}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(search.movieIds),
      signal,
    });
  }
  return fetch(withPage(search.url, page, INFINITE_PAGE_SIZE), { signal });
}

// Identity of the CURRENT result set. Used to reset the grid's window (measured row heights, scroll
// position) when the list becomes a different list rather than merely a longer one.
function listKeyOf(search) {
  if (search.movieIds) return `ids:${search.movieIds.length}:${search.sort || ""}`;
  return search.url || "empty";
}

// Placeholder grid shown while the first page loads — same .card-list layout as the real cards, so the
// page shows structure immediately (not a lone spinner) and the rail above can keep loading in parallel.
function BrowseSkeleton({ count = 12 }) {
  return (
    <div className="card-list" aria-hidden="true">
      {Array.from({ length: count }).map((_, i) => (
        <div className="card-cell" key={i}>
          <div className="movie-card skeleton-card" />
        </div>
      ))}
    </div>
  );
}

function Browse({ search, userData, setUserData, isAuthReady, simpleStyle }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);
  const [pagination, setPagination] = useState(null);
  const isMobile = useIsMobile();
  const useSimpleStyle = simpleStyle && isMobile;

  // Infinite-scroll modes (server-paginated endpoints flagged via search.infinite):
  // fetch page 1 here, then stream further pages as a bottom sentinel nears the viewport.
  // Covers both URL endpoints and the id-list (Seen/Want) POST endpoint.
  const isInfinite = !!search.infinite && (!!search.url || !!search.movieIds);
  const [loadingMore, setLoadingMore] = useState(false);
  const pageRef = useRef(1);
  const loadingMoreRef = useRef(false);
  // Bumped every time a new result set starts loading. A page-N fetch that was already in flight
  // checks it on arrival and drops its rows — otherwise switching filter mid-flight appended the old
  // search's page 2 onto the new search's page 1 (the append is blind: `prev.concat(more)`).
  const epochRef = useRef(0);
  const moreAbortRef = useRef(null);
  // Read by loadMore, which is deliberately identity-stable (no deps) so the scroll listener never
  // has to re-subscribe.
  const searchRef = useRef(search);
  const hasMoreRef = useRef(false);
  searchRef.current = search;

  // ── Non-infinite path: one fetch returns the full result set (or the legacy envelope). ──
  useEffect(() => {
    // No isAuthReady gate: URL searches are age-gated server-side via the auth cookie, and id-based
    // (Seen/Want) searches are only dispatched post-auth upstream — so the grid fetches in parallel
    // with /API/Me. `search.pending` is the initial sentinel (skeleton stays until NavBar dispatches).
    if (isInfinite || search.pending) return;
    if (!search.url && !search.movieIds) {
      setMovieDataArray([]);
      setPagination(null);
      setLoading(false);
      return;
    }
    setLoading(true);
    const controller = new AbortController();
    const { signal } = controller;
    const fetchPromise = search.movieIds
      ? fetch("/API/GetMoviesByIds", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(search.movieIds),
          signal,
        })
      : fetch(search.url, { signal });
    fetchPromise
      .then((r) => r.json())
      .then((data) => {
        if (data != null && Array.isArray(data.movies) && typeof data.totalCount === "number") {
          setMovieDataArray(data.movies);
          setPagination({ totalCount: data.totalCount, page: data.page, pageSize: data.pageSize });
        } else {
          setMovieDataArray(Array.isArray(data) ? data : (data?.value ?? []));
          setPagination(null);
        }
        setLoading(false);
      })
      .catch((err) => {
        if (err.name !== "AbortError") throw err;
      });
    return () => controller.abort();
  }, [search.url, search.movieIds, search.pending, isInfinite]);

  // ── Infinite path: load the first page, then append on scroll. ──
  useEffect(() => {
    // No isAuthReady gate (see the non-infinite effect above): the first page loads in parallel with auth.
    if (!isInfinite || search.pending) return;
    setLoading(true);
    pageRef.current = 1;
    loadingMoreRef.current = false;
    setLoadingMore(false);
    // Invalidate (and cancel) any page-N fetch still in flight for the PREVIOUS result set.
    epochRef.current += 1;
    moreAbortRef.current?.abort();
    moreAbortRef.current = null;
    const controller = new AbortController();
    fetchInfinitePage(search, 1, controller.signal)
      .then((r) => r.json())
      .then((data) => {
        setMovieDataArray(Array.isArray(data?.movies) ? data.movies : []);
        setPagination(
          typeof data?.totalCount === "number"
            ? { totalCount: data.totalCount, page: 1, pageSize: INFINITE_PAGE_SIZE }
            : null
        );
        setLoading(false);
      })
      .catch((err) => {
        if (err.name !== "AbortError") throw err;
      });
    return () => controller.abort();
  }, [search.url, search.movieIds, search.pending, isInfinite]);

  const hasMore = isInfinite && pagination != null && movieDataArray.length < pagination.totalCount;
  hasMoreRef.current = hasMore;

  // Identity-stable (reads everything through refs) so the scroll listener subscribes once instead of
  // being torn down and re-added on every appended page.
  const loadMore = useCallback(() => {
    if (loadingMoreRef.current || !hasMoreRef.current) return;
    loadingMoreRef.current = true;
    setLoadingMore(true);
    const epoch = epochRef.current;
    const next = pageRef.current + 1;
    const controller = new AbortController();
    moreAbortRef.current = controller;
    fetchInfinitePage(searchRef.current, next, controller.signal)
      .then((r) => r.json())
      .then((data) => {
        if (epochRef.current !== epoch) return; // a different search landed while this was in flight
        const more = Array.isArray(data?.movies) ? data.movies : [];
        if (more.length) {
          pageRef.current = next;
          setMovieDataArray((prev) => prev.concat(more));
        }
      })
      .catch((err) => {
        if (err.name !== "AbortError") throw err;
      })
      .finally(() => {
        if (epochRef.current !== epoch) return; // the new search owns the flags now — don't clobber them
        loadingMoreRef.current = false;
        setLoadingMore(false);
      });
  }, []);

  const { sentinelRef, recheck } = useInfiniteScroll({ enabled: isInfinite, hasMore, onLoadMore: loadMore });

  // After a page lands, re-check the sentinel without re-subscribing: keeps the list filling when the
  // content is still shorter than the viewport, or when the user is parked at the bottom.
  useEffect(() => {
    recheck();
  }, [movieDataArray.length, loading, recheck]);

  const history = useHistory();
  const location = useLocation();
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [selectedKind, setSelectedKind] = useState("movie");
  const [isModalVisible, setIsModalVisible] = useState(false);

  // "Add to playlist" surface: pickerRequest = { items:[{playableId,title}], name } (null = closed).
  // playlistsVersion bumps to nudge the My-Playlists shelf to reload after a create/change.
  const [pickerRequest, setPickerRequest] = useState(null);
  const [playlistsVersion, setPlaylistsVersion] = useState(0);
  const openPlaylistPicker = useCallback((items, name = "") => {
    setPickerRequest({ items: items || [], name });
  }, []);

  // Restore-order reshuffle (back-nav) — memoized so it doesn't rebuild the Map/Set/concat over the
  // full array (and hand a fresh array identity to the memoized grid) on every unrelated render
  // (modal open/close, Seen/Want toggle, scroll append).
  // Changes only when the grid becomes a DIFFERENT list (new search / new id set) — not when it grows
  // by a page. The windowed grid uses it to drop its measured row heights and return to the top.
  const listKey = useMemo(() => listKeyOf(search), [search]);

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

  const handleOpenMovie = useCallback((movieId, kind = "movie") => {
    setSelectedMovieId(movieId);
    setSelectedKind(kind || "movie");
    setIsModalVisible(true);
  }, []);

  const handleCloseModal = useCallback(() => {
    setIsModalVisible(false);
    setSelectedMovieId(null);
  }, []);

  const handleActorSearch = useCallback((actor) => {
    if (!actor || !actor.trim()) {
      return;
    }
    const location = locationRef.current;
    const movieDataArray = movieDataRef.current;

    const trimmedActor = actor.trim();
    const params = new URLSearchParams();
    params.set("mode", "actor");
    params.set("value", trimmedActor);

    const targetSearch = `?${params.toString()}`;
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
    if (!location.search && !gridHasMisc && movieDataArray.length > 0) {
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
  // title). Pushes ?mode=&value= so NavBar's dispatch picks it up; the modal closes via the
  // search-change effect below.
  const handleBrowseSearch = useCallback((mode, value) => {
    const v = (value ?? "").trim();
    if (!mode || !v) return;
    const params = new URLSearchParams();
    params.set("mode", mode);
    params.set("value", v);
    history.push({ pathname: "/", search: `?${params.toString()}` });
  }, [history]);

  // Close modal when search changes (e.g., when actor is clicked)
  useEffect(() => {
    handleCloseModal();
  }, [search, handleCloseModal]);

  // Called by CardList / MovieModal when a movie's viewing state is toggled.
  // Only removes a movie from the displayed list when the action that was deactivated
  // is the exact criterion that defines membership in the current browse mode.
  // e.g. removing from Seen while on the Want list leaves the card visible,
  // because Want-list membership (SetWantToWatch) was not affected.
  const handleToggleViewing = useCallback((movieId, action, isActive) => {
    if (!isActive) {
      const params = new URLSearchParams(locationRef.current.search);
      const mode = params.get("mode");
      if (MODE_ACTION[mode] === action && movieDataRef.current.some((m) => m.id === movieId)) {
        setMovieDataArray((prev) => prev.filter((m) => m.id !== movieId));
        // Keep the infinite-scroll total in sync with the removal so hasMore stays correct
        // (no-op when not infinite, where pagination is null).
        setPagination((prev) => (prev ? { ...prev, totalCount: Math.max(0, prev.totalCount - 1) } : prev));
      }
    }
  }, []);

  const handleMovieUpdated = useCallback((updatedMovie) => {
    setMovieDataArray((prev) =>
      prev.map((m) => (m.id === updatedMovie.id ? updatedMovie : m))
    );
  }, []);

  return (
    <>
      {/* Rail mounts regardless of the grid's loading state so its lineup + posters fetch in parallel
          with the movie grid (it self-gates on a streaming-enabled session), rather than only after. */}
      {!location.search && <NowOnTvRail userData={userData} setUserData={setUserData} />}
      {loading ? (
        <BrowseSkeleton count={isMobile ? 6 : 12} />
      ) : useSimpleStyle ? (
        <SimpleCardList
          movieDataArray={displayMovies}
          userData={userData}
          setUserData={setUserData}
          onMovieClick={handleOpenMovie}
          onToggleViewing={handleToggleViewing}
        />
      ) : (
        <CardList
          movieDataArray={displayMovies}
          userData={userData}
          setUserData={setUserData}
          actorSearch={handleActorSearch}
          activePerson={search.actor}
          onMovieClick={handleOpenMovie}
          onToggleViewing={handleToggleViewing}
          isMobile={isMobile}
          listKey={listKey}
        />
      )}
      {isInfinite && !loading && (
        <div ref={sentinelRef} aria-hidden="true" style={SENTINEL_STYLE} />
      )}
      {/* Only while a page is actually in flight — this used to show for as long as `hasMore` was
          true, i.e. a permanent footer under a list that was sitting perfectly still. */}
      {loadingMore && (
        <div style={LOADING_MORE_STYLE}>Loading more…</div>
      )}
      <Suspense fallback={null}>
        {useSimpleStyle ? (
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
            onAddToPlaylist={openPlaylistPicker}
          />
        ) : (
          <MovieModal
            movieId={selectedMovieId}
            kind={selectedKind}
            open={isModalVisible}
            onClose={handleCloseModal}
            actorSearch={handleActorSearch}
            onBrowse={handleBrowseSearch}
            onOpenTitle={handleOpenMovie}
            movieDataArray={displayMovies}
            userData={userData}
            setUserData={setUserData}
            onToggleViewing={handleToggleViewing}
            onMovieUpdated={handleMovieUpdated}
            onAddToPlaylist={openPlaylistPicker}
          />
        )}
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
