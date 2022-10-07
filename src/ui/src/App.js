import { Layout } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import { gql } from "@apollo/client";
import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";
import BatchInsertPage from "./Pages/BatchInsertPage";

const storedUsername = window.localStorage.getItem("Username");

const randomMoviesQuery = gql`
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

function App() {
  const [userData, setUserData] = useState(null);
  const [search, setSearch] = useState({ query: randomMoviesQuery, variables: {} });
  const [hasCheckedFirstLogin, setHasCheckedFirstLogin] = useState(false);

  function resetSearch() {
    setSearch({ query: randomMoviesQuery, variables: {} });
  }

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        window.localStorage.setItem("Username", username);
      });
  }

  if (!hasCheckedFirstLogin) {
    setHasCheckedFirstLogin(true);
    if (storedUsername) {
      onUserLoggedIn(storedUsername);
    }
  }

  function TitleSearch(title) {
    const query = gql`
      query ($title: String!) {
        movies(where: { or: [{ simpleTitle: { contains: $title } }, { title: { contains: $title } }] }, order: { simpleTitle: ASC }) {
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
    const variables = { title: title };
    setSearch({ query: query, variables: variables });
  }

  function ActorSearch(actor) {
    const query = gql`
      query ($actor: String!) {
        movies(where: { actors: { contains: $actor } }, order: { simpleTitle: ASC }) {
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
    const variables = { actor: actor };
    setSearch({ query: query, variables: variables });
  }

  function MovieIDListSearch(movieIds) {
    const query = gql`
      query ($movieIds: [Int!]) {
        movies(where: { id: { in: $movieIds } }, order: { simpleTitle: ASC }) {
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
    const variables = { movieIds: movieIds };
    setSearch({ query: query, variables: variables });
  }

  function MoviesSeenSearch() {
    MovieIDListSearch(userData.moviesSeen);
  }

  function MoviesWantToWatchSearch() {
    MovieIDListSearch(userData.moviesToWatch);
  }

  return (
    <BrowserRouter>
      <Layout style={{ height: "100vh", overflow: "hidden" }}>
        <NavBar
          search={search}
          setSearch={setSearch}
          resetSearch={resetSearch}
          userData={userData}
          setUserData={setUserData}
          onUserLoggedIn={onUserLoggedIn}
          titleSearch={TitleSearch}
          actorSearch={ActorSearch}
          moviesSeenSearch={MoviesSeenSearch}
          moviesWantToWatchSearch={MoviesWantToWatchSearch}
        />
        <Layout.Content style={{ height: "100%", overflowY: "auto", paddingRight: "10px" }}>
          <Switch>
            <Route path="/movie/:id" exact>
              <MoviePage />
            </Route>
            <Route path="/insert" exact>
              <InsertPage />
            </Route>
            <Route path="/batchinsert" exact>
              <BatchInsertPage />
            </Route>
            <Route path="/">
              <Browse search={search} userData={userData} setUserData={setUserData} actorSearch={ActorSearch} />
            </Route>
          </Switch>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
