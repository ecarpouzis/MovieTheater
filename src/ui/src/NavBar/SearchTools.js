import { Input, List, Button, Select, message } from "antd";
import { useState, useEffect } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";

const { Search } = Input;

const sectionHeaderStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "700",
  color: "#6b8aad",
  textTransform: "uppercase",
  letterSpacing: "1.5px",
  marginBottom: "12px",
  paddingBottom: "8px",
  borderBottom: "1px solid #1e3a57",
};

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "#8fa8c0",
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
  "#",
  "A",
  "B",
  "C",
  "D",
  "E",
  "F",
  "G",
  "H",
  "I",
  "J",
  "K",
  "L",
  "M",
  "N",
  "O",
  "P",
  "Q",
  "R",
  "S",
  "T",
  "U",
  "V",
  "W",
  "X",
  "Y",
  "Z",
];

const listStyle = {
  paddingBottom: "20px",
};

function SearchTools({ search, userData }) {
  const history = useHistory();
  const [mpaRatings, setMpaRatings] = useState([]);
  const [genres, setGenres] = useState([]);

  useEffect(() => {
    MovieAPI.getMPARatings()
      .then((r) => r.json())
      .then((data) => {
        if (Array.isArray(data)) setMpaRatings(data);
      })
      .catch(() => {});
    MovieAPI.getGenres()
      .then((r) => r.json())
      .then((data) => {
        if (Array.isArray(data)) setGenres(data);
      })
      .catch(() => {});
  }, []);

  function navigateToBrowseSearch(mode, value = "") {
    const params = new URLSearchParams();

    if (mode) {
      params.set("mode", mode);
    }

    if (value && value.trim()) {
      params.set("value", value.trim());
    }

    history.push({
      pathname: "/",
      search: params.toString() ? `?${params.toString()}` : "",
    });
  }

  function ToggleLetterSearch(firstLetter) {
    // Check if already viewing this letter by comparing the startsWith property
    const isAlreadySelected = search.startsWith === firstLetter;

    if (isAlreadySelected) {
      navigateToBrowseSearch();
    } else {
      navigateToBrowseSearch("letter", firstLetter);
    }
  }

  function ToggleRatingSearch(ratingId) {
    const isAlreadySelected = search.maxRatingId === String(ratingId);

    if (isAlreadySelected) {
      navigateToBrowseSearch();
      return;
    }

    if (userData?.ageRestriction != null && ratingId > userData.ageRestriction) {
      const restrictionName = mpaRatings.find((r) => r.id === userData.ageRestriction)?.name || "your current setting";
      message.warning(`Your age restriction is set to ${restrictionName}. You cannot browse movies above that rating.`);
      return;
    }

    navigateToBrowseSearch("rating", String(ratingId));
  }

  return (
    <div id="SearchToolContainer" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid #1e3a57" }}>
      <span style={sectionHeaderStyle}>Search</span>
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
            value={Array.isArray(search.genre) ? search.genre : search.genre ? [search.genre] : []}
            onChange={(vals) => navigateToBrowseSearch(vals.length ? "genre" : undefined, vals.join(","))}
            options={genres.map((g) => ({ label: g, value: g }))}
            filterOption={(input, option) => option.label.toLowerCase().includes(input.toLowerCase())}
          />
        </>
      )}
      <span style={inputLabelStyle}>Type</span>
      <Select
        allowClear
        placeholder="Title type"
        style={{ width: "100%" }}
        // Render the popup inside the nav drawer's stacking context. Default AntD portals it to
        // <body> at z-index 1050, which sits BEHIND the mobile nav drawer (z-index 1250) — so the
        // options were invisible/untappable on mobile. Anchoring to the trigger's parent fixes it.
        getPopupContainer={(trigger) => trigger.parentNode}
        value={search.titleType || undefined}
        onChange={(val) => navigateToBrowseSearch(val ? "type" : undefined, val || "")}
        options={[
          { label: "Movies", value: "Movies" },
          { label: "Series", value: "Series" },
          { label: "Short", value: "Short" },
          { label: "Misc", value: "Misc" },
        ]}
      />
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
                  backgroundColor: item === search.startsWith ? "#1890ff" : "rgba(255,255,255,0.08)",
                  color: item === search.startsWith ? "white" : "rgba(255,255,255,0.75)",
                  borderColor: item === search.startsWith ? "#1890ff" : "rgba(255,255,255,0.15)",
                }}
              >
                <span style={searchLetterStyle}>{item}</span>
              </Button>
            </List.Item>
          );
        }}
      />
      {mpaRatings.length > 0 && (
        <>
          <span style={inputLabelStyle}>MPA Rating Search</span>
          <div style={{ display: "flex", flexWrap: "wrap", gap: "6px", paddingBottom: "20px" }}>
            {mpaRatings.map((r) => {
              const isActive = search.maxRatingId === String(r.id);
              const isRestricted = userData?.ageRestriction != null && r.id > userData.ageRestriction;
              return (
                <button
                  key={r.id}
                  className={`search-rating-btn${isActive ? " search-rating-btn--active" : ""}${isRestricted ? " search-rating-btn--restricted" : ""}`}
                  onClick={() => ToggleRatingSearch(r.id)}
                  style={{
                    whiteSpace: "nowrap",
                    overflow: "visible",
                    display: "inline-block",
                    padding: "4px 14px",
                    fontSize: "14px",
                    lineHeight: "22px",
                    borderRadius: "6px",
                    border: "1px solid",
                    cursor: "pointer",
                    transition: "background 0.15s, color 0.15s",
                    backgroundColor: isActive ? "#1890ff" : "rgba(255,255,255,0.08)",
                    color: isActive ? "white" : isRestricted ? "rgba(255,255,255,0.3)" : "rgba(255,255,255,0.75)",
                    borderColor: isActive ? "#1890ff" : isRestricted ? "rgba(255,255,255,0.07)" : "rgba(255,255,255,0.15)",
                    opacity: isRestricted ? 0.6 : 1,
                  }}
                >
                  {r.name}
                </button>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}

export default SearchTools;
