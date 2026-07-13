import { MovieAPI } from "../../MovieAPI";
import { Card } from "antd";
import UserMovieOptions, { useViewingToggles } from "./UserMovieOptions";
import "./CardList.css";
import { memo, useCallback, useEffect, useMemo, useState } from "react";
import { preloadImages } from "../../preloadImages";
import useGridWindow from "../../hooks/useGridWindow";

// Posters fetched ahead of the mounted window, so a card's <img> renders from cache the moment the
// window reaches it rather than snapping in. Bounded — the old code preloaded every card the list
// had ever loaded, which meant 60 fresh image requests per appended page competing with the posters
// actually on screen.
const PRELOAD_AHEAD = 24;

// Poster thumbnail with a graceful fallback: when the image 404s (common for Misc videos, which
// usually have no poster), swap in a placeholder instead of the browser's broken-image glyph.
function CardPoster({ item, isAboveFold }) {
  const [failed, setFailed] = useState(false);
  const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion, item.kind);
  if (failed) {
    return (
      <div className="card-poster-placeholder" aria-hidden="true">
        <span className="card-poster-placeholder-icon">🎞</span>
      </div>
    );
  }
  return (
    <img
      className="card-poster-image"
      alt=""
      src={thumbUrl}
      loading={isAboveFold ? "eager" : "lazy"}
      fetchPriority={isAboveFold ? "high" : "auto"}
      decoding="async"
      onError={() => setFailed(true)}
    />
  );
}

// The plot text is clamped to a fixed-height box and faded at the bottom. The fade was
// previously gated by a per-card ResizeObserver that measured overflow — at thousands of
// cards that per-item layout work was a major render cost (views-perf catalog #4), so the
// fade is now always-on via CSS. When the text is short it sits over empty white space and
// is invisible anyway, so there's no visual regression.
function PlotText({ text, className, hiddenClass }) {
  const classes = [className, hiddenClass, "card-plot--faded"].filter(Boolean).join(" ");
  return <p className={classes}>{text}</p>;
}

// One grid card. Memoized on PRIMITIVES (isWatched/isWanted booleans, activeName string, stable
// handlers) rather than the whole userData object, so a Seen/Want toggle, an infinite-scroll
// append, or a modal open/close only re-renders the card that actually changed — not every mounted
// card in the grid. All callbacks it receives are stabilized (useCallback / useViewingToggles) in
// the parent so the memo isn't defeated by fresh closures each render.
const MovieCard = memo(function MovieCard({
  item,
  isAboveFold,
  activeName,
  showOptions,
  isWatched,
  isWanted,
  onMovieClick,
  onActorSearch,
  onToggleSeen,
  onToggleWant,
}) {
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
    <div className="card-cell">
      <Card hoverable className="movie-card">
        <div className="card-content-wrapper">
          <div className="card-poster-container">
            <CardPoster item={item} isAboveFold={isAboveFold} />
          </div>
          <div className="card-right-col">
            <div
              onClick={isMisc ? undefined : () => onMovieClick(item.id, item.kind)}
              className={`card-title${isMisc ? " card-title--static" : ""}`}
            >
              {item.title}{year ? <span className="card-title-year"> ({year})</span> : ""}
            </div>
            <div className="card-meta-row">
              {item.kind === "series" && <span className="badge-rating">📺 Series</span>}
              {isMisc && <span className="badge-misc">🎞 {item.category || "Misc"}</span>}
              {item.rating && (
                <span
                  className="badge-rating"
                  title={item.ratingEstimated ? "Estimated rating — no official certificate" : undefined}
                >
                  {item.rating}{item.ratingEstimated ? " ~" : ""}
                </span>
              )}
              {item.runtime && <span className="badge-runtime">{item.runtime}</span>}
              {item.imdbRating && <span className="badge-imdb">&#9733; {item.imdbRating}</span>}
            </div>
            <div className="card-actor-row">
              {castNames.map((name, i) => {
                const isActive = activeName && name.toLowerCase() === activeName;
                return (
                  <span key={i} className="actor-link-wrap">
                    {i > 0 && <span className="actor-sep">, </span>}
                    <button
                      type="button"
                      className={`actor-link${isActive ? " actor-link--active" : ""}`}
                      onClick={() => onActorSearch(name)}
                    >
                      {name}
                    </button>
                  </span>
                );
              })}
            </div>
            <PlotText text={summaryText} className="card-plot" />
            {/* Seen/Want live INSIDE the right column (centered at its base) so the card's height is
                set by the poster — one horizontal layout on both desktop and mobile. */}
            {!isMisc && showOptions && (
              <UserMovieOptions
                id={item.id}
                kind={item.kind}
                isWatched={isWatched}
                isWanted={isWanted}
                onToggleSeen={onToggleSeen}
                onToggleWant={onToggleWant}
              />
            )}
          </div>
        </div>
      </Card>
    </div>
  );
});

