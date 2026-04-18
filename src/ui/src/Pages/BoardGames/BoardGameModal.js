import { useState, useEffect } from "react";
import { Modal, Input, Button, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./BoardGameModal.css";

const { TextArea } = Input;

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

function EditField({ label, value, onChange, multiline = false, type = "text" }) {
  return (
    <div className="edit-field">
      <label className="edit-field-label">{label}</label>
      {multiline ? (
        <TextArea rows={4} value={value ?? ""} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <Input type={type} value={value ?? ""} onChange={(e) => onChange(e.target.value)} />
      )}
    </div>
  );
}

function BoardGameModal({ gameId, open, onClose, games, userData, onGameUpdated }) {
  const game = games.find((g) => g.id === gameId);

  const [editing, setEditing] = useState(false);
  const [editState, setEditState] = useState({});
  const [saving, setSaving] = useState(false);
  const [rematchId, setRematchId] = useState("");
  const [rematching, setRematching] = useState(false);

  useEffect(() => {
    if (!open) {
      setEditing(false);
      setEditState({});
      setRematchId("");
    }
  }, [open]);

  if (!game) return null;

  const minP = game.minPlayers;
  const maxP = game.maxPlayers;
  const players = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;
  const description = stripHtml(game.description);

  function startEditing() {
    setEditState({ ...game, description: stripHtml(game.description), imageUrl: game.imageUrl ?? "" });
    setEditing(true);
  }

  function cancelEditing() {
    setEditing(false);
    setEditState({});
    setRematchId("");
  }

  function updateField(field, value) {
    setEditState((prev) => ({ ...prev, [field]: value }));
  }

  async function saveChanges() {
    setSaving(true);
    try {
      const response = await MovieAPI.updateBoardgame(editState);
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        message.error(body.message || `Server error (${response.status})`);
        return;
      }
      const result = await response.json();
      if (result.success) {
        message.success("Boardgame updated");
        setEditing(false);
        if (onGameUpdated) onGameUpdated(result.data);
      } else {
        message.error(result.message || "Save failed");
      }
    } catch {
      message.error("Error saving boardgame");
    } finally {
      setSaving(false);
    }
  }

  function confirmRematch() {
    const bggId = parseInt(rematchId, 10);
    if (!bggId || bggId <= 0) {
      message.error("Enter a valid BGG Thing ID");
      return;
    }
    Modal.confirm({
      title: `Re-match to BGG ID ${bggId}?`,
      content: "This will replace all data and images for this game with the correct BGG entry. This cannot be undone.",
      okText: "Re-match",
      okType: "danger",
      cancelText: "Cancel",
      onOk: async () => {
        setRematching(true);
        try {
          const response = await MovieAPI.rematchBoardgame(game.id, bggId);
          if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            message.error(body.message || `Server error (${response.status})`);
            return;
          }
          const result = await response.json();
          if (result.success) {
            message.success("Boardgame re-matched successfully");
            setEditing(false);
            setRematchId("");
            if (onGameUpdated) onGameUpdated(result.data);
          } else {
            message.error(result.message || "Re-match failed");
          }
        } catch {
          message.error("Error during re-match");
        } finally {
          setRematching(false);
        }
      },
    });
  }

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={800} wrapClassName="boardgame-modal">
      <div className="boardgame-modal-body">
        <div className="boardgame-modal-poster-column">
          <img
            className="boardgame-modal-poster"
            alt={game.name}
            src={`/BoardgameImage/${game.id}${game.imageVersion != null ? `?v=${game.imageVersion}` : ""}`}
          />
        </div>
        <div className="boardgame-modal-info-panel">
          {!editing ? (
            <>
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
              {userData?.canEditMovies && (
                <div className="modal-edit-row">
                  <Button type="default" onClick={startEditing}>
                    <span className="fas fa-pen" style={{ marginRight: 6 }} />
                    Edit
                  </Button>
                </div>
              )}
              <div className="boardgame-modal-id">id #{game.id} · BGG #{game.bggThingId}</div>
            </>
          ) : (
            <div className="modal-edit-form">
              <EditField label="Name" value={editState.name} onChange={(v) => updateField("name", v)} />
              <EditField label="Year Published" value={editState.yearPublished} onChange={(v) => updateField("yearPublished", v ? Number(v) : null)} type="number" />
              <EditField label="Min Players" value={editState.minPlayers} onChange={(v) => updateField("minPlayers", v ? Number(v) : null)} type="number" />
              <EditField label="Max Players" value={editState.maxPlayers} onChange={(v) => updateField("maxPlayers", v ? Number(v) : null)} type="number" />
              <EditField label="Playing Time (min)" value={editState.playingTime} onChange={(v) => updateField("playingTime", v ? Number(v) : null)} type="number" />
              <EditField label="Min Age" value={editState.minAge} onChange={(v) => updateField("minAge", v ? Number(v) : null)} type="number" />
              <EditField label="Description" value={editState.description} onChange={(v) => updateField("description", v)} multiline />
              <EditField label="Image URL (re-downloads on save)" value={editState.imageUrl} onChange={(v) => updateField("imageUrl", v)} />

              <div className="modal-edit-actions">
                <Button type="primary" onClick={saveChanges} loading={saving}>Save</Button>
                <Button onClick={cancelEditing}>Cancel</Button>
              </div>

              <div className="boardgame-rematch-section">
                <div className="boardgame-rematch-label">Wrong game? Re-match to a different BGG ID</div>
                <div className="boardgame-rematch-row">
                  <Input
                    placeholder="BGG Thing ID"
                    value={rematchId}
                    onChange={(e) => setRematchId(e.target.value)}
                    style={{ width: 160 }}
                    type="number"
                  />
                  <Button danger onClick={confirmRematch} loading={rematching}>
                    Re-match
                  </Button>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}

export default BoardGameModal;
