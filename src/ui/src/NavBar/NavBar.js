import { MenuOutlined } from "@ant-design/icons";
import { Layout } from "antd";
import { Suspense, useState, useEffect, useRef } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { lazyWithReload as lazy } from "../lazyWithReload";
import "./NavBar.css";

import MoviesSiderRail from "../Pages/Browse/MoviesSiderRail";
import Login from "./Login";
import BoardGameNavContent from "./BoardGameNavContent";
import ArcadeNavContent from "./ArcadeNavContent";
import MusicNavContent from "./MusicNavContent";
import PhotosNavContent from "./PhotosNavContent";
import TvNavContent from "./TvNavContent";
// Settings/admin modals only open on demand (and admin only for privileged users), so keep them out
// of the entry bundle and load their chunks when first rendered.
const UserSettingsModal = lazy(() => import("./UserSettingsModal"));
// The playlists modal (Movies only) loads on demand when first opened from the sidebar pill.
const MyPlaylistsModal = lazy(() => import("../Pages/Tv/MyPlaylistsModal"));
import useIsMobile from "../hooks/useIsMobile";
import { loadTitleTypes, saveTitleTypes, loadSort, saveSort } from "../hooks/useMovieSearch";
import { requestSectionSearch } from "../catalog/bar/useSlot";
import { parseFacetState, facetStateKey } from "../catalog/rail/facetUrl";
import {
  MOVIES_PARSE_SPEC, isPlainMoviesSearch, legacyToFacetSearch, markMoviesSeeded, moviesFilterParams, myListsOf,
  seededMoviesSearch, sessionStorageOrNull, typesOf,
} from "../Pages/Browse/moviesFacetSpec";
import useShowHiddenPhotos from "../hooks/useShowHiddenPhotos";
// Section nav icons (light variants — the navbar sits on a dark ground). Dark variants for
// light-background contexts live alongside in ../assets/icons/dark/.
import movieTheaterIcon from "../assets/icons/movie-theater.svg";
import tvIcon from "../assets/icons/tv.svg";
import boardGamesIcon from "../assets/icons/board-games.svg";
import comicsIcon from "../assets/icons/comics.svg";
import BooksNavContent from "./BooksNavContent";
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
  // TV (R9 S1c): its own section — the channels pages used to fall through to the movies rail.
  { key: "tv", prefix: "/channels", icon: tvIcon, title: "TV", themeClass: " navbar-tv-theme", Content: TvNavContent },
  { key: "arcade", prefix: "/arcade", icon: null, title: "Arcade", themeClass: " navbar-arcade-theme", siderWidth: 248, Content: ArcadeNavContent },
  { key: "boardgames", prefix: "/boardgames", icon: boardGamesIcon, title: "Board Games", themeClass: " navbar-boardgames-theme", Content: BoardGameNavContent },
  { key: "music", prefix: "/music", icon: musicIcon, title: "Music", themeClass: " navbar-music-theme", Content: MusicNavContent },
  { key: "photos", prefix: "/photos", icon: photosIcon, title: photosWordmark, themeClass: " navbar-photos-theme", Content: PhotosNavContent },
  { key: "books", prefix: "/books", icon: comicsIcon, title: "Books", themeClass: " navbar-books-theme", siderWidth: 280, Content: BooksNavContent },
  { key: "movies", icon: movieTheaterIcon, title: "Movie Theater", themeClass: "" },
];

