import { Modal } from "antd";
import "./BoardGameModal.css";

function stripHtml(html) {
  if (!html) return "";
  return html
    .replace(/<[^>]*>/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#039;/g, "'")
    .trim();
}

function BoardGameModal({ gameId, open, onClose, games }) {
  const game = games.find((g) => g.id === gameId);

  if (!game) return null;

  const minP = game.minPlayers;
  const maxP = game.maxPlayers;
  const players = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;
  const description = stripHtml(game.description);

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={800} wrapClassName="boardgame-modal">
      <div className="boardgame-modal-body">
        <div className="boardgame-modal-poster-column">
          <img
            className="boardgame-modal-poster"
            alt={game.name}
            src={`/BoardgameImage/${game.id}`}
          />
        </div>
        <div className="boardgame-modal-info-panel">
          <h2 className="boardgame-modal-title">{game.name}</h2>
          <div className="boardgame-modal-meta-row">
            {game.yearPublished && <span>{game.yearPublished}</span>}
            {players && (
              <>
                <span className="modal-dot">·</span>
                <span>👥 {players} players</span>
              </>
            )}
            {game.playingTime && (
              <>
                <span className="modal-dot">·</span>
                <span>⏱ {game.playingTime} min</span>
              </>
            )}
          </div>
          <div className="boardgame-modal-stats-row">
            {game.averageRating ? (
              <div className="boardgame-modal-stat">
                <span className="modal-label">BGG Rating</span>
                <span className="boardgame-modal-stat-value">★ {Number(game.averageRating).toFixed(1)}/10</span>
              </div>
            ) : null}
            {game.averageWeight ? (
              <div className="boardgame-modal-stat">
                <span className="modal-label">Complexity</span>
                <span className="boardgame-modal-stat-value">{Number(game.averageWeight).toFixed(2)}/5</span>
              </div>
            ) : null}
            {game.minAge ? (
              <div className="boardgame-modal-stat">
                <span className="modal-label">Min Age</span>
                <span className="boardgame-modal-stat-value">{game.minAge}+</span>
              </div>
            ) : null}
          </div>
          {description && <p className="boardgame-modal-plot">{description}</p>}
          <a
            className="boardgame-bgg-link"
            href={`https://boardgamegeek.com/boardgame/${game.bggThingId}`}
            target="_blank"
            rel="noreferrer"
          >
            View on BoardGameGeek
          </a>
          <div className="boardgame-modal-id">id #{game.id}</div>
        </div>
      </div>
    </Modal>
  );
}

export default BoardGameModal;
