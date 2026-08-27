import { Input, Select, Slider } from "antd";
import { useState, useEffect } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import { inputLabelStyle, getPopupContainer } from "./navShared";
import useIsMobile from "../hooks/useIsMobile";

// Sort-by options for the Browse grid. Labels are user-facing; values match the API `sort` param.
// Random leads because it is the default: it's the shuffled discovery grid the site opens on, and
// making it an ordinary sort is what lets it be paged, filtered and scoped like the rest.
// MPA Rating Search stops. Each stop browses THAT rating — "PG-13" shows the PG-13 movies, not
// "PG-13 and everything tamer" (which is what this used to be: a cap/ceiling). `ids` are the MPA
// lookup ids the stop stands for: NC-17 covers both NC-17(5) and X(6), one certificate as far as
// anyone browsing is concerned. `ids[0]` is what the viewer's age restriction is compared against, so
// a rating above it isn't offered. "Unknown"(7) is not a rating anyone searches for, so it has no stop.
// There is no "Any" stop — the slider simply starts empty, and picking the selected stop again clears it.
const RATING_STOPS = [
  { label: "G", ids: [1] },
  { label: "PG", ids: [2] },
  { label: "PG-13", ids: [3] },
  { label: "R", ids: [4] },
  { label: "NC-17", ids: [5, 6] },
];

const stopValue = (stop) => stop.ids.join(",");

const { Search } = Input;

// Genres are a static lookup, but this component remounts whenever the mobile/desktop layout flips —
// which re-fetched every time. Cache the in-flight/resolved promise at module scope so it's fetched
// once per session; a failure clears the cache so a later mount can retry.
let genresPromise = null;

function loadGenres() {
  if (!genresPromise) {
    genresPromise = MovieAPI.getGenres()
      .then((r) => r.json())
      .then((data) => (Array.isArray(data) ? data : []))
      .catch(() => {
        genresPromise = null;
        return [];
      });
  }
  return genresPromise;
}

