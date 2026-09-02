import { memo } from "react";
import { Card } from "antd";
import { MovieAPI } from "../../MovieAPI";
import FallbackImage from "../../Components/FallbackImage";
import UserMovieOptions from "./UserMovieOptions";
import "./CardList.css";

/**
 * The movie grid's cards — the section's OWN presentation, laid out by the catalog package's Grid
 * (R9 S3: `CatalogSource.renderCard`). The engine, the letter strip, the band skeletons and the
 * tweaks plumbing are the package's; the markup below is unchanged from the CardList shell these
 * were lifted out of, plus the tweak contract:
 *
 *  - the root wears `bx-card` + the host's hover class, and the poster box wears `bx-cover`, so
 *    Hover (lift / zoom / tilt / dim) and Rounded apply through `catalog-views.css`;
 *  - the cover sizes off the Grid's `--cell` variable (`catalog-views.css` sets it from the
 *    cover-size tweak), so the size slider moves a movie card like any other;
 *  - `metadata: "minimal"` drops the badge row, the cast row and the plot — poster, title and the
 *    Seen/Want controls remain, which is what "minimal" means on a card this dense.
 *
 * Both cards are memoized on PRIMITIVES (isWatched/isWanted booleans, activeName string, stable
 * handlers) rather than the whole userData object, so a Seen/Want toggle or a modal open/close
 * re-renders only the card that changed. Every callback they receive is stabilized in Browse.
 */

/** The Grid's base cover height for Movies before the cover-size tweak (the poster's old 200px). */
export const MOVIE_GRID_CELL = 200;
/** The mobile "simple" style is a taller poster tile, not a horizontal card. */
export const MOVIE_SIMPLE_CELL = 200;

// Poster thumbnail with a graceful fallback: when the image 404s (common for Misc videos, which
// usually have no poster), swap in a placeholder instead of the browser's broken-image glyph.
// FallbackImage is the site-wide convention for this — it also heals if the src later changes.
// `retry`: this card lives in a streamed band, so the catalog's image-failure law applies (a
// transient failure under a fling's burst is retried, and the placeholder is dormant, not final —
// what the package card already does for these same posters on every other view).
function CardPoster({ item, isAboveFold }) {
  const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion, item.kind);
  return (
    <FallbackImage
      className="card-poster-image"
      alt=""
      src={thumbUrl}
      retry
      loading={isAboveFold ? "eager" : "lazy"}
      // lowercase: React 18 only knows the DOM attribute spelling (camelCase lands in v19) and
      // warns + drops the camelCase prop on every card render.
      fetchpriority={isAboveFold ? "high" : "auto"}
      decoding="async"
      fallback={
        <div className="card-poster-placeholder" aria-hidden="true">
          <span className="card-poster-placeholder-icon">🎞</span>
        </div>
      }
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

export const MovieCard = memo(function MovieCard({
  item,
  eager,
  metadata,
  hoverClass,
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
  const rich = metadata !== "minimal";

  return (
    <div className={`card-cell bx-card${hoverClass ? ` ${hoverClass}` : ""}`}>
      <Card hoverable className="movie-card">
        <div className="card-content-wrapper">
          <div className="card-poster-container bx-cover">
            <CardPoster item={item} isAboveFold={eager} />
          </div>
          <div className="card-right-col">
            <div
              onClick={isMisc ? undefined : () => onMovieClick(item.id, item.kind)}
              className={`card-title${isMisc ? " card-title--static" : ""}`}
            >
              {item.title}{year ? <span className="card-title-year"> ({year})</span> : ""}
            </div>
            {rich && (
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
            )}
            {rich && (
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
            )}
            {rich && <PlotText text={summaryText} className="card-plot" />}
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

/**
 * The phone "simple" card style — a poster tile with the title, a meta line and the Seen/Want row.
 * Still a MovieCard variant (Eric's S3 ruling), NOT a package view mode: it rides the same Grid
 * bands, so it finally gets the pills, the letter strip and the tweaks the horizontal card has.
 */
export const SimpleMovieCard = memo(function SimpleMovieCard({
  item,
  eager,
  metadata,
  hoverClass,
  showOptions,
  isWatched,
  isWanted,
  onMovieClick,
  onToggleSeen,
  onToggleWant,
}) {
  const isMisc = item.kind === "misc";
  const thumbUrl = MovieAPI.getPosterThumbnail(item.id, item.posterVersion, item.kind);
  const year = item.releaseDate ? new Date(item.releaseDate).getFullYear() : null;
  const metaText = isMisc
    ? [year, item.category || "Misc"].filter(Boolean).join(" • ")
    : [year, item.rating, item.runtime].filter(Boolean).join(" • ");

  return (
    <div className={`simple-card-cell bx-card${hoverClass ? ` ${hoverClass}` : ""}`}>
      <div className="mobile-movie-card">
        <div className="simple-card-poster bx-cover">
          <FallbackImage
            className="simple-card-poster-img"
            alt={item.title}
            src={thumbUrl}
            retry
            loading={eager ? "eager" : "lazy"}
            fetchpriority={eager ? "high" : "auto"}
            decoding="async"
          />
        </div>
        <div
          className="simple-card-text"
          onClick={isMisc ? undefined : () => onMovieClick(item.id, item.kind)}
        >
          <div className="simple-card-title">{item.title}</div>
          {metadata !== "minimal" && <div className="simple-card-meta">{metaText}</div>}
        </div>
        {!isMisc && showOptions && (
          <div className="simple-card-actions">
            <UserMovieOptions
              id={item.id}
              kind={item.kind}
              isWatched={isWatched}
              isWanted={isWanted}
              onToggleSeen={onToggleSeen}
              onToggleWant={onToggleWant}
              inline
            />
          </div>
        )}
      </div>
    </div>
  );
});
