import { Input, List, Button } from "antd";
import { gql } from "@apollo/client";

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

function SearchTools({ search, setSearch, resetSearch, titleSearch, actorSearch }) {
  function ToggleLetterSearch(firstLetter) {
    let isAlreadySelected;
    let query;
    let variables;
    if (firstLetter === "#") {
      query = gql`
        query {
          movies(
            where: {
              simpleTitle: {
                or: [
                  { startsWith: "#" }
                  { startsWith: "0" }
                  { startsWith: "1" }
                  { startsWith: "2" }
                  { startsWith: "3" }
                  { startsWith: "4" }
                  { startsWith: "5" }
                  { startsWith: "6" }
                  { startsWith: "7" }
                  { startsWith: "8" }
                  { startsWith: "9" }
                ]
              }
            }
            order: { simpleTitle: ASC }
          ) {
            id
            actors
            title
            simpleTitle
            rating
            releaseDate
            runtime
            genre
            director
            writer
            plot
            posterLink
            imdbRating
            tomatoRating
            uploadedDate
            removeFromRandom
          }
        }
      `;
      isAlreadySelected = search.query === query && search.variables.firstLetter == firstLetter;
    } else {
      query = gql`
        query ($firstLetter: String!) {
          movies(where: { simpleTitle: { startsWith: $firstLetter } }, order: { simpleTitle: ASC }) {
            id
            actors
            title
            simpleTitle
            rating
            releaseDate
            runtime
            genre
            director
            writer
            plot
            posterLink
            imdbRating
            tomatoRating
            uploadedDate
            removeFromRandom
          }
        }
      `;
      isAlreadySelected = search.query === query;
      variables = { firstLetter: firstLetter };
    }

    if (isAlreadySelected) {
      resetSearch();
    } else {
      setSearch({ query: query, variables: variables });
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
        <Search placeholder="Title" onSearch={titleSearch} enterButton />
        <br />
        <br />
        <span style={searchLabelStyle}>ACTOR NAME</span>
        <Search placeholder="Actor" onSearch={actorSearch} enterButton />
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
                      backgroundColor: item === search.startsWith ? "silver" : "white",
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
