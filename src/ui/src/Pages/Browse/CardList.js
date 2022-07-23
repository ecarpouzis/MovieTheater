import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { Scrollbars } from "react-custom-scrollbars";
import { Link } from "react-router-dom";

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

let cardBodyStyle = {
  height: "200px",
  padding: "0px",
  display: "flex",
  userSelect: "none",
  //If a user is logged in, we need height:250px
};

let cardContentWrapper = {
  height: "100%",
  width: "100%",
  display: "flex",
  //If a user is logged in, we need flex-wrap: wrap here, and height:90%
};

const posterContainer = { height: "100%", float: "left" };

const cardRightColumStyle = {
  flexGrow: "1",
  overflowY: "auto",
  textAlign: "left",
  paddingLeft: "3px",
  paddingRight: "13px",
};

const filmIcon = {
  fontSize: "30px",
  width: "35px",
  verticalAlign: "middle",
  paddingRight: "30px",
};

const heartIcon = {
  fontSize: "25px",
  width: "30px",
  verticalAlign: "middle",
  paddingRight: "5px",
};

const buttonLabelStyle = {
  fontWeight: "bold",
  verticalAlign: "middle",
};

const hasWatchedDataContainer = {
  width: "100px",
  margin: "auto",
  marginLeft: "-20px",
  float: "left",
  color: "#a9a9a9",
};
//when watched: #4169e3

const toWatchDataContainer = {
  width: "100px",
  margin: "auto",
  marginLeft: "10px",
  paddingRight: "20px",
  float: "left",
  color: "#a9a9a9",
};
//when wanted: #dc143c

function UserMovieOptions({ userData, id, setUserData }) {
  if (userData) {
    const isWatched = userData.moviesSeen.includes(id);
    let watchedDataContainer;

    if (isWatched) {
      watchedDataContainer = {
        ...hasWatchedDataContainer,
        color: "#4169e3",
      };
    } else {
      watchedDataContainer = hasWatchedDataContainer;
    }

    const isWanted = userData.moviesToWatch.includes(id);
    let wantedDataContainer;
    if (isWanted) {
      wantedDataContainer = {
        ...toWatchDataContainer,
        color: "#dc143c",
      };
    } else {
      wantedDataContainer = toWatchDataContainer;
    }
    return (
      <>
        <br style={{ clear: "both" }} />
        <div style={{ margin: "auto" }}>
          <div
            onClick={() => {
              if (!isWatched) {
                let newUserData = {
                  ...userData,
                  moviesSeen: [...userData.moviesSeen, id],
                };
                setUserData(newUserData);
              } else {
                let newUserData = {
                  ...userData,
                  moviesSeen: userData.moviesSeen.filter((x) => x !== id),
                };
                setUserData(newUserData);
              }

              MovieAPI.setWatchedState(userData.username, id, !isWatched)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            className="zoom-on-hover"
            style={watchedDataContainer}
          >
            <span style={filmIcon} className="fas fa-film"></span>
            <span style={buttonLabelStyle}>SEEN</span>
          </div>
          <div
            onClick={() => {
              if (!isWanted) {
                let newUserData = {
                  ...userData,
                  moviesToWatch: [...userData.moviesToWatch, id],
                };
                setUserData(newUserData);
              } else {
                let newUserData = {
                  ...userData,
                  moviesToWatch: userData.moviesToWatch.filter((x) => x !== id),
                };
                setUserData(newUserData);
              }
              MovieAPI.setWantToWatchState(userData.username, id, !isWanted)
                .then((response) => response.json())
                .then((response) => {
                  if (!response.success) {
                    alert(response.message);
                  }
                });
            }}
            className="zoom-on-hover"
            style={wantedDataContainer}
          >
            <span style={heartIcon} className="fas fa-heart"></span>
            <span style={buttonLabelStyle}>WANT {isWanted}</span>
          </div>
        </div>
      </>
    );
  }
  return <></>;
}

function CardList({ movieDataArray, userData, setUserData }) {
  //If a user is logged in, cards need to be formatted for Watchlist buttons
  if (userData) {
    cardBodyStyle = { ...cardBodyStyle, height: "260px", flexWrap: "wrap" };
    cardContentWrapper = {
      ...cardContentWrapper,
      height: "85%",
    };
  }

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
              <div key={i}>
                <a style={actorLinkStyle} href={"/browse?sort=Actor&actor=" + actor.trim()}>
                  {actor}
                </a>
                <br />
              </div>
            ));

            return (
              <List.Item>
                <Card hoverable bodyStyle={cardBodyStyle}>
                  <div style={cardContentWrapper}>
                    <div style={posterContainer}>
                      <img className="moviePosterImage" style={cardPosterStyle} alt="" src={thumbUrl} loading="lazy" />
                    </div>
                    <Scrollbars>
                      <div className="RightCol" style={cardRightColumStyle}>
                        <Link to={{ pathname: "/movie/" + item.id, id: item.id }} style={cardTitleStyle} className="movieTitle">
                          {item.title + " (" + new Date(item.releaseDate).getFullYear() + ")"}
                        </Link>
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
                  </div>
                  <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} />
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
