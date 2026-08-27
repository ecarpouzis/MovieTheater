import { Layout, Spin } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState, useRef, Suspense } from "react";
import { lazyWithReload as lazy } from "./lazyWithReload";
import { BrowserRouter, Switch, Route } from "react-router-dom";
import NavBar from "./NavBar/NavBar";
import SectionBar from "./catalog/bar/SectionBar";
import PatchedArtifactAlarm from "./NavBar/PatchedArtifactAlarm";
import Browse from "./Pages/Browse/Browse";
import { useMovieSearch } from "./hooks/useMovieSearch";
import { useTheme } from "./hooks/useTheme";
import { MusicPlayerProvider } from "./Music/MusicPlayerContext";
import { readStored, writeStored } from "./utils/storage";

// Route-level code-splitting. The landing (Browse) and the nav shell stay in the main bundle; every
// other page loads on demand, keeping its heavy deps out of the initial download — most notably
// hls.js + the video player (Watch/TV), @dnd-kit (Rate), the boardgames pages, and the ingest/admin
// tooling. Each lazy() below becomes its own chunk, fetched only when its route is first visited.
// lazyWithReload: a chunk fetched after a deploy no longer exists, so a stale tab reloads instead of
// blanking (see lazyWithReload.js).
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
const MusicPage = lazy(() => import("./Pages/Music/MusicPage"));
const MusicNowPlayingPage = lazy(() => import("./Pages/Music/MusicNowPlayingPage"));
const MusicPlaylistsPage = lazy(() => import("./Pages/Music/MusicPlaylistsPage"));
// The MSE probe page (music-mse-plan.md §Phase 1): the gate that has to be run on the phone that
// actually fails, which is why it is a committed route rather than a scratchpad rig. Lazy like every
// other non-landing page, and gated behind ?diag=1 inside itself.
const MusicMseProbe = lazy(() => import("./Music/MusicMseProbe"));
// Family photo album (photos-plan.md §5 Phase 0). Its own chunk like every other section, and
// deliberately routed for everyone: the route existing is not access, the RequireFamilyAlbum policy
// on /API/Photos is — the page renders "family members only" when the server says so.
const PhotosPage = lazy(() => import("./Pages/Photos/PhotosPage"));
const BooksPage = lazy(() => import("./Pages/Books/BooksPage"));

// readStored, not a bare getItem: these run at MODULE SCOPE, where a storage throw (Safari
// private mode, storage disabled) used to be a white screen before a single component mounted.
const storedUsername = readStored("Username");
const storedCardStyle = readStored("CardStyle");

function App() {
  const [userData, setUserData] = useState(null);
  const hasCheckedFirstLoginRef = useRef(false);
  const [isAuthReady, setIsAuthReady] = useState(!storedUsername);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const { theme, toggleTheme } = useTheme();

  const { search, resetSearch, titleSearch, actorSearch, genreSearch, franchiseSearch, firstLetterSearch, titleTypeSearch, ratingSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch } =
    useMovieSearch();

  function applyUserData(responseData, username) {
    setUserData(responseData);
    setIsAuthReady(true);
    writeStored("Username", username ?? responseData.username);
    writeStored("CardStyle", responseData.cardStyle ?? "standard");
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
            writeStored("Username", null);
          }
        });
      }).catch(() => setIsAuthReady(true));
    }
  }

  const simpleStyle = (userData?.cardStyle ?? storedCardStyle) === "simple";

  return (
    <BrowserRouter>
      {/* Mounted at the app root, not on a page: a reverted patched binary (arcade core / Jellyfin
          DLL) must alarm wherever an admin happens to be, since nothing else reports it. Renders
          null for non-admins and is inert until the watchdog reports trouble. */}
      <PatchedArtifactAlarm userData={userData} />
      {/* MusicPlayerProvider mounts the app's single persistent <audio> + the bottom mini-player
          (music-plan.md §2.6): playback must survive route changes, so it lives above the Switch.
          enabled follows hasPassword — streaming is password-only (§3.1), enforced for real by the
          StreamingUser policy on every /API/Music/* route; this just keeps the UI honest instead of
          offering a player whose first request would 401. */}
      <MusicPlayerProvider enabled={!!userData?.hasPassword}>
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
          ratingSearch={ratingSearch}
          restoreMovieIdsSearch={restoreMovieIdsSearch}
          moviesSeenSearch={moviesSeenSearch}
          moviesWantToWatchSearch={moviesWantToWatchSearch}
          collapsed={sidebarCollapsed}
          onCollapse={setSidebarCollapsed}
          isAuthReady={isAuthReady}
          theme={theme}
          toggleTheme={toggleTheme}
        />
        <Layout.Content className="app-content">
          {/* The ONE content-top bar every section shares (R9 S1): tabs · search slot · the
              catalog's pills + ⚙ (portaled in by CatalogHost) · light/dark. Mounted once, above the
              routes, so it is the same element on every page. */}
          <SectionBar userData={userData} theme={theme} toggleTheme={toggleTheme} />
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
              <ChannelGuidePage userData={userData} setUserData={setUserData} />
            </Route>
            <Route path="/arcade" exact>
              <ArcadePage userData={userData} />
            </Route>
            <Route path="/music" exact>
              <MusicPage userData={userData} />
            </Route>
            {/* Full player: lyrics + queue + visualizer (music-plan.md §2.6). Declared before no
                other /music route needs it, but kept exact so /music itself still matches above. */}
            <Route path="/music/now-playing" exact>
              <MusicNowPlayingPage userData={userData} />
            </Route>
            {/* Playlists get their own route rather than a strip above the browse grid, which grew
                unboundedly and pushed the library down the page. */}
            <Route path="/music/playlists" exact>
              <MusicPlaylistsPage userData={userData} />
            </Route>
            {/* Visit-a-URL-on-the-phone diagnostics, not a listening surface: it renders nothing but
                an "on" button unless ?diag=1 has been used (the musicDiag convention). */}
            <Route path="/music/mse-probe" exact>
              <MusicMseProbe />
            </Route>
            {/* NOT exact: the album's views are real sub-routes (/photos/albums/:slug,
                /photos/people/:id, /photos/folders/<path>, …) so they deep-link, share and survive a
                refresh. PhotosPage owns the inner Switch, which keeps the whole section in one
                lazy chunk instead of one per view. */}
            <Route path="/photos">
              <PhotosPage userData={userData} />
            </Route>
            {/* Books (R8): non-exact like /photos. BooksPage owns the inner Switch (/books, /books/explore,
                /books/shelf, /books/novels, /books/kids, /books/admin, /books/read/:itemId). */}
            <Route path="/books">
              <BooksPage userData={userData} setUserData={setUserData} />
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
      </MusicPlayerProvider>
    </BrowserRouter>
  );
}

export default App;
