import { useEffect, useState } from "react";
import { gql, useQuery } from "@apollo/client";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";

const query = gql`
  query {
    randomMovies {
      id
      actors
      title
      simpleTitle
      rating
      releaseDate
      runtime
      genre
      director
      writer
      plot
      posterLink
      imdbRating
      tomatoRating
      uploadedDate
      removeFromRandom
    }
  }
`;

function Browse({ parameters, userData, setUserData }) {
  const { data, loading, error } = useQuery(query);

  if (data) {
    const movieDataArray = data.movies || data.randomMovies;
    return <CardList movieDataArray={movieDataArray} userData={userData} setUserData={setUserData}></CardList>;
  } else {
    return <span>Loading</span>;
  }
}

export default Browse;
