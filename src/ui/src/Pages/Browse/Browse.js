import { useEffect, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CardList from "./CardList";
import MovieModal from "./MovieModal";
import SimpleCardList from "./SimpleCardList";
import useIsMobile from "../../hooks/useIsMobile";

function Browse({ search, userData, setUserData, isAuthReady, simpleStyle, enablePagination }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);
  const [pagination, setPagination] = useState(null);
  const isMobile = useIsMobile();
  const useSimpleStyle = simpleStyle && isMobile;

  useEffect(() => {
    if (!isAuthReady) return;
    if (!search.url && !search.movieIds) {
      setMovieDataArray([]);
      setPagination(null);
      setLoading(false);
      return;
    }
    setLoading(true);
    const controller = new AbortController();
    const { signal } = controller;
    let effectiveUrl = search.url;
    if (!enablePagination && effectiveUrl) {
      const urlObj = new URL(effectiveUrl, window.location.origin);
      if (urlObj.searchParams.has("pageSize")) {
        urlObj.searchParams.set("pageSize", "0");
        urlObj.searchParams.delete("page");
        effectiveUrl = urlObj.pathname + urlObj.search;
      }
    }
    const fetchPromise = search.movieIds
      ? fetch("/API/GetMoviesByIds", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(search.movieIds),
          signal,
        })
      : fetch(effectiveUrl, { signal });
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
  }, [search.url, search.movieIds, isAuthReady, enablePagination]);

  const history = useHistory();
  const location = useLocation();
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);

  let displayMovies = movieDataArray;
  if (Array.isArray(search.restoreOrder) && search.restoreOrder.length > 0) {
    const movieById = new Map(movieDataArray.map((movie) => [movie.id, movie]));
    const orderedMovies = search.restoreOrder.map((id) => movieById.get(id)).filter(Boolean);
    const orderedIdSet = new Set(orderedMovies.map((movie) => movie.id));
    displayMovies = [...orderedMovies, ...movieDataArray.filter((movie) => !orderedIdSet.has(movie.id))];
  }

  const handleOpenMovie = (movieId) => {
    setSelectedMovieId(movieId);
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

  const goToPage = (newPage) => {
    const params = new URLSearchParams(location.search);
    if (newPage > 1) {
      params.set("page", String(newPage));
    } else {
      params.delete("page");
    }
    history.push({ pathname: "/", search: `?${params.toString()}` });
    window.scrollTo({ top: 0, behavior: "smooth" });
  };

  const paginationBar = pagination ? (() => {
    const { totalCount, page, pageSize } = pagination;
    const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
    const start = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
    const end = Math.min(page * pageSize, totalCount);
    const hasPrev = page > 1;
    const hasNext = page < totalPages;

    const navButtonStyle = {
      background: "#1890ff",
      border: "1px solid #1890ff",
      color: "white",
      borderRadius: "4px",
      padding: "6px 16px",
      cursor: "pointer",
      fontSize: "13px",
      fontWeight: 600,
      letterSpacing: "0.3px",
      transition: "opacity 0.15s",
    };

    const pageNumStyle = (isActive) => ({
      background: isActive ? "#1890ff" : "transparent",
      border: isActive ? "1px solid #1890ff" : "1px solid rgba(255,255,255,0.15)",
      color: isActive ? "white" : "#8fa8c0",
      borderRadius: "4px",
      padding: "4px 10px",
      cursor: isActive ? "default" : "pointer",
      fontSize: "13px",
      fontWeight: isActive ? 700 : 400,
      minWidth: "32px",
      textAlign: "center",
      transition: "background 0.15s, color 0.15s",
    });

    const ellipsisStyle = { color: "#8fa8c0", fontSize: "13px", userSelect: "none" };

    // Build the visible page numbers: up to 5 around current, always include last page
    let pageNumbers = [];
    if (totalPages > 1) {
      const maxVisible = 5;
      let rangeStart = Math.max(1, page - Math.floor(maxVisible / 2));
      let rangeEnd = rangeStart + maxVisible - 1;
      if (rangeEnd > totalPages) {
        rangeEnd = totalPages;
        rangeStart = Math.max(1, rangeEnd - maxVisible + 1);
      }
      for (let i = rangeStart; i <= rangeEnd; i++) {
        pageNumbers.push(i);
      }
      // Ensure last page is always reachable
      if (pageNumbers[pageNumbers.length - 1] !== totalPages) {
        pageNumbers.push("...");
        pageNumbers.push(totalPages);
      }
      // Ensure first page is always reachable
      if (pageNumbers[0] !== 1) {
        pageNumbers.unshift("...");
        pageNumbers.unshift(1);
      }
    }

    const pageNumberElements = pageNumbers.map((p, idx) =>
      p === "..." ? (
        <span key={`ellipsis-${idx}`} style={ellipsisStyle}>…</span>
      ) : (
        <button
          key={p}
          onClick={() => { if (p !== page) goToPage(p); }}
          style={pageNumStyle(p === page)}
        >
          {p}
        </button>
      )
    );

    const infoText = (
      <span style={{ color: "#8fa8c0", fontSize: "13px", letterSpacing: "0.3px" }}>
        {totalCount === 0
          ? "No movies found"
          : totalPages > 1
            ? `Showing ${start}–${end} of ${totalCount} movies (Page ${page} of ${totalPages})`
            : `${totalCount} movie${totalCount !== 1 ? "s" : ""} found`}
      </span>
    );

    return (
      <div style={{
        display: "flex", alignItems: "center", justifyContent: "center", gap: "8px",
        padding: "10px 16px",
        background: "#001529",
        borderBottom: "1px solid #1e3a57",
        borderTop: "1px solid #1e3a57",
        flexWrap: "wrap",
      }}>
        {infoText}
        {hasPrev && (
          <button onClick={() => goToPage(page - 1)} style={navButtonStyle}>
            ← Prev
          </button>
        )}
        {pageNumberElements}
        {hasNext && (
          <button onClick={() => goToPage(page + 1)} style={navButtonStyle}>
            Next →
          </button>
        )}
      </div>
    );
  })() : null;

  if (loading) {
    return <span>Loading</span>;
  }

  return (
    <>
      {enablePagination && paginationBar}
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
      {enablePagination && paginationBar}
      {useSimpleStyle ? (
        <MovieModal
          movieId={selectedMovieId}
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
