import { MenuOutlined } from "@ant-design/icons";
import { Layout } from "antd";
import { Suspense, useState, useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { lazyWithReload as lazy } from "../lazyWithReload";
import "./NavBar.css";

import SearchTools from "./SearchTools";
import Login from "./Login";
import BoardGameNavContent from "./BoardGameNavContent";
import ArcadeNavContent from "./ArcadeNavContent";
import MusicNavContent from "./MusicNavContent";
import PhotosNavContent from "./PhotosNavContent";
// Settings/admin modals only open on demand (and admin only for privileged users), so keep them out
// of the entry bundle and load their chunks when first rendered.
const UserSettingsModal = lazy(() => import("./UserSettingsModal"));
const AdminModal = lazy(() => import("./AdminModal"));
// The playlists modal (Movies only) loads on demand when first opened from the sidebar pill.
const MyPlaylistsModal = lazy(() => import("../Pages/Tv/MyPlaylistsModal"));
import useIsMobile from "../hooks/useIsMobile";
import { loadTitleTypes, saveTitleTypes, loadSort, saveSort } from "../hooks/useMovieSearch";
import useShowHiddenPhotos from "../hooks/useShowHiddenPhotos";
// Section nav icons (light variants — the navbar sits on a dark ground). Dark variants for
// light-background contexts live alongside in ../assets/icons/dark/.
import movieTheaterIcon from "../assets/icons/movie-theater.svg";
import tvIcon from "../assets/icons/tv.svg";
import boardGamesIcon from "../assets/icons/board-games.svg";
import comicsIcon from "../assets/icons/comics.svg";
import arcadeIcon from "../assets/icons/joystick.svg";
import musicIcon from "../assets/icons/music.svg";
import photosIcon from "../assets/icons/photos.svg";

// Photos is the one section whose word-mark is a NODE rather than a word: "Photos" alone reads as
// one more library next to Movie Theater, and the second line is what says whose album it is. Every
// other section still passes a plain string, so their markup is unchanged.
const photosWordmark = (
  <span className="navbar-photos-wordmark">
    Photos
    <span className="navbar-photos-wordmark-sub">Family album</span>
  </span>
);

// One row per section, first prefix match wins; the last row (no prefix) is Movies, the fallback.
// This table is the single place a section declares its rail: icon (arcade's joystick glyph was
// dropped in the browse redesign - the switcher menu still shows it), word-mark, theme class,
// data-feature token for theme.css, sider width (arcade's rail is 248px per the design handoff;
// antd's default is 200), and the rail body. Movies has no Content row - its rail is the inline
// Login + SearchTools pair, the one rail that also owns the playlists modal. Adding a section =
// adding a row here plus its tokens in theme.css.
const SECTIONS = [
  { key: "arcade", prefix: "/arcade", icon: null, title: "Arcade", themeClass: " navbar-arcade-theme", siderWidth: 248, Content: ArcadeNavContent },
  { key: "boardgames", prefix: "/boardgames", icon: boardGamesIcon, title: "Board Games", themeClass: " navbar-boardgames-theme", Content: BoardGameNavContent },
  { key: "music", prefix: "/music", icon: musicIcon, title: "Music", themeClass: " navbar-music-theme", Content: MusicNavContent },
  { key: "photos", prefix: "/photos", icon: photosIcon, title: photosWordmark, themeClass: " navbar-photos-theme", Content: PhotosNavContent },
  { key: "movies", icon: movieTheaterIcon, title: "Movie Theater", themeClass: "" },
];

function NavBar({
  search,
  resetSearch,
  userData,
  setUserData,
  onUserLoggedIn,
  titleSearch,
  actorSearch,
  genreSearch,
  franchiseSearch,
  firstLetterSearch,
  titleTypeSearch,
  landingSearch,
  ratingSearch,
  restoreMovieIdsSearch,
  moviesSeenSearch,
  moviesWantToWatchSearch,
  collapsed,
  onCollapse,
  isAuthReady,
  theme,
  toggleTheme,
}) {
  // Router objects — think of these as injected services provided by the router.
  // history is used to programmatically navigate; location is the current URL.
  const history = useHistory();
  const location = useLocation();

  // useRef holds a mutable value that persists across renders without triggering
  // a re-render when changed — like a private instance field on a class.
  const hasHandledInitialLoadRef = useRef(false);

  const isMobile = useIsMobile();
  // The admin show-hidden switch (photos-plan.md Phase 4 addendum). Held here rather than in the
  // photos page because it describes the SESSION, not the view — and because Phase 4 moved it out of
  // member reach entirely: any member may hide a photo, only an admin may see what was hidden.
  const [showHiddenPhotos, setShowHiddenPhotos] = useShowHiddenPhotos();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [settingsModalOpen, setSettingsModalOpen] = useState(false);
  const [adminModalOpen, setAdminModalOpen] = useState(false);
  const [playlistsModalOpen, setPlaylistsModalOpen] = useState(false);

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

    // The Type scope persists across modes via the `types` param. Distinguish: absent (→ the persisted
    // default, Movies unless the user changed it) vs. empty string (→ explicitly all types) vs. a list.
    const typesParam = params.get("types");
    const types =
      typesParam === null
        ? loadTitleTypes()
        : typesParam === ""
        ? []
        : typesParam.split(",").map((t) => t.trim()).filter(Boolean);
    saveTitleTypes(types);

    // Sort-by is a persistent overarching setting like the Type scope: absent in the URL → the persisted
    // value (default "alpha"); present → use and persist it. Threaded into every browse mode below.
    const sortParam = params.get("sort");
    const sort = sortParam || loadSort();
    saveSort(sort);

    // With no other filter active, the default browse is the Type scope itself (random when it's empty).
    const browseDefault = () => (types.length ? titleTypeSearch(types, sort) : resetSearch());

    // The CLEAN landing — no explicit type/sort param in the URL — shows a RANDOM assortment of the
    // scope. The persisted Sort (alpha/IMDb/RT) is a *browse* setting: it only applies once the user
    // actually browses, which always puts a type or sort param in the URL. Any params → sorted browse.
    const isCleanLanding = typesParam === null && sortParam === null;
    const landingGrid = () => (isCleanLanding ? landingSearch(types) : browseDefault());

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

        // A reload re-renders the clean landing's random grid (or the sorted browse if a filter/sort
        // param is in the URL).
        landingGrid();
        return;
      }

      // On normal back/forward navigation, browseMovieIds in route state carries the
      // list of movie IDs that was on screen before — restore it so the user lands
      // back on the same results.
      const restoreIds = Array.isArray(location.state?.browseMovieIds) ? location.state.browseMovieIds : [];
      const movieIds = restoreIds.map((id) => Number(id)).filter((id) => Number.isInteger(id) && id > 0);
      if (movieIds.length > 0) {
        restoreMovieIdsSearch(movieIds, types);
        return;
      }

      // A clean URL is the landing: a RANDOM assortment of the Type scope (the persisted Sort is a
      // browse setting, applied only when a type/sort param is present). LANDING_SEED keeps it stable
      // across re-runs of this effect — e.g. when auth resolves and userData changes — so it doesn't
      // reshuffle out from under the user.
      landingGrid();
      return;
    }

    // Dispatch table — equivalent to a switch statement or Dictionary<string, Action<string>>.
    // Keyed on the URL "mode" param; each entry is a lambda that runs the appropriate search.
    // Every search mode runs within the current Type scope; an empty value falls back to browsing the
    // scope itself. (Type is no longer its own mode — it's the orthogonal `types` param above.)
    const modeHandlers = {
      title: (v) => (v.trim() ? titleSearch(v, types, sort) : browseDefault()),
      actor: (v) => (v.trim() ? actorSearch(v, types, sort) : browseDefault()),
      genre: (v) => (v.trim() ? genreSearch(v, types, sort) : browseDefault()),
      franchise: (v) => (v.trim() ? franchiseSearch(v, types, sort) : browseDefault()),
      letter: (v) => (v.trim() ? firstLetterSearch(v, types, sort) : browseDefault()),
      rating: (v) => (v.trim() ? ratingSearch(v, types, sort) : browseDefault()),
      seen: () => {
        if (!isAuthReady) return;
        userData ? moviesSeenSearch(userData, types, sort) : browseDefault();
      },
      want: () => {
        if (!isAuthReady) return;
        userData ? moviesWantToWatchSearch(userData, types, sort) : browseDefault();
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
    genreSearch,
    franchiseSearch,
    firstLetterSearch,
    titleTypeSearch,
    landingSearch,
    ratingSearch,
    restoreMovieIdsSearch,
    moviesSeenSearch,
    moviesWantToWatchSearch,
  ]);

  const section = SECTIONS.find((sec) => sec.prefix && location.pathname.startsWith(sec.prefix))
    ?? SECTIONS[SECTIONS.length - 1];
  const isMovies = section.key === "movies";
  const isPhotos = section.key === "photos";
  const sectionIcon = section.icon;
  const sectionTitle = section.title;
  const navThemeClass = section.themeClass;

  // Publish the active feature to <html> so theme.css re-tints its tokens (accent, sidebar,
  // content bg) per section. Runs on every route change.
  useEffect(() => {
    document.documentElement.dataset.feature = section.key;
  }, [section.key]);

  // Sun/moon light-dark toggle — present on every feature's header/top bar.
  const themeToggleButton = (
    <button
      className="navbar-theme-toggle"
      onClick={toggleTheme}
      title={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
      aria-label="Toggle light/dark theme"
    >
      {theme === "dark" ? "☀" : "☾"}
    </button>
  );

  // Session teardown lives here rather than in the per-section nav panels: Log Out is part of the
  // shared footer now, so Movies / Board Games / Arcade no longer each carry a copy of this.
  function logoutUser() {
    fetch("/API/Logout", { method: "POST" }).finally(() => {
      setUserData();
      window.localStorage.clear();
    });
  }

  // The photos section's one nav control (photos-plan.md §2.9 / Phase 4 addendum): reveal the assets
  // a family member has hidden. Admin-only ON TOP of family membership — the same two-part test the
  // server applies, restated here only so an unusable control is not drawn. It is a courtesy, never
  // the gate: /API/Photos ignores includeHidden from a non-admin no matter what this checkbox says,
  // which is why a member editing localStorage gains nothing.
  const photosNavControls = isPhotos && userData?.familyAlbum && userData?.hasPassword && userData?.isAdmin && (
    <div className="navbar-photos-controls">
      <label className="navbar-photos-toggle">
        <input
          type="checkbox"
          checked={showHiddenPhotos}
          onChange={(e) => setShowHiddenPhotos(e.target.checked)}
        />
        Show hidden photos
      </label>
      <span className="navbar-photos-hint">
        Hidden photos stay exactly where they are on disk — this only changes what the album shows.
      </span>
    </div>
  );

  // Rail footer — theme row, then Log Out beneath it. Shared by the mobile drawer and the desktop
  // sider so the two can't drift.
  const navFooter = (
    <div className="navbar-footer">
      {photosNavControls}
      <div className="navbar-theme-row">
        <span className="navbar-theme-label">{theme === "dark" ? "Dark" : "Light"} mode</span>
        {themeToggleButton}
      </div>
      {userData && (
        <button className="logout-button" onClick={logoutUser}>
          Log Out
        </button>
      )}
    </div>
  );

  // The section switcher's items — shared by the mobile and desktop dropdowns so they can't drift.
  // Order: Movie Theater, TV, Board Games, Comics. "TV" is the former Channels page (its guide is the
  // primary way into the TV feature); the old standalone /tv button was removed. Library Review moved
  // out of here to the user panel (between the settings and admin icons).
  const sectionMenuItems = (
    <>
      <button className="navbar-section-item" onClick={() => history.push("/")}>
        <img className="navbar-section-icon" src={movieTheaterIcon} alt="" /> Movie Theater
        <span className="navbar-hue-dot" style={{ background: "#4A90E2" }} />
      </button>
      {userData?.hasPassword && (
        <button className="navbar-section-item" onClick={() => history.push("/channels")}>
          <img className="navbar-section-icon" src={tvIcon} alt="" /> TV
          <span className="navbar-hue-dot" style={{ background: "#38B6C9" }} />
        </button>
      )}
      {userData?.hasPassword && (
        <button className="navbar-section-item" onClick={() => history.push("/arcade")}>
          <img className="navbar-section-icon" src={arcadeIcon} alt="" /> Arcade
          <span className="navbar-hue-dot" style={{ background: "#9A7BD4" }} />
        </button>
      )}
      {userData?.hasPassword && (
        <button className="navbar-section-item" onClick={() => history.push("/music")}>
          <img className="navbar-section-icon" src={musicIcon} alt="" /> Music
          <span className="navbar-hue-dot" style={{ background: "#C9484F" }} />
        </button>
      )}
      {/* Family photo album (photos-plan.md §2.1): hidden by default and shown only to a
          family-flagged user — including from admins, who are not implicitly members. Hiding the
          entry is a courtesy, not the gate: every /API/Photos route re-checks the flag server-side,
          so typing the URL gets a non-member a 403 and nothing else.
          hasPassword mirrors the policy's second half (§3 Phase 0 addendum): the album additionally
          requires a password-verified session, so showing the entry to a passwordless member would
          only offer them a dead end. */}
      {userData?.familyAlbum && userData?.hasPassword && (
        <button className="navbar-section-item" onClick={() => history.push("/photos")}>
          <img className="navbar-section-icon" src={photosIcon} alt="" /> Photos
          <span className="navbar-hue-dot" style={{ background: "#7FA648" }} />
        </button>
      )}
      <button className="navbar-section-item" onClick={() => history.push("/boardgames")}>
        <img className="navbar-section-icon" src={boardGamesIcon} alt="" /> Board Games
        <span className="navbar-hue-dot" style={{ background: "#2E9E63" }} />
      </button>
      {userData?.comicSiteAccess && (
        <button className="navbar-section-item" onClick={() => window.open(userData.comicSiteAccess, "_blank", "noopener,noreferrer")}>
          <img className="navbar-section-icon" src={comicsIcon} alt="" /> Comics
          <span className="navbar-hue-dot" style={{ background: "#D98936" }} />
        </button>
      )}
    </>
  );

  // Every section rail takes the same props (boardgames also reads `search`; the rest ignore it).
  // Movies is the fallback: the inline Login + SearchTools pair.
  const navContent = section.Content ? (
    <section.Content
      userData={userData}
      onUserLoggedIn={onUserLoggedIn}
      setSettingsModalOpen={setSettingsModalOpen}
      setAdminModalOpen={setAdminModalOpen}
      search={search}
    />
  ) : (
    <>
      <Login
        userData={userData}
        onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen}
        setAdminModalOpen={setAdminModalOpen}
        onOpenPlaylists={() => setPlaylistsModalOpen(true)}
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
              {sectionIcon && <img className="navbar-home-icon" src={sectionIcon} alt="" />}
              <span className="navbar-title">{sectionTitle} ▼</span>
            </button>
            {dropdownOpen && (
              <div className="navbar-section-dropdown">{sectionMenuItems}</div>
            )}
          </div>
          {userData && <span className="navbar-username-badge">{userData.username}</span>}
        </div>

        {drawerOpen && <div className="navbar-overlay" onClick={() => setDrawerOpen(false)} />}
        {dropdownOpen && <div className="navbar-overlay" onClick={() => setDropdownOpen(false)} style={{ zIndex: 1150 }} />}

        <div className={`navbar-dropdown${drawerOpen ? " navbar-dropdown--open" : ""}${navThemeClass}`}>
          {navContent}
          {navFooter}
        </div>

        <Suspense fallback={null}>
          <UserSettingsModal
            open={settingsModalOpen}
            onClose={() => setSettingsModalOpen(false)}
            userData={userData}
            setUserData={setUserData}
          />
          <AdminModal open={adminModalOpen} onClose={() => setAdminModalOpen(false)} />
          {isMovies && (
            <MyPlaylistsModal open={playlistsModalOpen} onClose={() => setPlaylistsModalOpen(false)} userData={userData} />
          )}
        </Suspense>
      </>
    );
  }

  return (
    <>
      {/* NavBar.css mirrors the per-section width in --sider-width so the switcher dropdown spans
          the right width. */}
      <Layout.Sider className={`navbar-sider${navThemeClass}`} width={section.siderWidth ?? 200} trigger={null} collapsible collapsed={collapsed} onCollapse={onCollapse}>
        <div className="navbar-sider-inner">
          <div className={`navbar-sider-header${navThemeClass}`}>
            <div className="navbar-dropdown-wrapper">
              <button className="navbar-home-btn" onClick={() => setDropdownOpen((o) => !o)}>
                {sectionIcon && <img className="navbar-home-icon" src={sectionIcon} alt="" />}
                <span className="navbar-sider-title">{sectionTitle} <span className="navbar-caret">▼</span></span>
              </button>
              {dropdownOpen && (
                <div className="navbar-section-dropdown navbar-section-dropdown-desktop">{sectionMenuItems}</div>
              )}
            </div>
          </div>
          {navContent}
          {navFooter}
        </div>
      </Layout.Sider>
      <Suspense fallback={null}>
        <UserSettingsModal
          open={settingsModalOpen}
          onClose={() => setSettingsModalOpen(false)}
          userData={userData}
          setUserData={setUserData}
        />
        <AdminModal open={adminModalOpen} onClose={() => setAdminModalOpen(false)} />
        {isMovies && (
          <MyPlaylistsModal open={playlistsModalOpen} onClose={() => setPlaylistsModalOpen(false)} userData={userData} />
        )}
      </Suspense>
    </>
  );
}

export default NavBar;
