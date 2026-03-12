import { Layout } from "antd";
import { MenuOutlined } from "@ant-design/icons";
import { useState, useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";

import SearchTools from "./SearchTools";
import Login from "./Login";

function useIsMobile(breakpoint = 768) {
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= breakpoint);
  useEffect(() => {
    const handler = () => setIsMobile(window.innerWidth <= breakpoint);
    window.addEventListener("resize", handler);
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
  const history = useHistory();
  const location = useLocation();
  const hasHandledInitialLoadRef = useRef(false);
  const isMobile = useIsMobile();
  const [drawerOpen, setDrawerOpen] = useState(false);

  // Close the drawer whenever the URL search changes (i.e. user performed a search)
  useEffect(() => {
    setDrawerOpen(false);
  }, [location.search]);

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
  // These callbacks are all stable (useCallback in App.js), so including them here
  // is safe and allows the linter to be satisfied without causing spurious re-runs.
  }, [location.search, userData?.username, resetSearch, titleSearch, actorSearch, firstLetterSearch, restoreMovieIdsSearch, moviesSeenSearch, moviesWantToWatchSearch]);

  const navContent = (
    <>
      <Login userData={userData} setUserData={setUserData} onUserLoggedIn={onUserLoggedIn} />
      <SearchTools search={search} />
    </>
  );

  if (isMobile) {
    return (
      <>
        {/* Fixed top bar */}
        <div
          style={{
            position: "fixed",
            top: 0,
            left: 0,
            right: 0,
            zIndex: 1100,
            background: "#001529",
            display: "flex",
            alignItems: "center",
            padding: "0 16px",
            height: "48px",
            boxShadow: "0 2px 8px rgba(0,0,0,0.35)",
            borderBottom: "1px solid #1e3a57",
          }}
        >
          <button
            onClick={() => setDrawerOpen(true)}
            style={{
              background: "none",
              border: "none",
              cursor: "pointer",
              padding: "8px",
              marginRight: "8px",
              display: "flex",
              alignItems: "center",
              color: "rgba(255,255,255,0.85)",
            }}
          >
            <MenuOutlined style={{ fontSize: "18px" }} />
          </button>
          <span style={{ color: "white", fontWeight: "700", fontSize: "17px", letterSpacing: "0.3px" }}>🎬 Movie Theater</span>
          {userData && (
            <span
              style={{
                color: "#a6adb4",
                marginLeft: "auto",
                fontSize: "12px",
                background: "#1e3a57",
                padding: "2px 10px",
                borderRadius: "10px",
              }}
            >
              {userData.username}
            </span>
          )}
        </div>

        {/* Overlay */}
        {drawerOpen && (
          <div
            onClick={() => setDrawerOpen(false)}
            style={{
              position: "fixed",
              inset: 0,
              zIndex: 1200,
              background: "rgba(0,0,0,0.45)",
            }}
          />
        )}

        {/* Slide-in panel */}
        <div
          style={{
            position: "fixed",
            top: 0,
            left: 0,
            bottom: 0,
            zIndex: 1300,
            width: "280px",
            background: "#001529",
            overflowY: "auto",
            overflowX: "hidden",
            boxShadow: drawerOpen ? "6px 0 16px rgba(0,0,0,0.45)" : "none",
            transform: drawerOpen ? "translateX(0)" : "translateX(-100%)",
            transition: "transform 0.25s ease",
          }}
        >
          <div
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "space-between",
              padding: "0 16px",
              height: "48px",
              borderBottom: "1px solid #1e3a57",
              flexShrink: 0,
            }}
          >
            <span style={{ color: "rgba(255,255,255,0.9)", fontWeight: "700", fontSize: "16px" }}>🎬 Movie Theater</span>
            <button
              onClick={() => setDrawerOpen(false)}
              style={{
                background: "none",
                border: "none",
                cursor: "pointer",
                color: "rgba(255,255,255,0.65)",
                fontSize: "18px",
                lineHeight: 1,
                padding: "4px",
              }}
            >
              ✕
            </button>
          </div>
          {navContent}
        </div>
      </>
    );
  }

  return (
    <Layout.Sider style={{ overflowY: "auto", overflowX: "hidden" }}>
      <div
        style={{
          padding: "14px 16px 12px",
          borderBottom: "1px solid #1e3a57",
          marginBottom: "2px",
        }}
      >
        <span style={{ color: "white", fontWeight: "700", fontSize: "16px", letterSpacing: "0.3px" }}>🎬 Movie Theater</span>
      </div>
      {navContent}
    </Layout.Sider>
  );
}

export default NavBar;
