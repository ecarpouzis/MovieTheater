import { Input, List, Button, Select, Slider } from "antd";
import { useState, useEffect } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import { loadSort } from "../hooks/useMovieSearch";

// Sort-by options for the Browse grid. Labels are user-facing; values match the API `sort` param.
const SORT_OPTIONS = [
  { label: "Alphabetical (A–Z)", value: "alpha" },
  { label: "Recently Added", value: "added" },
  { label: "IMDb rating", value: "imdb" },
  { label: "Rotten Tomatoes", value: "rt" },
  { label: "Popcornmeter", value: "popcorn" },
];

// MPA Rating Cap stops. `cap` = the ceiling id sent to /API/GetMoviesByRating (a title shows when its
// effective rating is ≤ cap). `restrict` = the rating id compared against the viewer's age restriction
// to clamp the slider. X and NC-17 are combined into one "NC-17" stop (cap 6 includes both NC-17=5 and
// X=6); "Unknown"(7) is not selectable, so unrated titles never appear under a cap. Index 0 = "Any"
// (no cap → browse the current scope).
const RATING_STOPS = [
  { label: "Any", cap: null, restrict: 0 },
  { label: "G", cap: 1, restrict: 1 },
  { label: "PG", cap: 2, restrict: 2 },
  { label: "PG-13", cap: 3, restrict: 3 },
  { label: "R", cap: 4, restrict: 4 },
  { label: "NC-17", cap: 6, restrict: 5 },
];

const { Search } = Input;

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "var(--sidebar-text-muted)",
  textTransform: "uppercase",
  letterSpacing: "0.8px",
  marginBottom: "5px",
  marginTop: "14px",
};

const searchLetterStyle = {
  fontWeight: "bold",
  position: "absolute",
  width: "100%",
  height: "1em",
  lineHeight: "1em",
  top: "50%",
  left: "0px",
  marginTop: "-0.5em",
};

const searchLetters = [
  "#", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
  "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
];

const listStyle = {
  paddingBottom: "20px",
};

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
  // ("" = all types). So clearing a genre/letter/title search returns to browsing the current scope,
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

  function ToggleLetterSearch(firstLetter) {
    const isAlreadySelected = search.startsWith === firstLetter;
    if (isAlreadySelected) {
      navigateToBrowseSearch();
    } else {
      navigateToBrowseSearch("letter", firstLetter);
    }
  }

  // ── MPA Rating Cap slider ──────────────────────────────────────────────
  // Clamp the highest selectable stop to the viewer's age restriction (an MPA id, or null = none).
  const maxRatingIndex =
    userData?.ageRestriction != null
      ? Math.max(0, RATING_STOPS.filter((s) => s.restrict <= userData.ageRestriction).length - 1)
      : RATING_STOPS.length - 1;

  // Reflect the active cap: find the stop whose `cap` matches the URL's maxRatingId (rating mode).
  const activeCapIndex =
    search.maxRatingId != null
      ? Math.max(0, RATING_STOPS.findIndex((s) => String(s.cap) === String(search.maxRatingId)))
      : 0;

  const ratingMarks = RATING_STOPS.slice(0, maxRatingIndex + 1).reduce((acc, s, i) => {
    acc[i] = s.label;
    return acc;
  }, {});

  function onRatingCapChange(index) {
    const stop = RATING_STOPS[index];
    if (!stop || stop.cap == null) {
      navigateToBrowseSearch(); // "Any" → clear the cap, browse the current scope
    } else {
      navigateToBrowseSearch("rating", String(stop.cap));
    }
  }

  return (
    <div id="SearchToolContainer" style={{ padding: "8px 16px", color: "white" }}>
      <span style={{ ...inputLabelStyle, marginTop: 0 }}>Movie Title</span>
      <Search
        placeholder="Title"
        style={{ width: "100%" }}
        onSearch={(value) => {
          if (value && value.trim()) {
            navigateToBrowseSearch("title", value);
          } else {
            navigateToBrowseSearch();
          }
        }}
        enterButton
      />
      <span style={inputLabelStyle}>People</span>
      <Search
        placeholder="Actor, director, or writer"
        style={{ width: "100%" }}
        onSearch={(value) => {
          if (value && value.trim()) {
            navigateToBrowseSearch("actor", value);
          } else {
            navigateToBrowseSearch();
          }
        }}
        enterButton
      />
      {genres.length > 0 && (
        <>
          <span style={inputLabelStyle}>Genre</span>
          <Select
            mode="multiple"
            showSearch
            allowClear
            placeholder="Genre (matches all selected)"
            style={{ width: "100%" }}
            getPopupContainer={(trigger) => trigger.parentNode}
            popupClassName="login-user-dropdown"
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
        getPopupContainer={(trigger) => trigger.parentNode}
        popupClassName="login-user-dropdown"
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
      <span style={inputLabelStyle}>Sort By</span>
      <Select
        style={{ width: "100%" }}
        getPopupContainer={(trigger) => trigger.parentNode}
        popupClassName="login-user-dropdown"
        value={new URLSearchParams(location.search).get("sort") || loadSort()}
        onChange={(val) => {
          const current = new URLSearchParams(location.search);
          navigateToBrowseSearch(current.get("mode"), current.get("value") || "", undefined, val);
        }}
        options={SORT_OPTIONS}
      />
      <span style={inputLabelStyle}>MPA Rating Cap</span>
      <div className="rating-cap-slider" style={{ padding: "0 6px 8px" }}>
        <Slider
          min={0}
          max={maxRatingIndex}
          step={1}
          marks={ratingMarks}
          tooltip={{ open: false }}
          value={Math.min(activeCapIndex, maxRatingIndex)}
          onChange={onRatingCapChange}
        />
      </div>
      <span style={inputLabelStyle}>First Letter</span>
      <List
        style={listStyle}
        grid={{
          gutter: [6, 8],
          xs: 3,
          sm: 3,
          md: 3,
          lg: 3,
          xl: 4,
          xxl: 4,
        }}
        dataSource={searchLetters}
        renderItem={(item) => {
          return (
            <List.Item style={{ display: "flex", justifyContent: "center", marginBottom: 0 }}>
              <Button
                className={`search-letter-btn${item === search.startsWith ? " search-letter-btn--active" : ""}`}
                onClick={() => {
                  ToggleLetterSearch(item);
                }}
                style={{
                  width: "36px",
                  backgroundColor: item === search.startsWith ? "var(--accent)" : "var(--sidebar-pill-bg)",
                  color: item === search.startsWith ? "#fff" : "var(--sidebar-text-muted)",
                  borderColor: item === search.startsWith ? "var(--accent)" : "var(--sidebar-input-border)",
                }}
              >
                <span style={searchLetterStyle}>{item}</span>
              </Button>
            </List.Item>
          );
        }}
      />
    </div>
  );
}

export default SearchTools;
