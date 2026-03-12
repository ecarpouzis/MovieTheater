import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { useState, useEffect } from "react";
import UserMovieOptions from "./UserMovieOptions";

function getColumnCount() {
  const w = window.innerWidth;
  if (w >= 1600) return 4;
  if (w >= 1200) return 3;
  if (w >= 768) return 2;
  return 1;
}

const listStyle = {
  width: "100%",
  padding: "10px 10px 2px",
};

const cardPosterStyle = {
  height: "100%",
  width: "100%",
  objectFit: "cover",
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

const baseCardBodyStyle = {
  height: "200px",
  padding: "0px",
  display: "flex",
  userSelect: "none",
};

const baseCardContentWrapper = {
  height: "100%",
  width: "100%",
  display: "flex",
};

const posterContainer = { height: "100%", width: "130px", float: "left", flexShrink: 0, overflow: "hidden" };

const cardRightColumStyle = {
  flexGrow: "1",
  overflowY: "auto",
  minHeight: 0,
  textAlign: "left",
  paddingLeft: "3px",
  paddingRight: "13px",
};

function useIsMobile(breakpoint = 768) {
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= breakpoint);
  useEffect(() => {
    const handler = () => setIsMobile(window.innerWidth <= breakpoint);
    window.addEventListener("resize", handler);
    return () => window.removeEventListener("resize", handler);
  }, [breakpoint]);
  return isMobile;
}

function CardList({ movieDataArray, userData, setUserData, actorSearch, onMovieClick, onToggleViewing }) {
const [hoveredMovieId, setHoveredMovieId] = useState(null);
const [hoveredActor, setHoveredActor] = useState(null);
const isMobile = useIsMobile();
const columns = getColumnCount();

const cardBodyStyle = userData
  ? { ...baseCardBodyStyle, height: "260px", flexWrap: "wrap" }
  : baseCardBodyStyle;
const cardContentWrapper = userData
  ? { ...baseCardContentWrapper, height: "85%" }
  : baseCardContentWrapper;

const currentCardBodyStyle = isMobile
  ? { padding: "12px", display: "flex", flexDirection: "column", userSelect: "none" }
  : cardBodyStyle;

const currentCardContentWrapper = isMobile
  ? { display: "flex", flexDirection: "column", width: "100%" }
  : cardContentWrapper;

const currentPosterContainer = isMobile ? { display: "flex", justifyContent: "center", width: "100%", marginBottom: "8px" } : posterContainer;

const currentPosterStyle = isMobile ? { maxHeight: "180px", width: "auto", height: "auto", objectFit: "contain" } : cardPosterStyle;

  return (
    <>
      {
        <List
          style={listStyle}
          grid={{ gutter: 8, column: columns }}
          dataSource={movieDataArray}
          renderItem={(item, i) => {
            const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

            const actorList = item.actors.split(",").map((actor, i) => (
              <div key={i}>
                <button
                  type="button"
                  style={{
                    ...actorLinkStyle,
                    color: hoveredActor === actor ? "#1890ff" : "black", // Change color on hover of actor name
                    background: "none",
                    border: "none",
                    padding: "0",
                    cursor: "pointer",
                  }}
                  onClick={() => actorSearch(actor)}
                  onMouseEnter={() => setHoveredActor(actor)}
                  onMouseLeave={() => setHoveredActor(null)}
                >
                  {actor}
                </button>
                <br />
              </div>
            ));

            const rightColContent = (
              <div className="RightCol" style={cardRightColumStyle}>
                <div
                  onClick={() => onMovieClick(item.id)}
                  onMouseEnter={() => setHoveredMovieId(item.id)}
                  onMouseLeave={() => setHoveredMovieId(null)}
                  style={{
                    ...cardTitleStyle,
                    cursor: "pointer",
                    color: hoveredMovieId === item.id ? "#1890ff" : "#5E5E5E", // Change color on hover of movie title
                  }}
                  className="movieTitle"
                >
                  {item.title + " (" + new Date(item.releaseDate).getFullYear() + ")"}
                </div>
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
            );

            return (
              <List.Item>
                {isMobile ? (
                  <Card hoverable bodyStyle={{ padding: "10px 10px 5px", userSelect: "none" }}>
                    {/* Compact two-column header: small poster left, info right */}
                    <div style={{ display: "flex", gap: "10px", marginBottom: "8px" }}>
                      <div style={{ flexShrink: 0, width: "80px", alignSelf: "stretch", overflow: "hidden", borderRadius: "4px" }}>
                        <img
                          style={{ width: "100%", height: "100%", display: "block", margin: "0", objectFit: "cover", objectPosition: "top" }}
                          alt=""
                          src={thumbUrl}
                          loading="lazy"
                        />
                      </div>
                      <div style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column", gap: "5px" }}>
                        <div
                          onClick={() => onMovieClick(item.id)}
                          style={{
                            fontWeight: "bold",
                            fontFamily: "Arial Black",
                            color: hoveredMovieId === item.id ? "#1890ff" : "#5E5E5E",
                            cursor: "pointer",
                            fontSize: "0.92em",
                            lineHeight: "1.3",
                          }}
                        >
                          {item.title} ({new Date(item.releaseDate).getFullYear()})
                        </div>
                        {/* Meta badges: rating, runtime, IMDb score */}
                        <div style={{ display: "flex", flexWrap: "wrap", gap: "4px" }}>
                          {item.rating && (
                            <span
                              style={{
                                background: "#f0f0f0",
                                padding: "1px 7px",
                                borderRadius: "3px",
                                fontSize: "0.72em",
                                fontWeight: "bold",
                                color: "#555",
                              }}
                            >
                              {item.rating}
                            </span>
                          )}
                          {item.runtime && (
                            <span style={{ background: "#f0f0f0", padding: "1px 7px", borderRadius: "3px", fontSize: "0.72em", color: "#555" }}>
                              {item.runtime}
                            </span>
                          )}
                          {item.imdbRating && (
                            <span
                              style={{
                                background: "#f5c518",
                                padding: "1px 7px",
                                borderRadius: "3px",
                                fontSize: "0.72em",
                                fontWeight: "bold",
                                color: "#333",
                              }}
                            >
                              ★ {item.imdbRating}
                            </span>
                          )}
                        </div>
                        {/* Actor pill chips */}
                        <div style={{ display: "flex", flexWrap: "wrap", gap: "4px" }}>
                          {item.actors.split(",").map((actor, idx) => (
                            <button
                              key={idx}
                              type="button"
                              onClick={() => actorSearch(actor)}
                              style={{
                                padding: "1px 8px",
                                background: "transparent",
                                border: "1px solid #ddd",
                                borderRadius: "10px",
                                fontSize: "0.7em",
                                cursor: "pointer",
                                color: "#666",
                                fontStyle: "italic",
                                whiteSpace: "nowrap",
                              }}
                            >
                              {actor.trim()}
                            </button>
                          ))}
                        </div>
                      </div>
                    </div>
                  <p style={{ fontSize: "0.82em", color: "#666", lineHeight: "1.4", margin: "0 0 2px 0", textAlign: "left" }}>{item.plot}</p>
                  <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
                </Card>
              ) : (
                <Card hoverable bodyStyle={currentCardBodyStyle}>
                  <div style={currentCardContentWrapper}>
                    <div style={currentPosterContainer}>
                      <img className="moviePosterImage" style={currentPosterStyle} alt="" src={thumbUrl} loading="lazy" />
                    </div>
                    {rightColContent}
                  </div>
                  <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
                </Card>
                )}
              </List.Item>
            );
          }}
        />
      }
    </>
  );
}

export default CardList;
