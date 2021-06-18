import { Input, Row, Col, Button } from "antd";

const { Search } = Input;

const searchLabelStyle = {
  fontWeight: "bold",
  textAlign: "left",
  display: "block",
};

const letterButtonStyle = {
  width: "50px",
  fontWeight: "bold",
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

function SearchTools() {
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
          //onSearch={onSearch}
          enterButton
        />
        <br />
        <br />
        <span style={searchLabelStyle}>ACTOR NAME</span>
        <Search
          placeholder="Actor"
          //onSearch={onSearch}
          enterButton
        />
        <br />
        <br />
        <span style={searchLabelStyle}>FIRST LETTER</span>
        <Row gutter={[8, 8]}>
          {searchLetters.map((letter, i) => (
            <Col key={i}>
              <Button syle={letterButtonStyle}>{letter}</Button>
            </Col>
          ))}
        </Row>
      </div>
    </div>
  );
}

export default SearchTools;
