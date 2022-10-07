import { useEffect, useState } from "react";
import { gql, useQuery } from "@apollo/client";
import { MovieAPI } from "../../MovieAPI";
import CardList from "./CardList";

function Browse({ search, userData, setUserData, actorSearch }) {
  const { data, loading, error } = useQuery(search.query, { variables: search.variables });

  if (data) {
    const movieDataArray = data.movies || data.randomMovies;
    return <CardList movieDataArray={movieDataArray} userData={userData} setUserData={setUserData} actorSearch={actorSearch}></CardList>;
  } else {
    return <span>Loading</span>;
  }
}

export default Browse;