function NavBar({
  search,
  userData,
  setUserData,
  onUserLoggedIn,
  facetSearch,
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
  // The signature of the last search actually dispatched — see the guard in the URL effect.
  const lastDispatchSigRef = useRef(null);

  const isMobile = useIsMobile();
  // The admin show-hidden switch (photos-plan.md Phase 4 addendum). Held here rather than in the
  // photos page because it describes the SESSION, not the view — and because Phase 4 moved it out of
  // member reach entirely: any member may hide a photo, only an admin may see what was hidden.
  const [showHiddenPhotos, setShowHiddenPhotos] = useShowHiddenPhotos();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const [settingsModalOpen, setSettingsModalOpen] = useState(false);
  const [playlistsModalOpen, setPlaylistsModalOpen] = useState(false);

  // useEffect with a dependency array runs the callback whenever any listed value changes
  // — similar to subscribing to a PropertyChanged event for those specific properties.
  // Close the dropdown whenever the URL path or query string changes.
  useEffect(() => {
    setDrawerOpen(false);
    setDropdownOpen(false);
  }, [location.pathname, location.search]);

  useEffect(() => {
    // The movies dispatcher belongs to the movies section only. Every other section owns its own
    // URL params and none of them is a movie search; until R8 this effect re-ran the movies
    // browseDefault() under /photos, /music, /books on every param change, a fetch nobody looked at.
    if (SECTIONS.some((sec) => sec.prefix && location.pathname.startsWith(sec.prefix))) return;

    // The ref lets us detect the very first execution of this effect.
    // Unlike a local variable, it survives re-renders without resetting.
    const isInitialLoad = !hasHandledInitialLoadRef.current;
    if (isInitialLoad) {
      hasHandledInitialLoadRef.current = true;
    }

    // 1. A pre-S2 link (?mode=&value=&types= — the old rail's vocabulary, the modal's chips, old
    //    bookmarks) is rewritten ONCE into the facet form it means; the effect re-runs on the new URL.
    const legacy = legacyToFacetSearch(location.search);
    if (legacy != null) {
      markMoviesSeeded(sessionStorageOrNull());
      history.replace({ pathname: location.pathname, search: legacy, state: location.state });
      return;
    }
    // 2. A clean landing gets the persisted Type scope as chips (Movies by default), once per tab
    //    session — so a cleared chip later in the session means "all types" and stays cleared.
    const seeded = seededMoviesSearch(location.search, loadTitleTypes(), sessionStorageOrNull());
    if (seeded != null) {
      history.replace({ pathname: location.pathname, search: seeded, state: location.state });
      return;
    }

    // The URL IS the filter (R9 S2): `q/f/x/y/my` parsed by the movies spec. The Type scope persists
    // as the next landing's seed; the sort persists like before (absent → the stored one).
    const params = new URLSearchParams(location.search);
    const facetState = parseFacetState(location.search, MOVIES_PARSE_SPEC);
    const types = typesOf(facetState);
    saveTitleTypes(types);
    const sortParam = params.get("sort");
    const sort = sortParam || loadSort();
    saveSort(sort);
    const plain = isPlainMoviesSearch(facetState);
    const lists = myListsOf(facetState);

    // Re-dispatch only when something search-shaped changed. The URL also carries params that are
    // NOT searches — ?title=<kind>:<id> is the open detail modal — and every dispatch below builds a
    // fresh `search` object, which refetches the grid. Without this guard, opening the modal would
    // push a URL, re-run this effect, and refetch the grid under itself. A browseMovieIds restore is
    // part of the signature (its arrival must dispatch; its persistence must not re-dispatch on
    // every modal open/close riding the same route state).
    const restoreIds = Array.isArray(location.state?.browseMovieIds) ? location.state.browseMovieIds : null;
    const dispatchSig = JSON.stringify({
      facet: facetStateKey(facetState), types, sort,
      auth: isAuthReady, user: userData?.username ?? null,
      restore: restoreIds && restoreIds.length
        ? `${restoreIds.length}:${restoreIds[0]}:${restoreIds[restoreIds.length - 1]}`
        : null,
    });
    if (!isInitialLoad && dispatchSig === lastDispatchSigRef.current) {
      return;
    }
    lastDispatchSigRef.current = dispatchSig;

    // The one browse: the facet state over the Type scope under the sort. With nothing else active
    // this is also the site's landing grid — Random is one of the sorts (and the default), so "the
    // landing" is just this browse under whichever sort the user last chose.
    const browse = () => facetSearch(moviesFilterParams(facetState).toString(), types, sort, facetState);

    if (plain) {
      // Nothing narrows the browse. Determine whether this is a hard browser reload
      // (F5 / Ctrl+R) vs. normal in-app navigation, using the browser Navigation API.
      const navigationEntry = window.performance?.getEntriesByType?.("navigation")?.[0];
      const isHardReload = isInitialLoad && navigationEntry?.type === "reload";

      if (isHardReload) {
        // On a hard reload, browseMovieIds in route state (the previous scroll position
        // context) is stale and should be cleared. Route state is like TempData in
        // ASP.NET MVC — it travels with the URL but isn't visible in the address bar.
        if (location.state?.browseMovieIds) {
          const restState = { ...location.state };
          delete restState.browseMovieIds;
          history.replace({
            pathname: location.pathname,
            search: location.search,
            state: Object.keys(restState).length > 0 ? restState : undefined,
          });
        }
        // A reload re-renders the browse for the current scope + sort. Under Random that is a genuine
        // reshuffle: the seed is minted per page load (see SHUFFLE_SEED), so F5 deals a new hand.
        browse();
        return;
      }

      // On normal back/forward navigation, browseMovieIds in route state carries the list of movie
      // IDs that was on screen before — restore it so the user lands back on the same results.
      const movieIds = (restoreIds ?? []).map((id) => Number(id)).filter((id) => Number.isInteger(id) && id > 0);
      if (movieIds.length > 0) {
        restoreMovieIdsSearch(movieIds, types);
        return;
      }
      browse();
      return;
    }

    // The viewer's own list on its own (only the Type scope beside it) keeps the dense id-list path:
    // untoggling Seen/Want removes the card in place, which a paged scope cannot express until S3
    // seats these lists on the engine. Combined with any facet, the server filters the list.
    const onlyList = lists.length === 1 && (lists[0] === "seen" || lists[0] === "want") && isPlainMoviesSearch({ ...facetState, flags: {} });
    if (onlyList) {
      if (!isAuthReady) return;
      if (!userData) {
        browse();
        return;
      }
      if (lists[0] === "seen") moviesSeenSearch(userData, types, sort);
      else moviesWantToWatchSearch(userData, types, sort);
      return;
    }
    browse();
    // These callbacks are all stable (useCallback in useMovieSearch), and history is a stable
    // reference from useHistory(). userData?.username is used intentionally instead of
    // userData to avoid re-running when moviesSeen/moviesToWatch mutate — only a user
    // identity change should re-trigger the dispatch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    location.search,
    location.pathname,
    location.state,
    userData?.username,
    isAuthReady,
    history,
    facetSearch,
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
      // Only the ACCOUNT keys. This used to be localStorage.clear(), which also destroyed every
      // device preference on the box — the music queue, the arcade controller mappings, volumes,
      // the theme — for whoever logged in next. Those describe the DEVICE, not the account.
      try {
        window.localStorage.removeItem("Username");
        window.localStorage.removeItem("CardStyle");
      } catch { /* storage blocked — nothing to clear */ }
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
  // The theme toggle left this foot in R9 S1: it lives in the SectionBar (desktop) and the top bar
  // (phones) — the strip that is always on screen.
  const navFooter = (
    <div className="navbar-footer">
      {photosNavControls}
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
      {/* Books (R8): the former external "Comics" link, now a section. Same gating shape as Photos:
          the entry is a courtesy, the /API/Books/* route re-checks the grant and the password session. */}
      {userData?.booksAccess && userData?.hasPassword && (
        <button className="navbar-section-item" onClick={() => history.push("/books")}>
          <img className="navbar-section-icon" src={comicsIcon} alt="" /> Books
          <span className="navbar-hue-dot" style={{ background: "#D98936" }} />
        </button>
      )}
    </>
  );

  // Every section rail takes the same props (boardgames also reads `search`; the rest ignore it).
  // Movies is the fallback: Login (the user block + the Seen · Want · Rate index rows) and, on
  // desktop, the generic facet rail over the movies spec (R9 S2) — the phone's browse raises its own
  // full-page sheet from the bar's Filters pill.
  const navContent = section.Content ? (
    <section.Content
      userData={userData}
      onUserLoggedIn={onUserLoggedIn}
      setSettingsModalOpen={setSettingsModalOpen}
      search={search}
      onOpenPlaylists={() => setPlaylistsModalOpen(true)}
    />
  ) : (
    <>
      <Login
        userData={userData}
        onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen}
        onOpenPlaylists={() => setPlaylistsModalOpen(true)}
      />
      {!isMobile && <MoviesSiderRail userData={userData} />}
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
          {/* The GENERIC controls live up here on phones (R9 S1): search (opens the rail drawer, where
              the section's search lives), the catalog's ⚙ (portaled into #topbar-tools by CatalogHost)
              and the theme toggle. The section strip under this bar is content navigation only. */}
          <div className="navbar-topbar-tools">
            <button type="button" className="navbar-tb-btn" onClick={() => { if (!requestSectionSearch()) setDrawerOpen(true); }} title="Search" aria-label="Search">
              <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden="true"><circle cx="7" cy="7" r="4.5" /><line x1="10.5" y1="10.5" x2="14" y2="14" /></svg>
            </button>
            <span id="topbar-tools" className="navbar-topbar-slot" />
            {themeToggleButton}
          </div>
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
          {(isMovies || section.key === "tv") && (
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
        {(isMovies || section.key === "tv") && (
          <MyPlaylistsModal open={playlistsModalOpen} onClose={() => setPlaylistsModalOpen(false)} userData={userData} />
        )}
      </Suspense>
    </>
  );
}

export default NavBar;
