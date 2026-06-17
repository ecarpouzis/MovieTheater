import { useCallback, useEffect, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CardList from "./CardList";
import MovieModal from "./MovieModal";
import SimpleCardList from "./SimpleCardList";
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

function Browse({ search, userData, setUserData, isAuthReady, simpleStyle }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);
  const [pagination, setPagination] = useState(null);
  const isMobile = useIsMobile();
  const useSimpleStyle = simpleStyle && isMobile;

  // Infinite-scroll modes (server-paginated endpoints flagged via search.infinite):
  // fetch page 1 here, then stream further pages as a bottom sentinel nears the viewport.
  const isInfinite = !!search.infinite && !!search.url;
  const pageRef = useRef(1);
  const loadingMoreRef = useRef(false);
  const sentinelRef = useRef(null);

  // ── Non-infinite path: one fetch returns the full result set (or the legacy envelope). ──
  useEffect(() => {
    if (!isAuthReady || isInfinite) return;
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
  }, [search.url, search.movieIds, isAuthReady, isInfinite]);

  // ── Infinite path: load the first page, then append on scroll. ──
  useEffect(() => {
    if (!isAuthReady || !isInfinite) return;
    setLoading(true);
    pageRef.current = 1;
    loadingMoreRef.current = false;
    const controller = new AbortController();
    fetch(withPage(search.url, 1, INFINITE_PAGE_SIZE), { signal: controller.signal })
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
  }, [search.url, isAuthReady, isInfinite]);

  const hasMore = isInfinite && pagination != null && movieDataArray.length < pagination.totalCount;

  const loadMore = useCallback(() => {
    if (loadingMoreRef.current || !hasMore) return;
    loadingMoreRef.current = true;
    const next = pageRef.current + 1;
    fetch(withPage(search.url, next, INFINITE_PAGE_SIZE))
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
  }, [hasMore, search.url]);

  // Observe the bottom sentinel; fetch the next page ~one screen before it's reached.
  // Re-created after each append (movieDataArray.length dep) so that if the sentinel is
  // STILL within the root margin after a page loads, the fresh observer's initial callback
  // fires again and chains the next page — a single persistent observer wouldn't re-fire
  // while the sentinel stays continuously intersecting (tall screens / short pages).
  useEffect(() => {
    if (!isInfinite || !hasMore) return;
    const node = sentinelRef.current;
    if (!node) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) loadMore();
      },
      { root: getScrollParent(node), rootMargin: "800px 0px" }
    );
    io.observe(node);
    return () => io.disconnect();
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

    if (!location.search && movieDataArray.length > 0) {
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
      if (modeActionMap[mode] === action) {
        setMovieDataArray((prev) => prev.filter((m) => m.id !== movieId));
      }
    }
  };

  const handleMovieUpdated = (updatedMovie) => {
    setMovieDataArray((prev) =>
      prev.map((m) => (m.id === updatedMovie.id ? updatedMovie : m))
    );
  };

  // Lightweight count header for infinite-scroll modes (no page buttons — the list streams).
  const infiniteBar = isInfinite && pagination ? (
    <div style={{
      display: "flex", alignItems: "center", justifyContent: "center",
      padding: "10px 16px",
      background: "#001529",
      borderBottom: "1px solid #1e3a57",
      borderTop: "1px solid #1e3a57",
    }}>
      <span style={{ color: "#8fa8c0", fontSize: "13px", letterSpacing: "0.3px" }}>
        {pagination.totalCount === 0
          ? "No movies found"
          : `Showing ${movieDataArray.length} of ${pagination.totalCount} movie${pagination.totalCount !== 1 ? "s" : ""}`}
      </span>
    </div>
  ) : null;

  if (loading) {
    return <span>Loading</span>;
  }

  return (
    <>
      {infiniteBar}
      {useSimpleStyle ? (
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
      {isInfinite && (
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
