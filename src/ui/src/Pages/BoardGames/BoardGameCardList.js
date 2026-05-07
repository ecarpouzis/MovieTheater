import { useRef, useLayoutEffect, useState } from "react";
import { Card, Tooltip } from "antd";
import "../Browse/CardList.css";
import "./BoardGameCardList.css";

function PlotText({ text }) {
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

  return <p ref={ref} className={`card-plot${overflows ? " card-plot--faded" : ""}`}>{text}</p>;
}

function stripHtml(html) {
  if (!html) return "";
  return html
    .replace(/<[^>]*>/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#039;/g, "'")
    .replace(/&#10;/g, " ")
    .trim();
}

function BoardGameCardList({ games, expansionMap, onGameClick }) {
  return (
    <div className="card-list">
      {games.map((game) => {
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

        const expansions = expansionMap?.[game.id] ?? [];
        const expansionMaxPlayers = expansions.reduce(
          (acc, exp) => (exp.maxPlayers != null && exp.maxPlayers > acc ? exp.maxPlayers : acc),
          maxP ?? 0
        );
        const hasExtendedPlayers = expansionMaxPlayers > (maxP ?? 0);

        // Build a single player-count string; base portion is plain, extension appended separately
        const basePlayers = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;

        return (
          <div key={game.id} className="boardgame-card-wrapper">
            <Card hoverable className="movie-card boardgame-card">
              <div className="card-content-wrapper">
                <div className="card-poster-container boardgame-card-poster-container">
                  <img className="card-poster-image boardgame-card-poster-image" alt={game.name || "Board game"} src={thumbUrl} loading="lazy" />
                </div>
                <div className="card-right-col">
                  <div onClick={() => onGameClick(game.id)} className="card-title">
                    {game.name} {game.yearPublished ? `(${game.yearPublished})` : ""}
                  </div>
                  <div className="card-meta-row">
                    {(basePlayers || hasExtendedPlayers) && (
                      <span className="badge-rating">
                        👥 {basePlayers ?? expansionMaxPlayers}
                        {hasExtendedPlayers && <span className="badge-exp-ext">→{expansionMaxPlayers}</span>}
                      </span>
                    )}
                    {playtime ? <span className="badge-runtime">⏱ {playtime} min</span> : null}
                    {game.averageRating ? <span className="badge-imdb">★ {Number(game.averageRating).toFixed(1)}</span> : null}
                    {game.averageWeight ? <span className="badge-rating">🧠 {Number(game.averageWeight).toFixed(1)}/5</span> : null}
                  </div>
                  {description && <PlotText text={description.substring(0, 300)} />}
                </div>
              </div>
            </Card>
            {expansions.length > 0 && (
              <Tooltip title={expansions.map((e) => e.name).join(", ")} placement="left">
                <div className="card-expansion-flag">{expansions.length}</div>
              </Tooltip>
            )}
          </div>
        );
      })}
    </div>
  );
}

export default BoardGameCardList;
