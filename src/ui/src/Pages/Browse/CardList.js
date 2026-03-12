import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { useState, useEffect } from "react";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";

function getColumnCount() {
  const w = window.innerWidth;
  if (w >= 1600) return 4;
  if (w >= 1200) return 3;
  if (w >= 768) return 2;
  return 1;
}

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
const isMobile = useIsMobile();
const columns = getColumnCount();

  return (
    <>
      {
        <List
          className="card-list"
          grid={{ gutter: 8, column: columns }}
          dataSource={movieDataArray}
          renderItem={(item, i) => {
            const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

            const actorList = item.actors.split(",").map((actor, i) => (
              <div key={i}>
                <button
                  type="button"
                  className="actor-link"
                  onClick={() => actorSearch(actor)}
                >
                  {actor}
                </button>
                <br />
              </div>
            ));

            const rightColContent = (
              <div className="RightCol card-right-col">
                <div
                  onClick={() => onMovieClick(item.id)}
                  className="card-title movieTitle"
                >
                  {item.title + " (" + new Date(item.releaseDate).getFullYear() + ")"}
                </div>
                <br />
                <span className="movieTime card-time">
                  {item.runtime}
                </span>
                <span className="movieRating card-rating">
                  {item.rating}
                </span>
                <br />
                <div className="card-actor-spacer">{actorList}</div>
                <span className="moviePlot card-plot">
                  {item.plot}
                </span>
              </div>
            );

            return (
              <List.Item>
                 {isMobile ? (
                  <Card hoverable className="mobile-movie-card">
                    {/* Compact two-column header: small poster left, info right */}
                    <div className="mobile-card-header">
                      <div className="mobile-card-poster-wrapper">
                        <img
                          className="mobile-card-poster-img"
                          alt=""
                          src={thumbUrl}
                          loading="lazy"
                        />
                      </div>
                      <div className="mobile-card-info">
                        <div
                          onClick={() => onMovieClick(item.id)}
                          className="mobile-card-title"
                        >
                          {item.title} ({new Date(item.releaseDate).getFullYear()})
                        </div>
                        {/* Meta badges: rating, runtime, IMDb score */}
                        <div className="mobile-badge-container">
                          {item.rating && <span className="badge-rating">{item.rating}</span>}
                          {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
                          {item.imdbRating && <span className="badge-imdb">★ {item.imdbRating}</span>}
                        </div>
                        {/* Actor pill chips */}
                        <div className="mobile-badge-container">
                          {item.actors.split(",").map((actor, idx) => (
                            <button
                              key={idx}
                              type="button"
                              className="mobile-actor-chip"
                              onClick={() => actorSearch(actor)}
                            >
                              {actor.trim()}
                            </button>
                          ))}
                        </div>
                      </div>
                    </div>
                  <p className="mobile-card-plot">{item.plot}</p>
                  <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
                </Card>
              ) : (
                <Card hoverable className={`movie-card${userData ? " movie-card--with-user" : ""}`}>
                  <div className={`card-content-wrapper${userData ? " card-content-wrapper--with-user" : ""}`}>
                    <div className="card-poster-container">
                      <img className="card-poster-image" alt="" src={thumbUrl} loading="lazy" />
                    </div>
                    {rightColContent}
                  </div>
                  <br style={{ clear: "both" }} />
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
