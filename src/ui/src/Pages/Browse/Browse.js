import { useEffect, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CardList from "./CardList";
import MovieModal from "./MovieModal";
import SimpleCardList from "./SimpleCardList";
import SimpleMovieModal from "./SimpleMovieModal";
import useIsMobile from "../../hooks/useIsMobile";

function Browse({ search, userData, setUserData, isAuthReady, simpleStyle }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);
  const isMobile = useIsMobile();
  const useSimpleStyle = simpleStyle && isMobile;

  useEffect(() => {
    if (!isAuthReady) return;
    if (!search.url && !search.movieIds) {
      setMovieDataArray([]);
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
        setMovieDataArray(Array.isArray(data) ? data : (data?.value ?? []));
        setLoading(false);
      })
      .catch((err) => {
        if (err.name !== "AbortError") throw err;
      });
    return () => controller.abort();
  }, [search.url, search.movieIds, isAuthReady]);

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

  if (loading) {
    return <span>Loading</span>;
  }

  return (
    <>
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
          onMovieClick={handleOpenMovie}
          onToggleViewing={handleToggleViewing}
          isMobile={isMobile}
        />
      )}
      {useSimpleStyle ? (
        <SimpleMovieModal
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
        />
      )}
    </>
  );
}

export default Browse;
