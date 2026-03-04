import { useEffect, useMemo, useState } from "react";
import { gql, useQuery } from "@apollo/client";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";
import MovieModal from "./MovieModal";

const RATING_MAPS_QUERY = gql`
  query {
    ratingMaps {
      movieRating
      mpaRatingID
    }
  }
`;

function Browse({ search, userData, setUserData }) {
  const { data, loading, error } = useQuery(search.query, { variables: search.variables });
  const { data: ratingMapsData } = useQuery(RATING_MAPS_QUERY);
  const history = useHistory();
  const location = useLocation();
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);

  const ratingToMpaId = useMemo(() => {
    if (!ratingMapsData?.ratingMaps) return null;
    return new Map(ratingMapsData.ratingMaps.map((rm) => [rm.movieRating, rm.mpaRatingID]));
  }, [ratingMapsData]);

  let movieDataArray = data ? data.movies || data.randomMovies : [];

  if (!userData && data?.randomMovies) {
    movieDataArray = movieDataArray.filter((movie) => !movie.removeFromRandom);
  }

  if (data && Array.isArray(search.restoreOrder) && search.restoreOrder.length > 0 && Array.isArray(data.movies)) {
    const movieById = new Map(data.movies.map((movie) => [movie.id, movie]));
    const orderedMovies = search.restoreOrder.map((id) => movieById.get(id)).filter(Boolean);
    const orderedIdSet = new Set(orderedMovies.map((movie) => movie.id));
    const remainingMovies = data.movies.filter((movie) => !orderedIdSet.has(movie.id));
    movieDataArray = [...orderedMovies, ...remainingMovies];
  }

  if (userData?.ageRestriction != null && ratingToMpaId) {
    movieDataArray = movieDataArray.filter((movie) => {
      const mpaId = ratingToMpaId.get(movie.rating);
      return mpaId == null || mpaId <= userData.ageRestriction;
    });
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

  if (data) {
    return (
      <>
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
