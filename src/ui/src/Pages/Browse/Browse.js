import { useEffect, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";
import MovieModal from "./MovieModal";

function Browse({ search, userData, setUserData }) {
  const [movieDataArray, setMovieDataArray] = useState([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!search.url) {
      setMovieDataArray([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    fetch(search.url)
      .then((r) => r.json())
      .then((data) => {
        setMovieDataArray(Array.isArray(data) ? data : (data?.value ?? []));
        setLoading(false);
      });
  }, [search.url, userData?.username]);

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

  if (loading) {
    return <span>Loading</span>;
  }

  return (
    <>
      <CardList
        movieDataArray={displayMovies}
        userData={userData}
        setUserData={setUserData}
        actorSearch={handleActorSearch}
        onMovieClick={handleOpenMovie}
      />
      <MovieModal
        movieId={selectedMovieId}
        open={isModalVisible}
        onClose={handleCloseModal}
        actorSearch={handleActorSearch}
        movieDataArray={displayMovies}
        userData={userData}
        setUserData={setUserData}
      />
    </>
  );
}

export default Browse;

