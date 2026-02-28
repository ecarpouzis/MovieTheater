import { Input, List, Button } from "antd";
import { useHistory } from "react-router-dom";

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

const listStyle = {};

function SearchTools({ search }) {
  const history = useHistory();

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
        renderItem={(item, i) => {
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
    </div>
  );
}

export default SearchTools;
