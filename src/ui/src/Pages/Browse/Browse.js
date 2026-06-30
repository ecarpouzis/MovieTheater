import { useCallback, useEffect, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CardList from "./CardList";
import MovieModal from "./MovieModal";
import SimpleCardList from "./SimpleCardList";
import NowOnTvRail from "./NowOnTvRail";
import useIsMobile from "../../hooks/useIsMobile";

// Page size for infinite-scroll modes. Matches the server default in GetMoviesByType.
const INFINITE_PAGE_SIZE = 60;

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

// Find the nearest scrollable ancestor so the IntersectionObserver watches the
// right root — desktop scrolls inside `.app-content`, mobile scrolls the window.
function getScrollParent(node) {
  let el = node?.parentElement;
  while (el) {
    const oy = window.getComputedStyle(el).overflowY;
    if ((oy === "auto" || oy === "scroll") && el.scrollHeight > el.clientHeight) return el;
    el = el.parentElement;
  }
  return null; // null root = viewport (mobile / window scroll)
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
  const pageRef = useRef(1);
  const loadingMoreRef = useRef(false);
  const sentinelRef = useRef(null);

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

  const loadMore = useCallback(() => {
    if (loadingMoreRef.current || !hasMore) return;
    loadingMoreRef.current = true;
    const next = pageRef.current + 1;
    fetchInfinitePage(search, next)
      .then((r) => r.json())
      .then((data) => {
        const more = Array.isArray(data?.movies) ? data.movies : [];
        if (more.length) {
          pageRef.current = next;
          setMovieDataArray((prev) => prev.concat(more));
        }
      })
      .finally(() => {
        loadingMoreRef.current = false;
      });
  }, [hasMore, search]);

  // A bottom "sentinel" element drives infinite loading: whenever it comes within ~one screen of
  // the scroll viewport, load the next page. We measure the sentinel's position directly on each
  // scroll (rAF-throttled) rather than via an IntersectionObserver. An observer here proved fragile
  // — its callback only reports intersection *changes*, and a fresh-per-page observer's initial
  // callback raced with layout and could report "not intersecting"; once the user was at the bottom
  // there was no further scroll to correct it, so the chain stalled (most visibly on a short list's
  // final partial page). A direct position check on every scroll has no such transition/timing
  // dependency. loadMore is idempotent (guarded by loadingMoreRef + pageRef), so repeated calls
  // while a fetch is in flight are harmless.
  const PREFETCH_MARGIN = 800; // px — start loading ~one screen before the sentinel is reached.
  useEffect(() => {
    if (!isInfinite || !hasMore) return;
    const node = sentinelRef.current;
    if (!node) return;
    const root = getScrollParent(node);
    const scrollTarget = root || window;
    let scheduled = false;
    const maybeLoad = () => {
      scheduled = false;
      const viewBottom = root ? root.getBoundingClientRect().bottom : window.innerHeight;
      if (node.getBoundingClientRect().top <= viewBottom + PREFETCH_MARGIN) loadMore();
    };
    const onScroll = () => {
      if (scheduled) return;
      scheduled = true;
      requestAnimationFrame(maybeLoad);
    };
    scrollTarget.addEventListener("scroll", onScroll, { passive: true });
    window.addEventListener("resize", onScroll, { passive: true });
    // Initial check — and re-checked after each append via the length dep — so the list auto-fills
    // when the sentinel is already within reach (short content / sitting at the bottom) without
    // waiting for a scroll.
    onScroll();
    return () => {
      scrollTarget.removeEventListener("scroll", onScroll);
      window.removeEventListener("resize", onScroll);
    };
  }, [isInfinite, hasMore, loadMore, movieDataArray.length]);

  const history = useHistory();
  const location = useLocation();
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [selectedKind, setSelectedKind] = useState("movie");
  const [isModalVisible, setIsModalVisible] = useState(false);

  let displayMovies = movieDataArray;
  if (Array.isArray(search.restoreOrder) && search.restoreOrder.length > 0) {
    const movieById = new Map(movieDataArray.map((movie) => [movie.id, movie]));
    const orderedMovies = search.restoreOrder.map((id) => movieById.get(id)).filter(Boolean);
    const orderedIdSet = new Set(orderedMovies.map((movie) => movie.id));
    displayMovies = [...orderedMovies, ...movieDataArray.filter((movie) => !orderedIdSet.has(movie.id))];
  }

  const handleOpenMovie = (movieId, kind = "movie") => {
    setSelectedMovieId(movieId);
    setSelectedKind(kind || "movie");
    setIsModalVisible(true);
  };

  const handleCloseModal = () => {
    setIsModalVisible(false);
    setSelectedMovieId(null);
  };

  const handleActorSearch = (actor) => {
    if (!actor || !actor.trim()) {
      return;
    }

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
  };

  // Generic "jump to a browse search" used by the detail modal's insight chips (franchise, comp
  // title). Pushes ?mode=&value= so NavBar's dispatch picks it up; the modal closes via the
  // search-change effect below.
  const handleBrowseSearch = (mode, value) => {
    const v = (value ?? "").trim();
    if (!mode || !v) return;
    const params = new URLSearchParams();
    params.set("mode", mode);
    params.set("value", v);
    history.push({ pathname: "/", search: `?${params.toString()}` });
  };

  // Close modal when search changes (e.g., when actor is clicked)
  useEffect(() => {
    handleCloseModal();
  }, [search]);

  // Called by CardList / MovieModal when a movie's viewing state is toggled.
  // Only removes a movie from the displayed list when the action that was deactivated
  // is the exact criterion that defines membership in the current browse mode.
  // e.g. removing from Seen while on the Want list leaves the card visible,
  // because Want-list membership (SetWantToWatch) was not affected.
  const modeActionMap = {
    seen: "SetWatched",
    want: "SetWantToWatch",
  };
  const handleToggleViewing = (movieId, action, isActive) => {
    if (!isActive) {
      const params = new URLSearchParams(location.search);
      const mode = params.get("mode");
      if (modeActionMap[mode] === action && movieDataArray.some((m) => m.id === movieId)) {
        setMovieDataArray((prev) => prev.filter((m) => m.id !== movieId));
        // Keep the infinite-scroll total in sync with the removal so hasMore stays correct
        // (no-op when not infinite, where pagination is null).
        setPagination((prev) => (prev ? { ...prev, totalCount: Math.max(0, prev.totalCount - 1) } : prev));
      }
    }
  };

  const handleMovieUpdated = (updatedMovie) => {
    setMovieDataArray((prev) =>
      prev.map((m) => (m.id === updatedMovie.id ? updatedMovie : m))
    );
  };

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
        />
      )}
      {isInfinite && !loading && (
        <div ref={sentinelRef} aria-hidden="true" style={{ height: 1 }} />
      )}
      {hasMore && (
        <div style={{ textAlign: "center", padding: "16px", color: "#8fa8c0", fontSize: "13px" }}>
          Loading more…
        </div>
      )}
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
        />
      )}
    </>
  );
}

export default Browse;
