import { Layout } from "antd";
import { useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";

import SearchTools from "./SearchTools";
import Login from "./Login";

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
  const history = useHistory();
  const location = useLocation();
  const hasHandledInitialLoadRef = useRef(false);

  function decodePathValue(pathValue) {
    if (!pathValue) {
      return "";
    }

    try {
      return decodeURIComponent(pathValue);
    } catch {
      return "";
    }
  }

  function getNavigationState() {
    const pathname = location.pathname || "/";

    if (pathname.startsWith("/discover/title/")) {
      const value = decodePathValue(pathname.replace("/discover/title/", ""));
      return { mode: "title", value };
    }

    if (pathname.startsWith("/discover/person/")) {
      const value = decodePathValue(pathname.replace("/discover/person/", ""));
      return { mode: "actor", value };
    }

    if (pathname.startsWith("/discover/all/person/")) {
      const value = decodePathValue(pathname.replace("/discover/all/person/", ""));
      return { mode: "actor", value };
    }

    if (pathname.startsWith("/discover/letter/")) {
      const value = decodePathValue(pathname.replace("/discover/letter/", ""));
      return { mode: "letter", value };
    }

    if (pathname === "/library/watched") {
      return { mode: "seen", value: "" };
    }

    if (pathname === "/library/watchlist") {
      return { mode: "want", value: "" };
    }

    if (pathname === "/") {
      return { mode: null, value: "" };
    }

    // Backward compatibility for previous URL schemes
    if (pathname.startsWith("/search/title/")) {
      const value = decodePathValue(pathname.replace("/search/title/", ""));
      return { mode: "title", value };
    }

    if (pathname.startsWith("/search/actor/")) {
      const value = decodePathValue(pathname.replace("/search/actor/", ""));
      return { mode: "actor", value };
    }

    if (pathname.startsWith("/browse/letter/")) {
      const value = decodePathValue(pathname.replace("/browse/letter/", ""));
      return { mode: "letter", value };
    }

    if (pathname === "/browse/seen") {
      return { mode: "seen", value: "" };
    }

    if (pathname === "/browse/want") {
      return { mode: "want", value: "" };
    }

    const params = new URLSearchParams(location.search);
    const mode = params.get("mode");
    const value = params.get("value") || "";
    return { mode, value };
  }

  useEffect(() => {
    const isInitialLoad = !hasHandledInitialLoadRef.current;
    if (isInitialLoad) {
      hasHandledInitialLoadRef.current = true;
    }
    const { mode, value } = getNavigationState();

    if (!mode) {
      const navigationEntry = window.performance?.getEntriesByType?.("navigation")?.[0];
      const isHardReload = isInitialLoad && navigationEntry?.type === "reload";

      if (isHardReload) {
        if (location.state?.browseMovieIds) {
          const { browseMovieIds, ...restState } = location.state;
          history.replace({
            pathname: location.pathname,
            search: location.search,
            state: Object.keys(restState).length > 0 ? restState : undefined,
          });
        }

        resetSearch();
        return;
      }

      const restoreIds = userData && Array.isArray(location.state?.browseMovieIds) ? location.state.browseMovieIds : [];
      const movieIds = restoreIds.map((id) => Number(id)).filter((id) => Number.isInteger(id) && id > 0);
      if (userData && movieIds.length > 0) {
        restoreMovieIdsSearch(movieIds);
        return;
      }

      resetSearch();
      return;
    }

    if (mode === "title") {
      if (value.trim()) {
        titleSearch(value);
      } else {
        resetSearch();
      }
      return;
    }

    if (mode === "actor") {
      if (value.trim()) {
        actorSearch(value);
      } else {
        resetSearch();
      }
      return;
    }

    if (mode === "letter") {
      if (value.trim()) {
        firstLetterSearch(value);
      } else {
        resetSearch();
      }
      return;
    }

    if (mode === "seen") {
      if (userData) {
        moviesSeenSearch();
      } else {
        resetSearch();
      }
      return;
    }

    if (mode === "want") {
      if (userData) {
        moviesWantToWatchSearch();
      } else {
        resetSearch();
      }
      return;
    }

    resetSearch();
  }, [location.pathname, location.search, location.state, userData]);

  return (
    <Layout.Sider>
      <Login userData={userData} setUserData={setUserData} onUserLoggedIn={onUserLoggedIn} />
      <br />
      <SearchTools search={search} />
    </Layout.Sider>
  );
}

export default NavBar;
