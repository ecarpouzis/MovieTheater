import { memo } from "react";
import { Card, Tooltip } from "antd";
import "../Browse/CardList.css";
import "./BoardGameCardList.css";
import { stripHtml } from "./boardGameUtils";
import FallbackImage from "../../Components/FallbackImage";

/**
 * The boardgame grid's card — the section's own presentation, laid out by the catalog package's
 * Grid (R9 S3: `CatalogSource.renderCard`). The DOM deliberately reuses the Browse card skeleton
 * (CardList.css classes) with boardgame-specific overrides layered in BoardGameCardList.css, and
 * now carries the tweak contract too: `bx-card` + the host's hover class on the root, `bx-cover` on
 * the box art (so Hover and Rounded apply through catalog-views.css), the box sized off the Grid's
 * `--cell`, and `metadata: "minimal"` dropping the chips and the description.
 *
 * Memoized so a filter/sort/modal render doesn't re-render every mounted card.
 */

/** The Grid's base box-art height for Boardgames before the cover-size tweak (the art's old 200px). */
export const BOARDGAME_GRID_CELL = 200;

/** One shared empty list for the (majority) games with no expansions — a fresh `[]` per render would defeat the memo. */
export const NO_EXPANSIONS = [];

const BoardGameCard = memo(function BoardGameCard({ game, expansions, tooltipTrigger, metadata, hoverClass, eager, onGameClick }) {
  const v = game.imageVersion != null ? `?v=${game.imageVersion}` : "";
  const thumbUrl = `/BoardgameImageThumb/${game.id}${v}`;
  const minP = game.minPlayers;
  const maxP = game.maxPlayers;
  const minT = game.minPlayTime;
  const maxT = game.maxPlayTime ?? game.playingTime;
  const fmtT = (t) => (t != null && t > 999 ? "∞" : t);
  const dMin = fmtT(minT);
  const dMax = fmtT(maxT);
  const playtime = dMin != null && dMax != null ? (dMin === dMax ? `${dMin}` : `${dMin}–${dMax}`) : dMax ?? dMin ?? null;
  const description = stripHtml(game.description);
  const rich = metadata !== "minimal";

  const expansionMaxPlayers = expansions.reduce(
    (acc, exp) => (exp.maxPlayers != null && exp.maxPlayers > acc ? exp.maxPlayers : acc),
    maxP ?? 0
  );
  const hasExtendedPlayers = expansionMaxPlayers > (maxP ?? 0);

  // Build a single player-count string; base portion is plain, extension appended separately
  const basePlayers = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;

  return (
    <div className={`boardgame-card-wrapper bx-card${hoverClass ? ` ${hoverClass}` : ""}`}>
      <Card hoverable className="movie-card boardgame-card">
        <div className="card-content-wrapper">
          <div className="card-poster-container boardgame-card-poster-container bx-cover">
            <FallbackImage
              className="card-poster-image boardgame-card-poster-image"
              alt={game.name || "Board game"}
              src={thumbUrl}
              retry
              loading={eager ? "eager" : "lazy"}
              fetchpriority={eager ? "high" : "auto"}
              decoding="async"
              fallback={
                <div className="card-poster-placeholder" aria-hidden="true">
                  <span className="card-poster-placeholder-icon">🎲</span>
                </div>
              }
            />
          </div>
          <div className="card-right-col">
            <div onClick={() => onGameClick(game.id)} className="card-title">
              {game.name} {game.yearPublished ? `(${game.yearPublished})` : ""}
            </div>
            {rich && (
              <div className="card-meta-row">
                {(basePlayers || hasExtendedPlayers) && (
                  <span className="badge-rating">
                    👥&#x202F;{basePlayers ?? expansionMaxPlayers}
                    {hasExtendedPlayers && <span className="badge-exp-ext">→{expansionMaxPlayers}</span>}
                  </span>
                )}
                {playtime ? <span className="badge-runtime">⏱&#x202F;{playtime}</span> : null}
                {game.averageRating ? <Tooltip trigger={tooltipTrigger} title="BGG average rating (out of 100)"><span className="badge-imdb">★&#x202F;{Math.round(Number(game.averageRating) * 10)}</span></Tooltip> : null}
                {game.averageWeight ? <Tooltip trigger={tooltipTrigger} title="Complexity (0–100), based on BGG average weight out of 5"><span className="badge-rating">🧠&#x202F;{Math.round(Number(game.averageWeight) / 5 * 100)}</span></Tooltip> : null}
              </div>
            )}
            {/* Always-on CSS fade, like Browse's PlotText: the per-card ResizeObserver overflow
                measurement this used to carry was the pre-optimization Browse version (deleted
                there as views-perf catalog #4) still running here. */}
            {rich && description && (
              <p className="card-plot card-plot--faded">
                {expansions.length > 0 && <span className="expansion-float-spacer" />}
                {description}
              </p>
            )}
          </div>
        </div>
      </Card>
      {expansions.length > 0 && (
        <Tooltip
          trigger={tooltipTrigger}
          placement="left"
          styles={{ root: { maxWidth: 320 } }}
          title={
            <ul style={{ margin: 0, paddingLeft: 16 }}>
              {expansions.map((e) => <li key={e.id}>{e.name}</li>)}
            </ul>
          }
        >
          <div className="card-expansion-flag">{expansions.length}</div>
        </Tooltip>
      )}
    </div>
  );
});

export default BoardGameCard;
