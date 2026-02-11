import { useEffect, useState } from "react";
import { gql, useQuery } from "@apollo/client";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";
import MovieModal from "./MovieModal";

function Browse({ search, userData, setUserData, actorSearch }) {
  const { data, loading, error } = useQuery(search.query, { variables: search.variables });
  const [selectedMovieId, setSelectedMovieId] = useState(null);
  const [isModalVisible, setIsModalVisible] = useState(false);

  const handleOpenMovie = (movieId) => {
    setSelectedMovieId(movieId);
    setIsModalVisible(true);
  };

  const handleCloseModal = () => {
    setIsModalVisible(false);
    setSelectedMovieId(null);
  };

  if (data) {
    const movieDataArray = data.movies || data.randomMovies;
    return (
      <>
        <CardList
          movieDataArray={movieDataArray}
          userData={userData}
          setUserData={setUserData}
          actorSearch={actorSearch}
          onMovieClick={handleOpenMovie}
        />
        <MovieModal movieId={selectedMovieId} visible={isModalVisible} onClose={handleCloseModal} actorSearch={actorSearch} />
      </>
    );
  } else {
    return <span>Loading</span>;
  }
}

export default Browse;
