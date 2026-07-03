import { Layout, Spin } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState, useRef, lazy, Suspense } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import { useMovieSearch } from "./hooks/useMovieSearch";

// Route-level code-splitting. The landing (Browse) and the nav shell stay in the main bundle; every
// other page loads on demand, keeping its heavy deps out of the initial download — most notably
// hls.js + the video player (Watch/TV), @dnd-kit (Rate), the boardgames pages, and the ingest/admin
// tooling. Each lazy() below becomes its own chunk, fetched only when its route is first visited.
const BoardGames = lazy(() => import("./Pages/BoardGames/BoardGames"));
const MoviePage = lazy(() => import("./Pages/MoviePage"));
const InsertPage = lazy(() => import("./Pages/InsertPage"));
const BatchInsertPage = lazy(() => import("./Pages/BatchInsertPage"));
const BoardgameBatchInsertPage = lazy(() => import("./Pages/BoardGames/BoardgameBatchInsertPage"));
const WatchPage = lazy(() => import("./Pages/Watch/WatchPage"));
const TvPage = lazy(() => import("./Pages/Tv/TvPage"));
const ChannelGuidePage = lazy(() => import("./Pages/Tv/ChannelGuidePage"));
const ArcadePage = lazy(() => import("./Pages/Arcade/ArcadePage"));
const ArcadeRoomPage = lazy(() => import("./Pages/Arcade/ArcadeRoomPage"));
const WatchPartyPage = lazy(() => import("./Pages/Tv/WatchPartyPage"));
const IngestReviewPage = lazy(() => import("./Pages/IngestReview/IngestReviewPage"));
const RatePage = lazy(() => import("./Pages/Rate/RatePage"));

const storedUsername = window.localStorage.getItem("Username");
const storedCardStyle = window.localStorage.getItem("CardStyle");

function App() {
  const [userData, setUserData] = useState(null);
  const hasCheckedFirstLoginRef = useRef(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);

  const { search, resetSearch, titleSearch, actorSearch, genreSearch, franchiseSearch, firstLetterSearch, titleTypeSearch, landingSearch, ratingSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch } =
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
          franchiseSearch={franchiseSearch}
          firstLetterSearch={firstLetterSearch}
          titleTypeSearch={titleTypeSearch}
          landingSearch={landingSearch}
          ratingSearch={ratingSearch}
          restoreMovieIdsSearch={restoreMovieIdsSearch}
          moviesSeenSearch={moviesSeenSearch}
          moviesWantToWatchSearch={moviesWantToWatchSearch}
          collapsed={sidebarCollapsed}
          onCollapse={setSidebarCollapsed}
          isAuthReady={isAuthReady}
        />
        <Layout.Content className="app-content">
          <Suspense fallback={<div style={{ display: "flex", justifyContent: "center", padding: "80px 0" }}><Spin size="large" /></div>}>
          <Switch>
            <Route path="/movie/:id" exact>
              <MoviePage userData={userData} />
            </Route>
            <Route path="/watch/:movieId" exact>
              <WatchPage userData={userData} />
            </Route>
            <Route path="/tv/:channelId?" exact>
              <TvPage userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/watch-together/:token" exact>
              <WatchPartyPage userData={userData} />
            </Route>
            <Route path="/channels" exact>
              <ChannelGuidePage />
            </Route>
            <Route path="/arcade" exact>
              <ArcadePage />
            </Route>
            <Route path="/arcade/room/:code" exact>
              <ArcadeRoomPage />
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
            <Route path="/rate" exact>
              <RatePage userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/boardgames/batchinsert" exact>
              <BoardgameBatchInsertPage />
            </Route>
            <Route path="/boardgames" exact>
              <BoardGames userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/">
              <Browse search={search} userData={userData} setUserData={setUserData} isAuthReady={isAuthReady} simpleStyle={simpleStyle} />
            </Route>
          </Switch>
          </Suspense>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
