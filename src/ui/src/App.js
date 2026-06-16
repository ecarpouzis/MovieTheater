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
import WatchPage from "./Pages/Watch/WatchPage";
import TvPage from "./Pages/Tv/TvPage";
import IngestReviewPage from "./Pages/IngestReview/IngestReviewPage";
import { useMovieSearch } from "./hooks/useMovieSearch";

const storedUsername = window.localStorage.getItem("Username");
const storedCardStyle = window.localStorage.getItem("CardStyle");

function App() {
  const [userData, setUserData] = useState(null);
  const hasCheckedFirstLoginRef = useRef(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  const { search, resetSearch, titleSearch, actorSearch, genreSearch, firstLetterSearch, titleTypeSearch, ratingSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch } =
    useMovieSearch();

  function applyUserData(responseData, username) {
    setUserData(responseData);
    setIsAuthReady(true);
    window.localStorage.setItem("Username", username ?? responseData.username);
    window.localStorage.setItem("CardStyle", responseData.cardStyle ?? "standard");
  }

  //Attempts a login; resolves to { ok } on success, or { ok: false, requiresPassword?, message? }
  //so the Login component can prompt for a password when the account has one.
  function onUserLoggedIn(username, password) {
    return MovieAPI.loginUser(username, password).then((response) => {
      if (!response.ok) {
        setIsAuthReady(true);
        return response
          .json()
          .catch(() => ({}))
          .then((body) => ({ ok: false, status: response.status, ...body }));
      }
      return response.json().then((responseData) => {
        applyUserData(responseData, username);
        return { ok: true };
      });
    });
  }

  if (!hasCheckedFirstLoginRef.current) {
    hasCheckedFirstLoginRef.current = true;
    if (storedUsername) {
      //Restore the session from the auth cookie first — password-protected accounts
      //can't silently re-login. Passwordless accounts fall back to re-login as before.
      MovieAPI.getCurrentUser().then((response) => {
        if (response.ok) {
          return response.json().then((responseData) => applyUserData(responseData, storedUsername));
        }
        return onUserLoggedIn(storedUsername).then((result) => {
          if (!result.ok) {
            window.localStorage.removeItem("Username");
          }
        });
      }).catch(() => setIsAuthReady(true));
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
          titleTypeSearch={titleTypeSearch}
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
              <MoviePage userData={userData} />
            </Route>
            <Route path="/watch/:movieId" exact>
              <WatchPage userData={userData} />
            </Route>
            <Route path="/tv/:channelId?" exact>
              <TvPage userData={userData} />
            </Route>
            <Route path="/insert" exact>
              <InsertPage />
            </Route>
            <Route path="/batchinsert" exact>
              <BatchInsertPage />
            </Route>
            <Route path="/review-ingest" exact>
              <IngestReviewPage userData={userData} />
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
