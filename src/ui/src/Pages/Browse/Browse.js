import { useEffect, useState } from "react";

import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";

function Browse({ search, userData, setUserData }) {
  const [movieDataArray, setMovieDataArray] = useState([]);

  useEffect(() => {
    MovieAPI.getMovies(search)
      .then((response) => response.json())
      .then((responseData) => {
        setMovieDataArray(responseData);
      });
  }, [search]);

  return <CardList movieDataArray={movieDataArray} userData={userData} setUserData={setUserData}></CardList>;
}

export default Browse;