function CardList({ movieDataArray, userData, setUserData, actorSearch, activePerson, onMovieClick, onToggleViewing, listKey }) {
  const activeName = (activePerson || "").trim().toLowerCase();

  // O(1) membership checks per card (replaces an O(n) `.includes()` per card) — and only rebuilt
  // when the underlying id lists actually change, not on every unrelated render.
  const seenSet = useMemo(() => new Set(userData?.moviesSeen), [userData?.moviesSeen]);
  const wantSet = useMemo(() => new Set(userData?.moviesToWatch), [userData?.moviesToWatch]);

  // Stable seen/want toggles + a stable movie-click handler, so passing them to a memoized card
  // doesn't defeat the memo (a fresh closure each render would). actorSearch/onMovieClick/
  // onToggleViewing are stabilized with useCallback in Browse — they used to be plain functions
  // rebuilt on every render, which quietly defeated MovieCard's memo: appending page N re-rendered
  // all N×60 mounted cards, as did every modal open and every Seen/Want toggle.
  const { toggleSeen, toggleWant } = useViewingToggles(userData, setUserData, onToggleViewing);
  const handleMovieClick = useCallback((id, kind) => onMovieClick(id, kind), [onMovieClick]);
  const handleActorSearch = useCallback((name) => actorSearch(name), [actorSearch]);

  // Only the rows near the viewport stay mounted (useGridWindow); the rest of the list's height is
  // held by the two spacers. Cards are a fixed height here, so the reserved height is exact.
  const { hostRef, gridRef, start, end, padTop, padBottom } = useGridWindow(movieDataArray.length, { resetKey: listKey });
  const visible = useMemo(() => movieDataArray.slice(start, end), [movieDataArray, start, end]);

  // Warm the poster cache for the mounted window plus a little ahead of it, so a card scrolled into
  // the window renders its <img> from cache instead of fetching it (deduped globally).
  useEffect(() => {
    const ahead = movieDataArray.slice(start, Math.min(movieDataArray.length, end + PRELOAD_AHEAD));
    preloadImages(ahead.map((m) => MovieAPI.getPosterThumbnail(m.id, m.posterVersion, m.kind)));
  }, [movieDataArray, start, end]);

  return (
    <div ref={hostRef} className="card-list-host">
      {padTop > 0 && <div className="grid-spacer" style={{ height: padTop }} aria-hidden="true" />}
      <div className="card-list" ref={gridRef}>
        {visible.map((item, i) => {
          const index = start + i;
          // Eagerly fetch the first couple of rows (up to a 4-wide grid) so the posters above the fold
          // paint immediately instead of waiting on lazy-load intersection; everything below stays lazy.
          return (
            <MovieCard
              key={`${item.kind || "movie"}-${item.id}`}
              item={item}
              isAboveFold={index < 8}
              activeName={activeName}
              showOptions={!!userData}
              isWatched={userData ? seenSet.has(item.id) : false}
              isWanted={userData ? wantSet.has(item.id) : false}
              onMovieClick={handleMovieClick}
              onActorSearch={handleActorSearch}
              onToggleSeen={toggleSeen}
              onToggleWant={toggleWant}
            />
          );
        })}
      </div>
      {padBottom > 0 && <div className="grid-spacer" style={{ height: padBottom }} aria-hidden="true" />}
    </div>
  );
}

export default CardList;
