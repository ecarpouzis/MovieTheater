import { Layout } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState, useRef } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";
import BatchInsertPage from "./Pages/BatchInsertPage";
import UserSettingsPage from "./Pages/UserSettingsPage";
import { useMovieSearch } from "./hooks/useMovieSearch";

const storedUsername = window.localStorage.getItem("Username");

function App() {
  const [userData, setUserData] = useState(null);
  const hasCheckedFirstLoginRef = useRef(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  const { search, resetSearch, titleSearch, actorSearch, firstLetterSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch } =
    useMovieSearch();

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        setIsAuthReady(true);
        window.localStorage.setItem("Username", username);
      });
  }

  if (!hasCheckedFirstLoginRef.current) {
    hasCheckedFirstLoginRef.current = true;
    if (storedUsername) {
      onUserLoggedIn(storedUsername);
    }
  }

  return (
    <BrowserRouter>
      <Layout className="app-layout">
        <NavBar
          search={search}
          resetSearch={resetSearch}
          userData={userData}
          setUserData={setUserData}
          onUserLoggedIn={onUserLoggedIn}
          titleSearch={titleSearch}
          actorSearch={actorSearch}
          firstLetterSearch={firstLetterSearch}
          restoreMovieIdsSearch={restoreMovieIdsSearch}
          moviesSeenSearch={moviesSeenSearch}
          moviesWantToWatchSearch={moviesWantToWatchSearch}
          collapsed={sidebarCollapsed}
          onCollapse={setSidebarCollapsed}
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
            <Route path="/settings" exact>
              <UserSettingsPage userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/">
              <Browse search={search} userData={userData} setUserData={setUserData} isAuthReady={isAuthReady} sidebarCollapsed={sidebarCollapsed} />
            </Route>
          </Switch>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
