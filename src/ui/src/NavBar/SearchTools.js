import { Input, List, Button, message } from "antd";
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

const boardGameAgeRanges = ["0-3", "3-5", "6-8", "8-12", "13-100"];

function SearchTools({ search, userData, isBoardGames = false }) {
  const history = useHistory();
  const [mpaRatings, setMpaRatings] = useState([]);

  useEffect(() => {
    if (isBoardGames) {
      setMpaRatings([]);
      return;
    }

    MovieAPI.getMPARatings()
      .then((r) => r.json())
      .then((data) => {
        if (Array.isArray(data)) setMpaRatings(data);
      })
      .catch(() => {});
  }, [isBoardGames]);

  function navigateToBrowseSearch(mode, value = "") {
    const params = new URLSearchParams();

    if (mode) {
      params.set("mode", mode);
    }

    if (value && value.trim()) {
      params.set("value", value.trim());
    }

    history.push({
      pathname: isBoardGames ? "/boardgames" : "/",
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
      message.warning(
        isBoardGames
          ? `Your age restriction is set to ${restrictionName}. You cannot browse board games above that range.`
          : `Your age restriction is set to ${restrictionName}. You cannot browse movies above that rating.`,
      );
      return;
    }

    navigateToBrowseSearch("rating", String(ratingId));
  }

  function ToggleBoardGameAgeRangeSearch(ageRange) {
    const isAlreadySelected = search.maxRatingId === ageRange;
    if (isAlreadySelected) {
      navigateToBrowseSearch();
      return;
    }
    navigateToBrowseSearch("rating", ageRange);
  }

  return (
    <div id="SearchToolContainer" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid #1e3a57" }}>
      <span style={sectionHeaderStyle}>Search</span>
      <span style={{ ...inputLabelStyle, marginTop: 0 }}>{isBoardGames ? "Board Game Title" : "Movie Title"}</span>
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
      {!isBoardGames && (
        <>
          <span style={inputLabelStyle}>Actor Name</span>
          <Search
            placeholder="Actor"
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
        </>
      )}
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
      {isBoardGames && (
        <>
          <span style={inputLabelStyle}>Age Range Search</span>
          <div style={{ display: "flex", flexWrap: "wrap", gap: "6px", paddingBottom: "20px" }}>
            {boardGameAgeRanges.map((range) => {
              const isActive = search.maxRatingId === range;
              return (
                <button
                  key={range}
                  onClick={() => ToggleBoardGameAgeRangeSearch(range)}
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
                    color: isActive ? "white" : "rgba(255,255,255,0.75)",
                    borderColor: isActive ? "#1890ff" : "rgba(255,255,255,0.15)",
                  }}
                >
                  {range}
                </button>
              );
            })}
          </div>
        </>
      )}
      {!isBoardGames && mpaRatings.length > 0 && (
        <>
          <span style={inputLabelStyle}>{isBoardGames ? "Age Range Search" : "MPA Rating Search"}</span>
          <div style={{ display: "flex", flexWrap: "wrap", gap: "6px", paddingBottom: "20px" }}>
            {mpaRatings.map((r) => {
              const isActive = search.maxRatingId === String(r.id);
              const isRestricted = userData?.ageRestriction != null && r.id > userData.ageRestriction;
              return (
                <button
                  key={r.id}
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
