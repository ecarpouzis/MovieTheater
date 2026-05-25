import { MenuOutlined } from "@ant-design/icons";
import { Layout } from "antd";
import { useState, useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";
import "./NavBar.css";

import SearchTools from "./SearchTools";
import Login from "./Login";
import BoardGameNavContent from "./BoardGameNavContent";
import UserSettingsModal from "./UserSettingsModal";
import useIsMobile from "../hooks/useIsMobile";

function NavBar({
  search,
  resetSearch,
  userData,
  setUserData,
  onUserLoggedIn,
  titleSearch,
  actorSearch,
  firstLetterSearch,
  ratingSearch,
  restoreMovieIdsSearch,
  moviesSeenSearch,
  moviesWantToWatchSearch,
  collapsed,
  onCollapse,
  isAuthReady,
}) {
  // Router objects — think of these as injected services provided by the router.
  // history is used to programmatically navigate; location is the current URL.
  const history = useHistory();
  const location = useLocation();

  // useRef holds a mutable value that persists across renders without triggering
  // a re-render when changed — like a private instance field on a class.
  const hasHandledInitialLoadRef = useRef(false);

  const isMobile = useIsMobile();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [settingsModalOpen, setSettingsModalOpen] = useState(false);

  // useEffect with a dependency array runs the callback whenever any listed value changes
  // — similar to subscribing to a PropertyChanged event for those specific properties.
  // Close the dropdown whenever the URL path or query string changes.
  useEffect(() => {
    setDrawerOpen(false);
    setDropdownOpen(false);
  }, [location.pathname, location.search]);

  useEffect(() => {
    // The ref lets us detect the very first execution of this effect.
    // Unlike a local variable, it survives re-renders without resetting.
    const isInitialLoad = !hasHandledInitialLoadRef.current;
    if (isInitialLoad) {
      hasHandledInitialLoadRef.current = true;
    }

    // Parse the query string — equivalent to HttpUtility.ParseQueryString() in ASP.NET.
    // e.g. "?mode=title&value=Alien" → mode="title", value="Alien"
    const params = new URLSearchParams(location.search);
    const mode = params.get("mode");
    const value = params.get("value") || "";
    const page = params.get("page") || "1";

    if (!mode) {
      // No search mode in the URL. Determine whether this is a hard browser reload
      // (F5 / Ctrl+R) vs. normal in-app navigation, using the browser Navigation API.
      const navigationEntry = window.performance?.getEntriesByType?.("navigation")?.[0];
      const isHardReload = isInitialLoad && navigationEntry?.type === "reload";

      if (isHardReload) {
        // On a hard reload, browseMovieIds in route state (the previous scroll position
        // context) is stale and should be cleared. Route state is like TempData in
        // ASP.NET MVC — it travels with the URL but isn't visible in the address bar.
        // { ...location.state } is a shallow copy (like new Dictionary(existing)),
        // so we can safely delete the key without mutating the original.
        if (location.state?.browseMovieIds) {
          const restState = { ...location.state };
          delete restState.browseMovieIds;
          history.replace({
            pathname: location.pathname,
            search: location.search,
            state: Object.keys(restState).length > 0 ? restState : undefined,
          });
        }

        resetSearch();
        return;
      }

      // On normal back/forward navigation, browseMovieIds in route state carries the
      // list of movie IDs that was on screen before — restore it so the user lands
      // back on the same results.
      const restoreIds = Array.isArray(location.state?.browseMovieIds) ? location.state.browseMovieIds : [];
      const movieIds = restoreIds.map((id) => Number(id)).filter((id) => Number.isInteger(id) && id > 0);
      if (movieIds.length > 0) {
        restoreMovieIdsSearch(movieIds);
        return;
      }

      resetSearch();
      return;
    }

    // Dispatch table — equivalent to a switch statement or Dictionary<string, Action<string>>.
    // Keyed on the URL "mode" param; each entry is a lambda that runs the appropriate search.
    const modeHandlers = {
      title: (v) => (v.trim() ? titleSearch(v) : resetSearch()),
      actor: (v) => (v.trim() ? actorSearch(v) : resetSearch()),
      letter: (v) => (v.trim() ? firstLetterSearch(v) : resetSearch()),
      rating: (v) => (v.trim() ? ratingSearch(v, parseInt(page, 10) || 1) : resetSearch()),
      seen: () => {
        if (!isAuthReady) return;
        userData ? moviesSeenSearch(userData) : resetSearch();
      },
      want: () => {
        if (!isAuthReady) return;
        userData ? moviesWantToWatchSearch(userData) : resetSearch();
      },
    };

    const handler = modeHandlers[mode];
    if (handler) {
      handler(value);
    } else {
      resetSearch();
    }
    // These callbacks are all stable (useCallback in App.js), and history is a stable
    // reference from useHistory(). userData?.username is used intentionally instead of
    // userData to avoid re-running when moviesSeen/moviesToWatch mutate — only a user
    // identity change should re-trigger mode dispatch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    location.search,
    location.pathname,
    location.state,
    userData?.username,
    isAuthReady,
    history,
    resetSearch,
    titleSearch,
    actorSearch,
    firstLetterSearch,
    ratingSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  ]);

  const isBoardGames = location.pathname.startsWith("/boardgames");
  const sectionEmoji = isBoardGames ? "🎲" : "🎬";
  const sectionTitle = isBoardGames ? "Board Games" : "Movie Theater";
  const navThemeClass = isBoardGames ? " navbar-boardgames-theme" : "";

  // JSX can be stored in a variable just like any other value and rendered later.
  // The empty tags <> </> are a fragment — a grouping wrapper that emits no DOM element.
  const navContent = isBoardGames ? (
    <BoardGameNavContent
      userData={userData}
      setUserData={setUserData}
      onUserLoggedIn={onUserLoggedIn}
      setSettingsModalOpen={setSettingsModalOpen}
      search={search}
    />
  ) : (
    <>
      <Login
        userData={userData}
        setUserData={setUserData}
        onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen}
      />
      <SearchTools search={search} userData={userData} />
    </>
  );

  // Render entirely different markup for mobile vs. desktop rather than relying on
  // CSS media queries — the isMobile hook drives layout switching at the JS level.
  if (isMobile) {
    return (
      <>
        <div className={`navbar-topbar${navThemeClass}`}>
          <button className="navbar-menu-btn" onClick={() => setDrawerOpen((o) => !o)}>
            <MenuOutlined />
          </button>
          <div className="navbar-dropdown-wrapper">
            <button className="navbar-home-btn" onClick={() => setDropdownOpen((o) => !o)}>
              <span className="navbar-home-emoji">{sectionEmoji}</span>
              <span className="navbar-title">{sectionTitle} ▼</span>
            </button>
            {dropdownOpen && (
              <div className="navbar-section-dropdown">
                <button className="navbar-section-item" onClick={() => history.push("/")}>
                  🎬 Movie Theater
                </button>
                <button className="navbar-section-item" onClick={() => history.push("/boardgames")}>
                  🎲 Board Games
                </button>
                {userData?.comicSiteAccess && (
                  <button className="navbar-section-item" onClick={() => window.open(userData.comicSiteAccess, "_blank", "noopener,noreferrer")}>
                    📚 Comics
                  </button>
                )}
              </div>
            )}
          </div>
          {userData && <span className="navbar-username-badge">{userData.username}</span>}
        </div>

        {drawerOpen && <div className="navbar-overlay" onClick={() => setDrawerOpen(false)} />}
        {dropdownOpen && <div className="navbar-overlay" onClick={() => setDropdownOpen(false)} style={{ zIndex: 1150 }} />}

        <div className={`navbar-dropdown${drawerOpen ? " navbar-dropdown--open" : ""}${navThemeClass}`}>{navContent}</div>

        <UserSettingsModal 
          open={settingsModalOpen} 
          onClose={() => setSettingsModalOpen(false)} 
          userData={userData}
          setUserData={setUserData}
        />
      </>
    );
  }

  return (
    <>
      <Layout.Sider className={`navbar-sider${navThemeClass}`} trigger={null} collapsible collapsed={collapsed} onCollapse={onCollapse}>
        <div className={`navbar-sider-header${navThemeClass}`}>
          <div className="navbar-dropdown-wrapper">
            <button className="navbar-home-btn" onClick={() => setDropdownOpen((o) => !o)}>
              <span className="navbar-home-emoji">{sectionEmoji}</span>
              <span className="navbar-sider-title">{sectionTitle} ▼</span>
            </button>
            {dropdownOpen && (
              <div className="navbar-section-dropdown navbar-section-dropdown-desktop">
                <button className="navbar-section-item" onClick={() => history.push("/")}>
                  🎬 Movie Theater
                </button>
                <button className="navbar-section-item" onClick={() => history.push("/boardgames")}>
                  🎲 Board Games
                </button>
                {userData?.comicSiteAccess && (
                  <button className="navbar-section-item" onClick={() => window.open(userData.comicSiteAccess, "_blank", "noopener,noreferrer")}>
                    📚 Comics
                  </button>
                )}
              </div>
            )}
          </div>
        </div>
        {navContent}
      </Layout.Sider>
      <UserSettingsModal 
        open={settingsModalOpen} 
        onClose={() => setSettingsModalOpen(false)} 
        userData={userData}
        setUserData={setUserData}
      />
    </>
  );
}

export default NavBar;
