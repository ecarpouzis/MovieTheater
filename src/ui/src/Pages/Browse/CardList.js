import { MovieAPI } from "../../MovieAPI";
import { Card, List } from "antd";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";
import { useRef, useEffect, useState } from "react";

function PlotText({ text, className, hiddenClass }) {
  const ref = useRef(null);
  const [overflows, setOverflows] = useState(false);

  useEffect(() => {
    if (ref.current) {
      setOverflows(ref.current.scrollHeight > ref.current.clientHeight);
    }
  }, [text]);

  const classes = [className, hiddenClass, overflows ? "card-plot--faded" : ""].filter(Boolean).join(" ");

  return <p ref={ref} className={classes}>{text}</p>;
}

function CardList({ movieDataArray, userData, setUserData, actorSearch, onMovieClick, onToggleViewing, isMobile }) {
  return (
    <List
      className="card-list"
      grid={{ gutter: 8, xs: 1, sm: 1, md: 2, lg: 2, xl: 3, xxl: 4 }}
      dataSource={movieDataArray}
      renderItem={(item) => {
        const thumbUrl = MovieAPI.getPosterThumbnail(item.id);

        return (
          <List.Item>
            <Card hoverable className="movie-card">
              <div className={`card-content-wrapper${isMobile ? " card-content-wrapper--mobile" : ""}`}>
                <div className="card-poster-container">
                  <img className={`card-poster-image${isMobile ? " card-poster-image--mobile" : ""}`} alt="" src={thumbUrl} loading="lazy" />
                </div>
                <div className={`card-right-col${isMobile ? " card-right-col--mobile" : ""}`}>
                  <div onClick={() => onMovieClick(item.id)} className="card-title">
                    {item.title} ({new Date(item.releaseDate).getFullYear()})
                  </div>
                  <div className="card-meta-row">
                    {item.rating && <span className="badge-rating">{item.rating}</span>}
                    {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
                    {item.imdbRating && <span className="badge-imdb">&#9733; {item.imdbRating}</span>}
                  </div>
                  <div className="card-actor-row">
                    {item.actors.split(",").map((actor, i) => (
                      <button key={i} type="button" className="actor-link" onClick={() => actorSearch(actor.trim())}>
                        {actor.trim()}
                      </button>
                    ))}
                  </div>
                  <PlotText text={item.plot} className="card-plot" hiddenClass={isMobile ? "card-plot--hidden" : ""} />
                </div>
              </div>
              <PlotText text={item.plot} className="card-plot-below" hiddenClass={isMobile ? "card-plot-below--visible" : ""} />
              <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
            </Card>
          </List.Item>
        );
      }}
    />
  );
}

export default CardList;
