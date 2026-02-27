import { Input, List, Button } from "antd";
import { useHistory } from "react-router-dom";

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
          onSearch={(value) => {
            if (value && value.trim()) {
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
          onSearch={(value) => {
            if (value && value.trim()) {
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
