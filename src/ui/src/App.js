import { Layout } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState, useRef } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import BoardGames from "./Pages/BoardGames/BoardGames";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";
import BatchInsertPage from "./Pages/BatchInsertPage";
import BoardgameBatchInsertPage from "./Pages/BoardGames/BoardgameBatchInsertPage";
import { useMovieSearch } from "./hooks/useMovieSearch";

const storedUsername = window.localStorage.getItem("Username");
const storedCardStyle = window.localStorage.getItem("CardStyle");

function App() {
  const [userData, setUserData] = useState(null);
  const hasCheckedFirstLoginRef = useRef(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  const { search, resetSearch, titleSearch, actorSearch, genreSearch, firstLetterSearch, ratingSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch } =
    useMovieSearch();

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        setIsAuthReady(true);
        window.localStorage.setItem("Username", username);
        window.localStorage.setItem("CardStyle", responseData.cardStyle ?? "standard");
      });
  }

  if (!hasCheckedFirstLoginRef.current) {
    hasCheckedFirstLoginRef.current = true;
    if (storedUsername) {
      onUserLoggedIn(storedUsername);
    }
  }

  const simpleStyle = (userData?.cardStyle ?? storedCardStyle) === "simple";
  const enablePagination = userData?.enablePagination ?? false;

  return (
    <BrowserRouter>
      <Layout className="app-layout" hasSider>
        <NavBar
          search={search}
          resetSearch={resetSearch}
          userData={userData}
          setUserData={setUserData}
          onUserLoggedIn={onUserLoggedIn}
          titleSearch={titleSearch}
          actorSearch={actorSearch}
          genreSearch={genreSearch}
          firstLetterSearch={firstLetterSearch}
          ratingSearch={ratingSearch}
          restoreMovieIdsSearch={restoreMovieIdsSearch}
          moviesSeenSearch={moviesSeenSearch}
          moviesWantToWatchSearch={moviesWantToWatchSearch}
          collapsed={sidebarCollapsed}
          onCollapse={setSidebarCollapsed}
          isAuthReady={isAuthReady}
        />
        <Layout.Content className="app-content">
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
            <Route path="/boardgames/batchinsert" exact>
              <BoardgameBatchInsertPage />
            </Route>
            <Route path="/boardgames" exact>
              <BoardGames userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/">
              <Browse search={search} userData={userData} setUserData={setUserData} isAuthReady={isAuthReady} simpleStyle={simpleStyle} enablePagination={enablePagination} />
            </Route>
          </Switch>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
