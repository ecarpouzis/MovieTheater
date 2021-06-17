/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */

import { MovieAPI } from "./MovieAPI";
import { Card } from "antd";
import { Scrollbars } from "react-custom-scrollbars";

const gridStyle = {
  width: "400px",
  height: "200px",
  textAlign: "center",
  margin: "10px",
  padding: "0px",
  display: "flex",
};

const cardPosterStyle = {
  height: "100%",
  float: "left",
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
  width: "10%",
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

const cardRightColumStyle = {
  flexGrow: "1",
  overflowY: "auto",
  textAlign: "left",
  paddingLeft: "5px",
  paddingRight: "10px",
};
console.log(MovieAPI.getPosterThumbnail(100));

function CardList() {
  return (
    <Card>
      <MovieCard
        movieID="100"
        movieTitle="A Brighter Summer Day (1991)"
        movieRating="R"
        movieTime="3 h 57 min"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
        moviePlot={
          "Millions of Mainland Chinese fled to Taiwan with the National Government after its civil war defeat by the Chinese Communists in 1949. Their children were brought up in an uneasy atmosphere created by the parents' own uncertainty about the future. Many formed street gangs to search for identity and to strengthen their sense of security."
        }
      ></MovieCard>
      <MovieCard
        movieID="100"
        movieTitle="TestMovie"
        movieRating="R"
        movieTime="1h 30m"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
      ></MovieCard>
      <MovieCard
        movieID="100"
        movieTitle="TestMovie"
        movieRating="R"
        movieTime="1h 30m"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
      ></MovieCard>
      <MovieCard
        movieID="100"
        movieTitle="TestMovie"
        movieRating="R"
        movieTime="1h 30m"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
      ></MovieCard>
      <MovieCard
        movieID="100"
        movieTitle="TestMovie"
        movieRating="R"
        movieTime="1h 30m"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
      ></MovieCard>
    </Card>
  );
}

function MovieCard({
  movieID,
  movieTitle,
  movieRating,
  movieTime,
  movieActors,
  moviePlot,
}) {
  const thumbUrl = MovieAPI.getPosterThumbnail(movieID);

  const actorList = movieActors.map((actor) => (
    <div>
      <a href={"/browse?sort=Actor&actor=" + actor.trim()}>{actor}</a>
      <div class="actorSpacer"></div>
    </div>
  ));

  const cardActorSpacer = {
    width: "40%",
    float: "left",
    textAlign: "left",
    paddingLeft: "5px",
  };

  return (
    <Card.Grid style={gridStyle}>
      <div>
        <img
          class="moviePosterImage"
          style={cardPosterStyle}
          alt=""
          src={thumbUrl}
        />
      </div>
      <Scrollbars autoHide>
        <div style={cardRightColumStyle}>
          <a href="#" style={cardTitleStyle} class="movieTitle">
            {movieTitle}
          </a>
          <br />
          <span class="movieTime" style={cardTimeStyle}>
            {movieTime}
          </span>
          <span class="movieRating" style={cardRatingStyle}>
            {movieRating}
          </span>
          <br />
          <div style={cardActorSpacer}>{actorList}</div>

          <div class="actorSpacer"></div>
          <div class="actorSpacer"></div>
          <span class="moviePlot" style={cardPlotStyle}>
            {moviePlot}
          </span>
        </div>
      </Scrollbars>
    </Card.Grid>
  );
}

export default CardList;
