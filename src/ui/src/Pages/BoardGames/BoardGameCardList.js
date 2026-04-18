import { Card } from "antd";
import "../Browse/CardList.css";
import "./BoardGameCardList.css";

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

function BoardGameCardList({ games, onGameClick }) {
  return (
    <div className="card-list">
      {games.map((game) => {
        const v = game.imageVersion != null ? `?v=${game.imageVersion}` : "";
        const thumbUrl = `/BoardgameImageThumb/${game.id}${v}`;
        const minP = game.minPlayers;
        const maxP = game.maxPlayers;
        const players = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;
        const minT = game.minPlayTime;
        const maxT = game.maxPlayTime ?? game.playingTime;
        const playtime = minT && maxT ? (minT === maxT ? `${minT}` : `${minT}–${maxT}`) : maxT || minT || null;
        const description = stripHtml(game.description);

        return (
          <div key={game.id}>
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
                    {players && <span className="badge-rating">👥 {players}</span>}
                    {playtime ? <span className="badge-runtime">⏱ {playtime} min</span> : null}
                    {game.averageRating ? <span className="badge-imdb">★ {Number(game.averageRating).toFixed(1)}</span> : null}
                    {game.averageWeight ? <span className="badge-rating">⚖ {Number(game.averageWeight).toFixed(1)}/5</span> : null}
                  </div>
                  {description && <p className="card-plot">{description.substring(0, 300)}</p>}
                </div>
              </div>
            </Card>
          </div>
        );
      })}
    </div>
  );
}

export default BoardGameCardList;
