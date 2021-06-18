/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */

import { MovieAPI } from "./MovieAPI";
import { Card } from "antd";
import { Scrollbars } from "react-custom-scrollbars";

const movieDataArray = [
  {
    movieID: "4504",
    movieTitle: "Azorian: The Raising of the K-129 (2010)",
    movieRating: "N/A",
    movieTime: "105 min",
    movieActors: ["Nick Jackson"],
    moviePlot:
      "In 1968 the Soviet ballistic missile submarine K-129 sank in the Central North Pacific. American intelligence located it within weeks of its demise. The CIA crafted a secret program to ...",
  },
  {
    movieID: "2417",
    movieTitle: "A Brighter Summer Day (1991)",
    movieRating: "15",
    movieTime: "3 h 57 min",
    movieActors: ["Chen Chang", "Lisa Yang", "Kuo-Chu Chang", "Elaine Jin"],
    moviePlot:
      "Millions of Mainland Chinese fled to Taiwan with the National Government after its civil war defeat by the Chinese Communists in 1949. Their children were brought up in an uneasy atmosphere created by the parents' own uncertainty about the future. Many formed street gangs to search for identity and to strengthen their sense of security.",
  },
  {
    movieID: "36",
    movieTitle: "A Clockwork Orange (1971)",
    movieRating: "R",
    movieTime: "2 h 16 min",
    movieActors: [
      "Malcolm McDowell",
      "Patrick Magee",
      "Michael Bates",
      "Warren Clarke",
    ],
    moviePlot:
      "In future Britain, charismatic delinquent Alex DeLarge is jailed and volunteers for an experimental aversion therapy developed by the government in an effort to solve society's crime problem... but not all goes to plan.",
  },
  {
    movieID: "4504",
    movieTitle: "Azorian: The Raising of the K-129 (2010)",
    movieRating: "N/A",
    movieTime: "105 min",
    movieActors: ["Nick Jackson"],
    moviePlot:
      "In 1968 the Soviet ballistic missile submarine K-129 sank in the Central North Pacific. American intelligence located it within weeks of its demise. The CIA crafted a secret program to ...",
  },
  {
    movieID: "2417",
    movieTitle: "A Brighter Summer Day (1991)",
    movieRating: "15",
    movieTime: "3 h 57 min",
    movieActors: ["Chen Chang", "Lisa Yang", "Kuo-Chu Chang", "Elaine Jin"],
    moviePlot:
      "Millions of Mainland Chinese fled to Taiwan with the National Government after its civil war defeat by the Chinese Communists in 1949. Their children were brought up in an uneasy atmosphere created by the parents' own uncertainty about the future. Many formed street gangs to search for identity and to strengthen their sense of security.",
  },
  {
    movieID: "36",
    movieTitle: "A Clockwork Orange (1971)",
    movieRating: "R",
    movieTime: "2 h 16 min",
    movieActors: [
      "Malcolm McDowell",
      "Patrick Magee",
      "Michael Bates",
      "Warren Clarke",
    ],
    moviePlot:
      "In future Britain, charismatic delinquent Alex DeLarge is jailed and volunteers for an experimental aversion therapy developed by the government in an effort to solve society's crime problem... but not all goes to plan.",
  },
  {
    movieID: "4504",
    movieTitle: "Azorian: The Raising of the K-129 (2010)",
    movieRating: "N/A",
    movieTime: "105 min",
    movieActors: ["Nick Jackson"],
    moviePlot:
      "In 1968 the Soviet ballistic missile submarine K-129 sank in the Central North Pacific. American intelligence located it within weeks of its demise. The CIA crafted a secret program to ...",
  },
  {
    movieID: "2417",
    movieTitle: "A Brighter Summer Day (1991)",
    movieRating: "15",
    movieTime: "3 h 57 min",
    movieActors: ["Chen Chang", "Lisa Yang", "Kuo-Chu Chang", "Elaine Jin"],
    moviePlot:
      "Millions of Mainland Chinese fled to Taiwan with the National Government after its civil war defeat by the Chinese Communists in 1949. Their children were brought up in an uneasy atmosphere created by the parents' own uncertainty about the future. Many formed street gangs to search for identity and to strengthen their sense of security.",
  },
  {
    movieID: "36",
    movieTitle: "A Clockwork Orange (1971)",
    movieRating: "R",
    movieTime: "2 h 16 min",
    movieActors: [
      "Malcolm McDowell",
      "Patrick Magee",
      "Michael Bates",
      "Warren Clarke",
    ],
    moviePlot:
      "In future Britain, charismatic delinquent Alex DeLarge is jailed and volunteers for an experimental aversion therapy developed by the government in an effort to solve society's crime problem... but not all goes to plan.",
  },
];

const gridStyle = {
  width: "400px",
  height: "200px",
  textAlign: "center",
  padding: "0px",
  display: "flex",
  margin: "8px",
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

const actorLinkStyle = {
  color: "black",
  textDecoration: "underline",
  fontStyle: "italic",
  fontSize: ".9em",
  fontFamily: "verdana",
};

function CardList() {
  return (
    <>
      {movieDataArray.map((movie, i) => (
        <MovieCard
          key={i}
          movieID={movie.movieID}
          movieTitle={movie.movieTitle}
          movieRating={movie.movieRating}
          movieTime={movie.movieTime}
          movieActors={movie.movieActors}
          moviePlot={movie.moviePlot}
        />
      ))}
    </>
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
    <>
      <a
        style={actorLinkStyle}
        href={"/browse?sort=Actor&actor=" + actor.trim()}
      >
        {actor}
      </a>
      <br />
    </>
  ));

  const cardActorSpacer = {
    width: "100%",
    textAlign: "left",
    paddingLeft: "5px",
    clear: "left",
  };

  return (
    <Card.Grid style={gridStyle}>
      <div>
        <img
          className="moviePosterImage"
          style={cardPosterStyle}
          alt=""
          src={thumbUrl}
        />
      </div>
      <Scrollbars autoHide>
        <div style={cardRightColumStyle}>
          <a href="#" style={cardTitleStyle} className="movieTitle">
            {movieTitle}
          </a>
          <br />
          <span className="movieTime" style={cardTimeStyle}>
            {movieTime}
          </span>
          <span className="movieRating" style={cardRatingStyle}>
            {movieRating}
          </span>
          <br />
          <div style={cardActorSpacer}>{actorList}</div>
          <span className="moviePlot" style={cardPlotStyle}>
            {moviePlot}
          </span>
        </div>
      </Scrollbars>
    </Card.Grid>
  );
}

export default CardList;
