import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import { useState, useEffect } from "react";
import { useIsMobile } from "../../hooks/useIsMobile";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";

function getColumnCount(sidebarCollapsed) {
  const w = window.innerWidth;
  // When sidebar is collapsed, we have more space - be more aggressive with columns
  if (sidebarCollapsed) {
    if (w >= 1800) return 5;
    if (w >= 1400) return 4;
    if (w >= 1000) return 3;
    if (w >= 768) return 2;
    return 1;
  }
  // Normal sidebar visible
  if (w >= 2000) return 5;
  if (w >= 1600) return 4;
  if (w >= 1200) return 3;
  if (w >= 768) return 2;
  return 1;
}

function CardList({ movieDataArray, userData, setUserData, actorSearch, onMovieClick, onToggleViewing, sidebarCollapsed }) {
  const isMobile = useIsMobile();
  const [columns, setColumns] = useState(() => getColumnCount(sidebarCollapsed));

  useEffect(() => {
    function handleResize() {
      setColumns(getColumnCount(sidebarCollapsed));
    }
    window.addEventListener("resize", handleResize);
    return () => window.removeEventListener("resize", handleResize);
  }, [sidebarCollapsed]);

  // Update columns when sidebar collapses/expands
  useEffect(() => {
    setColumns(getColumnCount(sidebarCollapsed));
  }, [sidebarCollapsed]);

  return (
    <>
      <List
        className="card-list"
        grid={{ gutter: 8, column: columns }}
        dataSource={movieDataArray}
        renderItem={(item) => {
          const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

          const rightColContent = (
            <div className="card-right-col">
              <div onClick={() => onMovieClick(item.id)} className="card-title">
                {item.title} ({new Date(item.releaseDate).getFullYear()})
              </div>
              <div className="card-meta-row">
                {item.rating && <span className="badge-rating">{item.rating}</span>}
                {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
                {item.imdbRating && <span className="badge-imdb">★ {item.imdbRating}</span>}
              </div>
              <div className="card-actor-row">
                {item.actors.split(",").map((actor, i) => (
                  <button key={i} type="button" className="mobile-actor-chip" onClick={() => actorSearch(actor.trim())}>
                    {actor.trim()}
                  </button>
                ))}
              </div>
              <p className="card-plot">{item.plot}</p>
            </div>
          );

          return (
            <List.Item>
              {isMobile ? (
                <Card hoverable className="mobile-movie-card">
                  {/* Compact two-column header: small poster left, info right */}
                  <div className="mobile-card-header">
                    <div className="mobile-card-poster-wrapper">
                      <img className="mobile-card-poster-img" alt="" src={thumbUrl} loading="lazy" />
                    </div>
                    <div className="mobile-card-info">
                      <div onClick={() => onMovieClick(item.id)} className="mobile-card-title">
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
                          <button key={idx} type="button" className="mobile-actor-chip" onClick={() => actorSearch(actor.trim())}>
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
                <Card hoverable className="movie-card">
                  <div className="card-content-wrapper">
                    <div className="card-poster-container">
                      <img className="card-poster-image" alt="" src={thumbUrl} loading="lazy" />
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
    </>
  );
}

export default CardList;
