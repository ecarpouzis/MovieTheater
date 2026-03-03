import { Input, List, Button } from "antd";
import { useEffect, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";

const { Search } = Input;

const searchLabelStyle = {
  fontWeight: "bold",
  textAlign: "left",
  display: "block",
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

const listStyle = {};

function SearchTools({ search }) {
  const history = useHistory();
  const location = useLocation();
  const [titleValue, setTitleValue] = useState("");
  const [actorValue, setActorValue] = useState("");

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

  function navigateToBrowseSearch(mode, value = "") {
    const trimmedValue = value && value.trim() ? value.trim() : "";
    let pathname = "/";

    if (mode === "title" && trimmedValue) {
      pathname = `/discover/title/${encodeURIComponent(trimmedValue)}`;
    } else if (mode === "actor" && trimmedValue) {
      pathname = `/discover/person/${encodeURIComponent(trimmedValue)}`;
    } else if (mode === "letter" && trimmedValue) {
      pathname = `/discover/letter/${encodeURIComponent(trimmedValue)}`;
    }

    history.push({
      pathname,
      search: "",
    });
  }

  useEffect(() => {
    const pathname = location.pathname || "/";
    const params = new URLSearchParams(location.search || "");
    const legacyMode = params.get("mode") || "";

    if (
      pathname === "/library/watched" ||
      pathname === "/library/watchlist" ||
      pathname === "/browse/seen" ||
      pathname === "/browse/want" ||
      legacyMode === "seen" ||
      legacyMode === "want"
    ) {
      setActorValue("");
      setTitleValue("");
      return;
    }

    if (pathname.startsWith("/discover/title/")) {
      const value = decodePathValue(pathname.replace("/discover/title/", ""));
      setTitleValue(value);
      setActorValue("");
      return;
    }

    if (pathname.startsWith("/discover/person/")) {
      const value = decodePathValue(pathname.replace("/discover/person/", ""));
      setActorValue(value);
      setTitleValue("");
      return;
    }

    if (pathname.startsWith("/discover/all/person/")) {
      setActorValue("");
      setTitleValue("");
      return;
    }

    setActorValue("");
    setTitleValue("");
  }, [location.pathname, location.search]);

  function ToggleLetterSearch(firstLetter) {
    const isAlreadySelected = search.startsWith === firstLetter;

    if (isAlreadySelected) {
      navigateToBrowseSearch();
    } else {
      navigateToBrowseSearch("letter", firstLetter);
    }
  }

  return (
    <div id="SearchToolContainer" style={{ color: "white" }}>
      <span style={{ fontWeight: "bold", fontSize: "18px" }}>SEARCH</span>
      <br />
      <br />
      <div
        style={{
          width: "100%",
          paddingLeft: "10px",
          paddingRight: "10px",
        }}
      >
        <span style={searchLabelStyle}>MOVIE TITLE</span>
        <Search
          placeholder="Title"
          value={titleValue}
          onChange={(event) => setTitleValue(event.target.value)}
          onSearch={(value) => {
            if (value && value.trim()) {
              setActorValue("");
              navigateToBrowseSearch("title", value);
            } else {
              navigateToBrowseSearch();
            }
          }}
          enterButton
        />
        <br />
        <br />
        <span style={searchLabelStyle}>ACTOR NAME</span>
        <Search
          placeholder="Actor"
          value={actorValue}
          onChange={(event) => setActorValue(event.target.value)}
          onSearch={(value) => {
            if (value && value.trim()) {
              setTitleValue("");
              navigateToBrowseSearch("actor", value);
            } else {
              navigateToBrowseSearch();
            }
          }}
          enterButton
        />
        <br />
        <br />
        <span style={searchLabelStyle}>FIRST LETTER</span>

        {
          <List
            style={listStyle}
            grid={{
              gutter: 1,
              xs: 3,
              sm: 3,
              md: 3,
              lg: 3,
              xl: 4,
              xxl: 4,
            }}
            dataSource={searchLetters}
            renderItem={(item, i) => {
              return (
                <List.Item
                  style={{
                    marginBottom: "10px",
                  }}
                >
                  <Button
                    onClick={() => {
                      ToggleLetterSearch(item);
                    }}
                    style={{
                      width: "36px",
                      // removed backgroundColor change on selection since it was too similar to the hover color and made it hard to see which letter was selected, replaced with a blue background and white text for better visibility
                      //  backgroundColor: item === search.startsWith ? "silver" : "white",
                      backgroundColor: item === search.startsWith ? "#1890ff" : "white",
                      color: item === search.startsWith ? "white" : "black",
                      borderColor: item === search.startsWith ? "#1890ff" : "#d9d9d9",
                    }}
                  >
                    <span style={searchLetterStyle}>{item}</span>
                  </Button>
                </List.Item>
              );
            }}
          />
        }
      </div>
    </div>
  );
}

export default SearchTools;
