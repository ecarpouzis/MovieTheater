/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */

import { MovieAPI } from "./MovieAPI";
import { Card, List } from "antd";
import { Scrollbars } from "react-custom-scrollbars";

const listStyle = {
  width: "100%",
  height: "100%",
  padding: "10px",
};
const cardPosterStyle = {
  height: "100%",
};

const cardTitleStyle = {
  fontWeight: "bold",
  fontFamily: "Arial Black",
  color: "#5E5E5E",
  width: "100%",
  textAlign: "left",
  float: "left",
  paddingLeft: "5px",
};

const cardRatingStyle = {
  float: "left",
  paddingLeft: "10px",
  fontFamily: "Georgia",
  fontWeight: "bold",
};

const cardTimeStyle = {
  float: "left",
  paddingLeft: "5px",
};

const cardPlotStyle = {
  textAlign: "left",
  display: "block",
  clear: "left",
  paddingLeft: "5px",
};

const actorLinkStyle = {
  color: "black",
  textDecoration: "underline",
  fontStyle: "italic",
  fontSize: ".9em",
  fontFamily: "verdana",
};

const cardActorSpacer = {
  width: "100%",
  textAlign: "left",
  paddingLeft: "5px",
  clear: "left",
};

const cardBodyStyle = {
  height: "200px",
  padding: "0px",
  display: "flex",
};

const posterContainer = { height: "100%", float: "left" };

const cardRightColumStyle = {
  flexGrow: "1",
  overflowY: "auto",
  textAlign: "left",
  paddingLeft: "3px",
  paddingRight: "13px",
};

function CardList({ movieDataArray }) {
  return (
    <>
      {
        <List
          style={listStyle}
          grid={{
            gutter: 8,
            xs: 1,
            sm: 1,
            md: 2,
            lg: 2,
            xl: 3,
            xxl: 4,
          }}
          dataSource={movieDataArray}
          renderItem={(item, i) => {
            const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

            const actorList = item.actors.split(",").map((actor, i) => (
              <>
                <a
                  key={i}
                  style={actorLinkStyle}
                  href={"/browse?sort=Actor&actor=" + actor.trim()}
                >
                  {actor}
                </a>
                <br />
              </>
            ));

            return (
              <List.Item>
                <Card hoverable bodyStyle={cardBodyStyle}>
                  <div style={posterContainer}>
                    <img
                      className="moviePosterImage"
                      style={cardPosterStyle}
                      alt=""
                      src={thumbUrl}
                    />
                  </div>
                  <Scrollbars>
                    <div className="RightCol" style={cardRightColumStyle}>
                      <a href="#" style={cardTitleStyle} className="movieTitle">
                        {item.title +
                          " (" +
                          new Date(item.releaseDate).getFullYear() +
                          ")"}
                      </a>
                      <br />
                      <span className="movieTime" style={cardTimeStyle}>
                        {item.runtime}
                      </span>
                      <span className="movieRating" style={cardRatingStyle}>
                        {item.rating}
                      </span>
                      <br />
                      <div style={cardActorSpacer}>{actorList}</div>
                      <span className="moviePlot" style={cardPlotStyle}>
                        {item.plot}
                      </span>
                    </div>
                  </Scrollbars>
                </Card>
              </List.Item>
            );
          }}
        />
      }
    </>
  );
}

export default CardList;
