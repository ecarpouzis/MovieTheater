import { memo, useMemo } from "react";
import { Card, Tooltip } from "antd";
import "../Browse/CardList.css";
import "./BoardGameCardList.css";
import { stripHtml } from "./boardGameUtils";
import useTouchDevice from "../../hooks/useTouchDevice";
import useGridWindow from "../../hooks/useGridWindow";
import FallbackImage from "../../Components/FallbackImage";
import CatalogPager from "../../Components/CatalogPager";

// Matches Music's PAGE_STEP: only feeds CatalogPager's page-mode arithmetic (letters mode ignores it).
const PAGE_STEP = 120;

// One shared empty list for the (majority) games with no expansions. A fresh `[]` per render is a new
// prop identity, which silently defeats BoardGameCard's memo for exactly those cards.
const NO_EXPANSIONS = [];

// One card. The DOM deliberately reuses the Browse card skeleton (CardList.css classes) with
// boardgame-specific overrides layered in BoardGameCardList.css. Memoized so a filter/sort/modal
// render doesn't re-render every mounted card.
const BoardGameCard = memo(function BoardGameCard({ game, expansions, tooltipTrigger, onGameClick }) {
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

  const expansionMaxPlayers = expansions.reduce(
    (acc, exp) => (exp.maxPlayers != null && exp.maxPlayers > acc ? exp.maxPlayers : acc),
    maxP ?? 0
  );
  const hasExtendedPlayers = expansionMaxPlayers > (maxP ?? 0);

  // Build a single player-count string; base portion is plain, extension appended separately
  const basePlayers = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;

  return (
    <div className="boardgame-card-wrapper">
      <Card hoverable className="movie-card boardgame-card">
        <div className="card-content-wrapper">
          <div className="card-poster-container boardgame-card-poster-container">
            <FallbackImage
              className="card-poster-image boardgame-card-poster-image"
              alt={game.name || "Board game"}
              src={thumbUrl}
              loading="lazy"
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
            {/* Always-on CSS fade, like Browse's PlotText: the per-card ResizeObserver overflow
                measurement this used to carry was the pre-optimization Browse version (deleted
                there as views-perf catalog #4) still running here. */}
            {description && (
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

/**
 * The boardgame grid, windowed like every other big list on the site (useGridWindow: only the rows
 * near the viewport stay mounted; the spacers hold the rest of the height). `listKey` names what
 * makes it a DIFFERENT list — the filter/sort params — so the window resets to the top with them.
 * `letters` (A–Z buckets over the current list, or null) turns on the CatalogPager quick-scroll
 * bar: a tap is a SCROLL within the same list, not a re-filter, so everything before the tapped
 * letter is still above you — the Music/Arcade convention.
 */
function BoardGameCardList({ games, expansionMap, onGameClick, listKey, letters }) {
  const tooltipTrigger = useTouchDevice() ? "click" : "hover";
  const { hostRef, gridRef, start, end, padTop, padBottom, visibleStart, scrollToIndex } =
    useGridWindow(games.length, { resetKey: listKey });
  const visible = useMemo(() => games.slice(start, end), [games, start, end]);

  return (
    <>
      <div ref={hostRef} className="card-list-host">
        {padTop > 0 && <div className="grid-spacer" style={{ height: padTop }} aria-hidden="true" />}
        <div className="card-list" ref={gridRef}>
          {visible.map((game) => (
            <BoardGameCard
              key={game.id}
              game={game}
              expansions={expansionMap?.[game.id] ?? NO_EXPANSIONS}
              tooltipTrigger={tooltipTrigger}
              onGameClick={onGameClick}
            />
          ))}
        </div>
        {padBottom > 0 && <div className="grid-spacer" style={{ height: padBottom }} aria-hidden="true" />}
      </div>
      {letters && letters.length > 0 && (
        <CatalogPager
          mode="letters"
          letters={letters}
          total={games.length}
          pageSize={PAGE_STEP}
          currentIndex={visibleStart}
          onJump={(offset) => scrollToIndex(Math.max(0, offset))}
          itemNoun="game"
        />
      )}
    </>
  );
}

export default BoardGameCardList;
