import { Layout } from "antd";
import { MenuOutlined } from "@ant-design/icons";
import { useState, useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";
import "./NavBar.css";

import SearchTools from "./SearchTools";
import Login from "./Login";

// Custom hook — reusable stateful logic extracted into a standalone function.
// Hooks are the JS equivalent of a small utility class: they hold state and
// side effects, and return values the caller can use.
function useIsMobile(breakpoint = 768) {
  // useState is like a property backed by a private field; setting it schedules a re-render.
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= breakpoint);
  useEffect(() => {
    const handler = () => setIsMobile(window.innerWidth <= breakpoint);
    window.addEventListener("resize", handler);
    // Returning a function from useEffect registers it as a cleanup callback,
    // called when the component unmounts — equivalent to IDisposable.Dispose().
    return () => window.removeEventListener("resize", handler);
  }, [breakpoint]);
  return isMobile;
}

function NavBar({
  search,
  resetSearch,
  userData,
  setUserData,
  onUserLoggedIn,
  titleSearch,
  actorSearch,
  firstLetterSearch,
  restoreMovieIdsSearch,
  moviesSeenSearch,
  moviesWantToWatchSearch,
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

  // useEffect with a dependency array runs the callback whenever any listed value changes
  // — similar to subscribing to a PropertyChanged event for those specific properties.
  // Close the dropdown whenever the URL query string changes (i.e. a search was performed).
  useEffect(() => {
    setDrawerOpen(false);
  }, [location.search]);

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
      title:  (v) => v.trim() ? titleSearch(v)              : resetSearch(),
      actor:  (v) => v.trim() ? actorSearch(v)              : resetSearch(),
      letter: (v) => v.trim() ? firstLetterSearch(v)        : resetSearch(),
      seen:   ()  => userData  ? moviesSeenSearch(userData)  : resetSearch(),
      want:   ()  => userData  ? moviesWantToWatchSearch(userData) : resetSearch(),
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
  }, [location.search, location.pathname, location.state, userData?.username, history, resetSearch, titleSearch, actorSearch, firstLetterSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch]);

  // JSX can be stored in a variable just like any other value and rendered later.
  // The empty tags <> </> are a fragment — a grouping wrapper that emits no DOM element.
  const navContent = (
    <>
      <Login userData={userData} setUserData={setUserData} onUserLoggedIn={onUserLoggedIn} />
      <SearchTools search={search} />
    </>
  );

  // Render entirely different markup for mobile vs. desktop rather than relying on
  // CSS media queries — the isMobile hook drives layout switching at the JS level.
  if (isMobile) {
    return (
      <>
        <div className="navbar-topbar">
          <button className="navbar-menu-btn" onClick={() => setDrawerOpen((o) => !o)}>
            <MenuOutlined />
          </button>
          <button className="navbar-home-btn" onClick={() => history.push("/")}>
            <span className="navbar-home-emoji">🎬</span>
            <span className="navbar-title">Movie Theater</span>
          </button>
          {userData && <span className="navbar-username-badge">{userData.username}</span>}
        </div>

        {drawerOpen && <div className="navbar-overlay" onClick={() => setDrawerOpen(false)} />}

        <div className={`navbar-dropdown${drawerOpen ? " navbar-dropdown--open" : ""}`}>
          {navContent}
        </div>
      </>
    );
  }

  return (
    <Layout.Sider className="navbar-sider">
      <div className="navbar-sider-header">
        <button className="navbar-home-btn" onClick={() => history.push("/")}>
          <span className="navbar-home-emoji">🎬</span>
          <span className="navbar-sider-title">Movie Theater</span>
        </button>
      </div>
      {navContent}
    </Layout.Sider>
  );
}

export default NavBar;