function SearchTools({ search, userData }) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const [genres, setGenres] = useState([]);

  useEffect(() => {
    let active = true;
    loadGenres().then((data) => {
      if (active) setGenres(data);
    });
    return () => {
      active = false;
    };
  }, []);

  // Navigate the Browse grid. The Type scope (`types` param) persists across mode changes: every
  // caller leaves it untouched except the Type selector, which passes typesOverride to change it
  // ("" = all types). So clearing a genre/title search returns to browsing the current scope,
  // never a hardcoded default.
  function navigateToBrowseSearch(mode, value = "", typesOverride, sortOverride) {
    const current = new URLSearchParams(location.search);
    const params = new URLSearchParams();

    if (mode) {
      params.set("mode", mode);
    }

    if (value && value.trim()) {
      params.set("value", value.trim());
    }

    const types = typesOverride !== undefined ? typesOverride : current.get("types");
    if (types !== null && types !== undefined) {
      params.set("types", types);
    }

    // Sort-by persists across mode changes like the Type scope: callers leave it untouched
    // (sortOverride undefined) except the Sort-by dropdown, which passes a new value.
    const sort = sortOverride !== undefined ? sortOverride : current.get("sort");
    if (sort) {
      params.set("sort", sort);
    }

    history.push({
      pathname: "/",
      search: params.toString() ? `?${params.toString()}` : "",
    });
  }

  // ── MPA Rating Search slider ───────────────────────────────────────────
  // Only offer ratings the viewer is allowed to see (their age restriction is an MPA id; null = no
  // restriction). Nothing is gained by a stop whose grid would be empty by policy.
  const ratingStops =
    userData?.ageRestriction != null
      ? RATING_STOPS.filter((s) => s.ids[0] <= userData.ageRestriction)
      : RATING_STOPS;

  // -1 = nothing selected. The slider then renders "empty" (handle + track hidden, see index.css) —
  // an antd Slider always has a numeric value, so "no rating" is a presentation state, not a value.
  const activeRatingIndex = search.ratingIds
    ? ratingStops.findIndex((s) => stopValue(s) === String(search.ratingIds))
    : -1;

  const ratingMarks = ratingStops.reduce((acc, s, i) => {
    acc[i] = s.label;
    return acc;
  }, {});

  // onChangeComplete, NOT onChange: picking the stop that's already selected has to CLEAR it, and
  // onChange only fires when the value actually changes — so a second click on the same stop would
  // be silently swallowed. onChangeComplete fires on every release, changed or not.
  function onRatingPick(index) {
    const stop = ratingStops[index];
    if (!stop) return;
    if (index === activeRatingIndex) navigateToBrowseSearch(); // same stop again → clear, back to the scope
    else navigateToBrowseSearch("rating", stopValue(stop));
  }

  return (
    <div className="nav-search-tools" style={{ padding: "8px 16px", color: "white" }}>
      {/* On desktop the title search is the SectionBar's centre box (R9 S1d); the rail keeps it for
          the phone drawer, where the bar has no search slot.
          Each search field gets its OWN single-field <form>. A loose input with more focusable fields
          below it makes a mobile/tablet keyboard label its Enter key "Next", which moves focus to the
          next filter instead of searching; a single-field form implicitly submits, so the key becomes
          "Go"/"Search" (enterKeyHint names it) and antd's onSearch fires as it does on desktop. The
          submit handler only stops the default page reload — onSearch still does the navigating. */}
      {isMobile && (
        <>
          <span style={{ ...inputLabelStyle, marginTop: 0 }}>Movie Title</span>
          <form onSubmit={(e) => e.preventDefault()}>
            <Search
              placeholder="Title"
              style={{ width: "100%" }}
              enterKeyHint="search"
              onSearch={(value) => {
                if (value && value.trim()) {
                  navigateToBrowseSearch("title", value);
                } else {
                  navigateToBrowseSearch();
                }
              }}
              enterButton
            />
          </form>
        </>
      )}
      <span style={{ ...inputLabelStyle, marginTop: isMobile ? undefined : 0 }}>People</span>
      <form onSubmit={(e) => e.preventDefault()}>
        <Search
          placeholder="Actor, director, or writer"
          style={{ width: "100%" }}
          enterKeyHint="search"
          onSearch={(value) => {
            if (value && value.trim()) {
              navigateToBrowseSearch("actor", value);
            } else {
              navigateToBrowseSearch();
            }
          }}
          enterButton
        />
      </form>
      {genres.length > 0 && (
        <>
          <span style={inputLabelStyle}>Genre</span>
          <Select
            mode="multiple"
            showSearch
            allowClear
            placeholder="Genre (matches all selected)"
            style={{ width: "100%" }}
            getPopupContainer={getPopupContainer}
            classNames={{ popup: { root: "nav-dropdown" } }}
            value={Array.isArray(search.genre) ? search.genre : search.genre ? [search.genre] : []}
            onChange={(vals) => navigateToBrowseSearch(vals.length ? "genre" : undefined, vals.join(","))}
            options={genres.map((g) => ({ label: g, value: g }))}
            filterOption={(input, option) => option.label.toLowerCase().includes(input.toLowerCase())}
          />
        </>
      )}
      <span style={inputLabelStyle}>Type</span>
      <Select
        mode="multiple"
        showSearch
        allowClear
        placeholder="Title type (any selected)"
        style={{ width: "100%" }}
        getPopupContainer={getPopupContainer}
        classNames={{ popup: { root: "nav-dropdown" } }}
        value={Array.isArray(search.titleTypes) ? search.titleTypes : []}
        onChange={(vals) => {
          const current = new URLSearchParams(location.search);
          navigateToBrowseSearch(current.get("mode"), current.get("value") || "", vals.join(","));
        }}
        options={[
          { label: "Movies", value: "Movies" },
          { label: "Series", value: "Series" },
          { label: "Short", value: "Short" },
          { label: "Misc", value: "Misc" },
        ]}
        filterOption={(input, option) => option.label.toLowerCase().includes(input.toLowerCase())}
      />
      {/* Sort left the rail in R9 S1: the SectionBar's Sort pill is the one sort control (it writes
          the same ?sort= this Select wrote, with the catalog's labels). */}
      <span style={inputLabelStyle}>MPA Rating Search</span>
      <div
        className={`rating-search-slider${activeRatingIndex < 0 ? " rating-search-slider--empty" : ""}`}
        style={{ padding: "0 6px 8px" }}
      >
        <Slider
          min={0}
          max={ratingStops.length - 1}
          step={1}
          marks={ratingMarks}
          tooltip={{ open: false }}
          value={activeRatingIndex < 0 ? 0 : activeRatingIndex}
          onChangeComplete={onRatingPick}
        />
      </div>
      {/* The A–Z rail grid is gone (boardgames dropped its own first): quick-scroll is the on-page
          CatalogPager now, the same strip the music library uses. A letter tap SCROLLS the
          alphabetical grid instead of replacing the browse with "titles starting with X" — so the
          genre/type/rating filters survive the jump. Pick Alphabetical in Sort By to get the strip;
          ?mode=letter URLs still work, there's just no rail control writing them. */}
    </div>
  );
}

export default SearchTools;
