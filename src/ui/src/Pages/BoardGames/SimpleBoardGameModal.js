import { Modal } from "antd";
import { useEffect, useState } from "react";
import "./SimpleBoardGameModal.css";

function SimpleBoardGameModal({ gameId, open, onClose, gameDataArray }) {
  const [game, setGame] = useState(null);

  useEffect(() => {
    if (gameId && gameDataArray) {
      const foundGame = gameDataArray.find((g) => g.id === gameId);
      setGame(foundGame || null);
    } else {
      setGame(null);
    }
  }, [gameId, gameDataArray]);

  if (!game) return null;

  return (
    <Modal open={open} onCancel={onClose} footer={null} width="95%" centered className="simple-boardgame-modal">
      <div className="simple-boardgame-modal-content">
        <div className="simple-boardgame-modal-image-container">
          <img
            src={game.thumbnailUrl || game.placeholderImage}
            alt={game.title}
            className="simple-boardgame-modal-image"
            onError={(e) => {
              if (game.placeholderImage && e.currentTarget.src !== game.placeholderImage) {
                e.currentTarget.onerror = null;
                e.currentTarget.src = game.placeholderImage;
              }
            }}
          />
        </div>
        <div className="simple-boardgame-modal-info">
          <h2 className="simple-boardgame-modal-title">{game.title}</h2>
          <div className="simple-boardgame-modal-meta">
            <div className="simple-boardgame-modal-meta-item">
              <strong>Year:</strong> {new Date(game.releaseDate).getFullYear()}
            </div>
            <div className="simple-boardgame-modal-meta-item">
              <strong>Players:</strong> {game.players}
            </div>
            <div className="simple-boardgame-modal-meta-item">
              <strong>Playtime:</strong> {game.playtime}
            </div>
            <div className="simple-boardgame-modal-meta-item">
              <strong>Complexity:</strong> {game.complexity}
            </div>
          </div>
          <div className="simple-boardgame-modal-description">
            <p>
              This is a placeholder description for {game.title}. In the future, this will contain detailed information about the game, including
              rules, strategy tips, and more.
            </p>
          </div>
        </div>
      </div>
    </Modal>
  );
}

export default SimpleBoardGameModal;
