import { useEffect, useState } from "react";
import { gql, useQuery } from "@apollo/client";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";
import MovieModal from "./MovieModal";

function Browse({ search, userData, setUserData }) {
  const { data, loading, error } = useQuery(search.query, { variables: search.variables });
  const history = useHistory();
  const location = useLocation();
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);
  let movieDataArray = data ? data.movies || data.randomMovies : [];

  function decodePathValue(pathValue) {
    if (!pathValue) {
      return "";
    }

    try {
      return decodeURIComponent(pathValue);
    } catch {
      return "";
    }
  }

  function getBrowseHeaderText() {
    const pathname = location.pathname || "/";

    if (pathname.startsWith("/discover/letter/")) {
      const value = decodePathValue(pathname.replace("/discover/letter/", ""));
      return `Search: movies starting with '${value}'`;
    }

    if (pathname.startsWith("/discover/title/")) {
      const value = decodePathValue(pathname.replace("/discover/title/", ""));
      return `Search: '${value}' in movie titles`;
    }

    if (pathname.startsWith("/discover/person/")) {
      const value = decodePathValue(pathname.replace("/discover/person/", ""));
      return `Search: '${value}' in actor's names`;
    }

    if (pathname.startsWith("/discover/all/person/")) {
      const value = decodePathValue(pathname.replace("/discover/all/person/", ""));
      return `All: movies with actor '${value}'`;
    }

    if (pathname === "/library/watchlist") {
      const wantCount = userData ? userData.moviesToWatch.length : 0;
      return `Watchlist`;
    }

    if (pathname === "/library/watched") {
      const seenCount = userData ? userData.moviesSeen.length : 0;
      return `Seen movies`;
    }

    return "Browse All Movies";
  }

  if (data && Array.isArray(search.restoreOrder) && search.restoreOrder.length > 0 && Array.isArray(data.movies)) {
    const movieById = new Map(data.movies.map((movie) => [movie.id, movie]));
    const orderedMovies = search.restoreOrder.map((id) => movieById.get(id)).filter(Boolean);
    const orderedIdSet = new Set(orderedMovies.map((movie) => movie.id));
    const remainingMovies = data.movies.filter((movie) => !orderedIdSet.has(movie.id));
    movieDataArray = [...orderedMovies, ...remainingMovies];
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
    const targetPathname = `/discover/all/person/${encodeURIComponent(trimmedActor)}`;
    if (location.pathname === targetPathname && !location.search) {
      return;
    }

    if (userData && !location.search && movieDataArray.length > 0 && location.pathname === "/") {
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
      pathname: targetPathname,
      search: "",
    });
  };

  // Close modal when search changes (e.g., when actor is clicked)
  useEffect(() => {
    handleCloseModal();
  }, [search]);

  if (data) {
    const headerText = getBrowseHeaderText();
    const isBrowseAllHeader = headerText === "Browse All Movies";

    return (
      <>
        <div style={{ padding: "10px", fontWeight: "bold", fontSize: "16px", textAlign: isBrowseAllHeader ? "center" : "left" }}>{headerText}</div>
        <CardList
          movieDataArray={movieDataArray}
          userData={userData}
          setUserData={setUserData}
          actorSearch={handleActorSearch}
          onMovieClick={handleOpenMovie}
        />
        <MovieModal
          movieId={selectedMovieId}
          visible={isModalVisible}
          onClose={handleCloseModal}
          actorSearch={handleActorSearch}
          movieDataArray={movieDataArray}
          userData={userData}
          setUserData={setUserData}
        />
      </>
    );
  } else {
    return <span>Loading</span>;
  }
}

export default Browse;
