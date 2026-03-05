import { Layout } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";
import BatchInsertPage from "./Pages/BatchInsertPage";

const RANDOM_MOVIES_URL = "/API/GetRandomMovies";

const storedUsername = window.localStorage.getItem("Username");

function escapeODataString(value) {
  return value.replace(/'/g, "''");
}

function buildMoviesUrl(filter) {
  return `/odata/Movies?$filter=${encodeURIComponent(filter)}&$orderby=simpleTitle asc`;
}

function App() {
  const [userData, setUserData] = useState(null);
  const [search, setSearch] = useState({ url: RANDOM_MOVIES_URL });
  const [hasCheckedFirstLogin, setHasCheckedFirstLogin] = useState(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);

  function resetSearch() {
    setSearch({ url: RANDOM_MOVIES_URL });
  }

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        setIsAuthReady(true);
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
    const escaped = escapeODataString(title);
    setSearch({ url: buildMoviesUrl(`contains(simpleTitle,'${escaped}') or contains(title,'${escaped}')`) });
  }

  function ActorSearch(actor) {
    const escaped = escapeODataString(actor);
    setSearch({ url: buildMoviesUrl(`contains(actors,'${escaped}')`) });
  }

  function FirstLetterSearch(firstLetter) {
    if (firstLetter === "#") {
      const digitFilters = "0123456789".split("").map((d) => `startswith(simpleTitle,'${d}')`).join(" or ");
      setSearch({ url: buildMoviesUrl(digitFilters), startsWith: firstLetter });
    } else {
      const escaped = escapeODataString(firstLetter);
      setSearch({ url: buildMoviesUrl(`startswith(simpleTitle,'${escaped}')`), startsWith: firstLetter });
    }
  }

  function MovieIDListSearch(movieIds, restoreOrder = null) {
    if (!movieIds || movieIds.length === 0) {
      setSearch({ url: null, restoreOrder });
      return;
    }
    const idList = movieIds.join(",");
    setSearch({ url: `/odata/Movies?$filter=id in (${idList})&$orderby=simpleTitle asc`, restoreOrder });
  }

  function RestoreMovieIdsSearch(movieIds) {
    MovieIDListSearch(movieIds, movieIds);
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
          resetSearch={resetSearch}
          userData={userData}
          setUserData={setUserData}
          onUserLoggedIn={onUserLoggedIn}
          titleSearch={TitleSearch}
          actorSearch={ActorSearch}
          firstLetterSearch={FirstLetterSearch}
          restoreMovieIdsSearch={RestoreMovieIdsSearch}
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
              <Browse search={search} userData={userData} setUserData={setUserData} isAuthReady={isAuthReady} />
            </Route>
          </Switch>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;

