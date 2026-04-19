import { useState, useEffect } from "react";
import { Modal, Input, Button, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
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

function toYouTubeEmbedUrl(url) {
  if (!url) return null;
  try {
    const u = new URL(url);
    if (u.hostname === "youtu.be") {
      return `https://www.youtube.com/embed${u.pathname}`;
    }
    const v = u.searchParams.get("v");
    if (v) return `https://www.youtube.com/embed/${v}`;
  } catch {}
  if (url.includes("youtube.com/embed/")) return url;
  return null;
}

function EditField({ label, value, onChange, multiline = false, type = "text" }) {
  return (
    <div className="edit-field">
      <label className="edit-field-label">{label}</label>
      {multiline ? (
        <Input.TextArea rows={4} value={value ?? ""} onChange={(e) => onChange(e.target.value)} />
      ) : (
        <Input type={type} value={value ?? ""} onChange={(e) => onChange(e.target.value)} />
      )}
    </div>
  );
}

function BoardGameModal({ gameId, open, onClose, games, userData, onGameUpdated }) {
  const [game, setGame] = useState(null);
  const [editing, setEditing] = useState(false);
  const [editState, setEditState] = useState({});
  const [saving, setSaving] = useState(false);
  const [rematchId, setRematchId] = useState("");
  const [rematching, setRematching] = useState(false);

  // Rules workflow state
  const [discovering, setDiscovering] = useState(false);
  const [approving, setApproving] = useState(false);
  const [savingRules, setSavingRules] = useState(false);
  const [overridePdfUrl, setOverridePdfUrl] = useState("");
  const [editVideoUrls, setEditVideoUrls] = useState([]);
  const [newVideoUrl, setNewVideoUrl] = useState("");
  const [editPdfUrl, setEditPdfUrl] = useState("");

  useEffect(() => {
    const found = games.find((g) => g.id === gameId);
    setGame(found ?? null);
  }, [gameId, games]);

  useEffect(() => {
    if (!open) {
      setEditing(false);
      setEditState({});
      setRematchId("");
      setOverridePdfUrl("");
      setEditVideoUrls([]);
      setNewVideoUrl("");
      setEditPdfUrl("");
    }
  }, [open]);

  if (!game) return null;

  const minP = game.minPlayers;
  const maxP = game.maxPlayers;
  const players = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;
  const description = stripHtml(game.description);
  const videoUrls = game.howToPlayVideoUrls ?? [];
  const embedUrls = videoUrls.map(toYouTubeEmbedUrl).filter(Boolean);

  function patchGame(updates) {
    setGame((prev) => ({ ...prev, ...updates }));
    if (onGameUpdated) onGameUpdated({ ...game, ...updates });
  }

  function startEditing() {
    setEditState({ ...game, description: stripHtml(game.description), imageUrl: game.imageUrl ?? "" });
    setEditVideoUrls([...(game.howToPlayVideoUrls ?? [])]);
    setEditPdfUrl(game.rulesPdfUrl ?? "");
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

  function addVideoUrl() {
    const url = newVideoUrl.trim();
    if (!url) return;
    if (!editVideoUrls.includes(url)) setEditVideoUrls((prev) => [...prev, url]);
    setNewVideoUrl("");
  }

  function removeVideoUrl(url) {
    setEditVideoUrls((prev) => prev.filter((u) => u !== url));
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

  async function saveRulesOverrides() {
    setSavingRules(true);
    try {
      const resp = await MovieAPI.updateBoardgameRules(game.id, {
        rulesPdfUrl: editPdfUrl || undefined,
        howToPlayVideoUrls: editVideoUrls.length > 0 ? editVideoUrls : undefined,
      });
      if (!resp.ok) { message.error("Failed to save rules"); return; }
      const result = await resp.json();
      if (result.success) {
        message.success("Rules saved");
        patchGame({
          rulesPdfUrl: editPdfUrl || game.rulesPdfUrl,
          howToPlayVideoUrls: editVideoUrls.length > 0 ? editVideoUrls : game.howToPlayVideoUrls,
        });
      }
    } catch {
      message.error("Error saving rules");
    } finally {
      setSavingRules(false);
    }
  }

  async function discoverRules() {
    setDiscovering(true);
    try {
      const resp = await MovieAPI.discoverBoardgameRules(game.id);
      if (!resp.ok) { message.error("Discovery failed"); return; }
      const result = await resp.json();
      if (result.success) {
        const discovered = result.data.howToPlayVideoUrls ?? [];
        patchGame({
          rulesPdfCandidateUrl: result.data.pdfCandidateUrl,
          howToPlayVideoUrls: discovered,
        });
        setEditVideoUrls(discovered);
        message.success("Discovery complete");
      }
    } catch {
      message.error("Error during discovery");
    } finally {
      setDiscovering(false);
    }
  }

  async function approvePdf() {
    setApproving(true);
    try {
      const resp = await MovieAPI.approveBoardgameRulesPdf(game.id, overridePdfUrl || null);
      if (!resp.ok) { const b = await resp.json().catch(() => ({})); message.error(b.message || "Approval failed"); return; }
      const result = await resp.json();
      if (result.success) {
        patchGame({ rulesPdfUrl: result.data.rulesPdfUrl });
        setEditPdfUrl(result.data.rulesPdfUrl ?? "");
        message.success("PDF downloaded and saved");
      }
    } catch {
      message.error("Error approving PDF");
    } finally {
      setApproving(false);
    }
  }

  function confirmRematch() {
    const bggId = parseInt(rematchId, 10);
    if (!bggId || bggId <= 0) { message.error("Enter a valid BGG Thing ID"); return; }
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
          if (!response.ok) { const body = await response.json().catch(() => ({})); message.error(body.message || `Server error (${response.status})`); return; }
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
                {players && <><span className="modal-dot">·</span><span>👥 {players} players</span></>}
                {game.playingTime && <><span className="modal-dot">·</span><span>⏱ {game.playingTime} min</span></>}
              </div>
              <div className="boardgame-modal-stats-row">
                {game.averageRating ? <div className="boardgame-modal-stat"><span className="modal-label">BGG Rating</span><span className="boardgame-modal-stat-value">★ {Number(game.averageRating).toFixed(1)}/10</span></div> : null}
                {game.averageWeight ? <div className="boardgame-modal-stat"><span className="modal-label">Complexity</span><span className="boardgame-modal-stat-value">{Number(game.averageWeight).toFixed(2)}/5</span></div> : null}
                {game.minAge ? <div className="boardgame-modal-stat"><span className="modal-label">Min Age</span><span className="boardgame-modal-stat-value">{game.minAge}+</span></div> : null}
              </div>
              {description && <p className="boardgame-modal-plot">{description}</p>}

              {/* ── How to Play videos ── */}
              {embedUrls.map((url, i) => (
                <div key={url} className="rules-video-wrapper">
                  <iframe
                    className="rules-video-iframe"
                    src={url}
                    title={embedUrls.length > 1 ? `How to Play (${i + 1})` : "How to Play"}
                    allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
                    allowFullScreen
                  />
                </div>
              ))}

              {/* ── Rulebook PDF link ── */}
              {game.rulesPdfUrl && (
                <a
                  className="rules-pdf-link"
                  href={`/BoardgamePdf/${game.id}`}
                  target="_blank"
                  rel="noreferrer"
                >
                  📄 View Rulebook PDF
                </a>
              )}

              <a className="boardgame-bgg-link" href={`https://boardgamegeek.com/boardgame/${game.bggThingId}`} target="_blank" rel="noreferrer">
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

              {/* ── Rules Admin Panel ── */}
              <div className="rules-admin-panel">
                <div className="rules-admin-section-title">Rules &amp; Videos</div>

                <div className="rules-admin-row">
                  <Button onClick={discoverRules} loading={discovering}>Find Rules &amp; Videos</Button>
                  {game.rulesPdfCandidateUrl && (
                    <a href={game.rulesPdfCandidateUrl} target="_blank" rel="noreferrer" className="rules-candidate-link">
                      Review candidate PDF ↗
                    </a>
                  )}
                </div>

                <div className="edit-field">
                  <label className="edit-field-label">Override PDF URL (optional)</label>
                  <Input value={overridePdfUrl} onChange={(e) => setOverridePdfUrl(e.target.value)} placeholder={game.rulesPdfCandidateUrl ?? "Paste a PDF URL…"} />
                </div>
                <Button onClick={approvePdf} loading={approving} disabled={!game.rulesPdfCandidateUrl && !overridePdfUrl}>
                  Approve &amp; Download PDF
                </Button>
                {game.rulesPdfUrl && (
                  <div className="rules-confirmed-url">
                    ✓ PDF saved — <a href={`/BoardgamePdf/${game.id}`} target="_blank" rel="noreferrer">view</a>
                  </div>
                )}

                <div className="edit-field" style={{ marginTop: 12 }}>
                  <label className="edit-field-label">How to Play Videos</label>
                  {editVideoUrls.map((url) => (
                    <div key={url} className="rules-video-url-row">
                      <span className="rules-video-url-text">{url}</span>
                      <Button size="small" danger onClick={() => removeVideoUrl(url)}>Remove</Button>
                    </div>
                  ))}
                  <div className="rules-video-add-row">
                    <Input
                      value={newVideoUrl}
                      onChange={(e) => setNewVideoUrl(e.target.value)}
                      onPressEnter={addVideoUrl}
                      placeholder="YouTube URL…"
                    />
                    <Button onClick={addVideoUrl}>Add</Button>
                  </div>
                </div>

                <div className="rules-admin-row" style={{ marginTop: 8 }}>
                  <Button type="primary" onClick={saveRulesOverrides} loading={savingRules}>Save Rules &amp; Videos</Button>
                </div>
              </div>

              <div className="boardgame-rematch-section">
                <div className="boardgame-rematch-label">Wrong game? Re-match to a different BGG ID</div>
                <div className="boardgame-rematch-row">
                  <Input placeholder="BGG Thing ID" value={rematchId} onChange={(e) => setRematchId(e.target.value)} style={{ width: 160 }} type="number" />
                  <Button danger onClick={confirmRematch} loading={rematching}>Re-match</Button>
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
