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

  useEffect(() => {
    const isInitialLoad = !hasHandledInitialLoadRef.current;
    if (isInitialLoad) {
      hasHandledInitialLoadRef.current = true;
    }

    const params = new URLSearchParams(location.search);
    const mode = params.get("mode");
    const value = params.get("value") || "";

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

      const restoreIds = Array.isArray(location.state?.browseMovieIds) ? location.state.browseMovieIds : [];
      const movieIds = restoreIds.map((id) => Number(id)).filter((id) => Number.isInteger(id) && id > 0);
      if (movieIds.length > 0) {
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
  }, [location.search, location.state, userData]);

  return (
    <Layout.Sider>
      <Login userData={userData} setUserData={setUserData} onUserLoggedIn={onUserLoggedIn} />
      <br />
      <SearchTools search={search} />
    </Layout.Sider>
  );
}

export default NavBar;
