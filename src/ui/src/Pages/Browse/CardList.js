import { MovieAPI } from "../../MovieAPI";
import { Card } from "antd";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";
import { useRef, useLayoutEffect, useState } from "react";

function PlotText({ text, className, hiddenClass }) {
  const ref = useRef(null);
  const [overflows, setOverflows] = useState(false);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;

    const check = () => setOverflows(el.scrollHeight > el.clientHeight);
    check();

    const observer = new ResizeObserver(check);
    observer.observe(el);
    return () => observer.disconnect();
  }, [text]);

  const classes = [className, hiddenClass, overflows ? "card-plot--faded" : ""].filter(Boolean).join(" ");

  return <p ref={ref} className={classes}>{text}</p>;
}

function CardList({ movieDataArray, userData, setUserData, actorSearch, activePerson, onMovieClick, onToggleViewing, isMobile }) {
  const activeName = (activePerson || "").trim().toLowerCase();
  return (
    <div className="card-list">
      {movieDataArray.map((item) => {
        const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion);

        return (
          <div key={item.id}>
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
                    {item.actors.split(",").map((actor, i) => {
                      const name = actor.trim();
                      const isActive = activeName && name.toLowerCase() === activeName;
                      return (
                        <button
                          key={i}
                          type="button"
                          className={`actor-link${isActive ? " actor-link--active" : ""}`}
                          onClick={() => actorSearch(name)}
                        >
                          {name}
                        </button>
                      );
                    })}
                  </div>
                  <PlotText text={item.plot} className="card-plot" hiddenClass={isMobile ? "card-plot--hidden" : ""} />
                </div>
              </div>
              <PlotText text={item.plot} className="card-plot-below" hiddenClass={isMobile ? "card-plot-below--visible" : ""} />
              <UserMovieOptions userData={userData} id={item.id} setUserData={setUserData} onToggleViewing={onToggleViewing} />
            </Card>
          </div>
        );
      })}
    </div>
  );
}

export default CardList;
