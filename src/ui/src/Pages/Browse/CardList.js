import { MovieAPI } from "../../MovieAPI";
import { Card } from "antd";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";
import { useRef, useLayoutEffect, useState } from "react";

// Poster thumbnail with a graceful fallback: when the image 404s (common for Misc videos, which
// usually have no poster), swap in a placeholder instead of the browser's broken-image glyph.
function CardPoster({ item, isMobile, isAboveFold }) {
  const [failed, setFailed] = useState(false);
  const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion, item.kind);
  if (failed) {
    return (
      <div className={`card-poster-placeholder${isMobile ? " card-poster-placeholder--mobile" : ""}`} aria-hidden="true">
        <span className="card-poster-placeholder-icon">🎞</span>
      </div>
    );
  }
  return (
    <img
      className={`card-poster-image${isMobile ? " card-poster-image--mobile" : ""}`}
      alt=""
      src={thumbUrl}
      loading={isAboveFold ? "eager" : "lazy"}
      fetchPriority={isAboveFold ? "high" : "auto"}
      decoding="async"
      onError={() => setFailed(true)}
    />
  );
}

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
      {movieDataArray.map((item, index) => {
        // Eagerly fetch the first couple of rows (up to a 4-wide grid) so the posters
        // above the fold paint immediately instead of waiting on lazy-load intersection;
        // everything below stays lazy.
        const isAboveFold = index < 8;
        // Prefer the IMDB summary; fall back to the legacy plot only when there isn't one.
        const summaryText = item.plotFull || item.plot;
        // Pills come from the FK-derived top cast; fall back to legacy actors if absent.
        const castNames = (item.topCast || item.actors || "")
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean);
        const isMisc = item.kind === "misc";
        // Misc videos have no IMDb id, no ratings, and don't fit the movie detail modal or the
        // Seen/Want (Viewing) model — so they render as informational cards: poster (or placeholder),
        // a Misc/category badge, and their description.
        const year = item.releaseDate ? new Date(item.releaseDate).getFullYear() : null;

        return (
          <div key={`${item.kind || "movie"}-${item.id}`}>
            <Card hoverable className="movie-card">
              <div className={`card-content-wrapper${isMobile ? " card-content-wrapper--mobile" : ""}`}>
                <div className="card-poster-container">
                  <CardPoster item={item} isMobile={isMobile} isAboveFold={isAboveFold} />
                </div>
                <div className={`card-right-col${isMobile ? " card-right-col--mobile" : ""}`}>
                  <div
                    onClick={isMisc ? undefined : () => onMovieClick(item.id, item.kind)}
                    className={`card-title${isMisc ? " card-title--static" : ""}`}
                  >
                    {item.title}{year ? ` (${year})` : ""}
                  </div>
                  <div className="card-meta-row">
                    {item.kind === "series" && <span className="badge-rating">📺 Series</span>}
                    {isMisc && <span className="badge-misc">🎞 {item.category || "Misc"}</span>}
                    {item.rating && <span className="badge-rating">{item.rating}</span>}
                    {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
                    {item.imdbRating && <span className="badge-imdb">&#9733; {item.imdbRating}</span>}
                  </div>
                  <div className="card-actor-row">
                    {castNames.map((name, i) => {
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
                  <PlotText text={summaryText} className="card-plot" hiddenClass={isMobile ? "card-plot--hidden" : ""} />
                </div>
              </div>
              <PlotText text={summaryText} className="card-plot-below" hiddenClass={isMobile ? "card-plot-below--visible" : ""} />
              {!isMisc && (
                <UserMovieOptions userData={userData} id={item.id} kind={item.kind} setUserData={setUserData} onToggleViewing={onToggleViewing} />
              )}
            </Card>
          </div>
        );
      })}
    </div>
  );
}

export default CardList;
