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
    if (u.hostname === "youtu.be") return `https://www.youtube.com/embed${u.pathname}`;
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

function UrlRow({ url, actionLabel, actionDanger, onAction, loading }) {
  return (
    <div className="rules-url-row">
      <a href={url} target="_blank" rel="noreferrer" className="rules-url-text" title={url}>{url}</a>
      <Button size="small" danger={actionDanger} onClick={onAction} loading={loading}>{actionLabel}</Button>
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

  const [discovering, setDiscovering] = useState(false);
  const [approvingUrl, setApprovingUrl] = useState(null);
  const [removingSlot, setRemovingSlot] = useState(null);
  const [savingVideos, setSavingVideos] = useState(false);
  const [manualPdfUrl, setManualPdfUrl] = useState("");
  const [editVideoUrls, setEditVideoUrls] = useState([]);
  const [newVideoUrl, setNewVideoUrl] = useState("");

  useEffect(() => {
    const found = games.find((g) => g.id === gameId);
    setGame(found ?? null);
  }, [gameId, games]);

  useEffect(() => {
    if (!open) {
      setEditing(false);
      setEditState({});
      setRematchId("");
      setManualPdfUrl("");
      setEditVideoUrls([]);
      setNewVideoUrl("");
    }
  }, [open]);

  if (!game) return null;

  const minP = game.minPlayers;
  const maxP = game.maxPlayers;
  const players = minP && maxP ? (minP === maxP ? `${minP}` : `${minP}–${maxP}`) : minP || maxP || null;
  const description = stripHtml(game.description);
  const videoUrls = game.howToPlayVideoUrls ?? [];
  const embedUrls = videoUrls.map(toYouTubeEmbedUrl).filter(Boolean);
  const approvedPdfs = game.rulesPdfUrls ?? [];
  const candidatePdfs = game.rulesPdfCandidateUrls ?? [];

  function patchGame(updates) {
    setGame((prev) => ({ ...prev, ...updates }));
    if (onGameUpdated) onGameUpdated({ ...game, ...updates });
  }

  function startEditing() {
    setEditState({ ...game, description: stripHtml(game.description), imageUrl: game.imageUrl ?? "" });
    setEditVideoUrls([...(game.howToPlayVideoUrls ?? [])]);
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

  async function saveVideos() {
    setSavingVideos(true);
    try {
      const resp = await MovieAPI.updateBoardgameRules(game.id, { howToPlayVideoUrls: editVideoUrls });
      if (!resp.ok) { message.error("Failed to save videos"); return; }
      const result = await resp.json();
      if (result.success) {
        message.success("Videos saved");
        patchGame({ howToPlayVideoUrls: editVideoUrls });
      }
    } catch {
      message.error("Error saving videos");
    } finally {
      setSavingVideos(false);
    }
  }

  async function discoverRules() {
    setDiscovering(true);
    try {
      const resp = await MovieAPI.discoverBoardgameRules(game.id);
      if (!resp.ok) { message.error("Discovery failed"); return; }
      const result = await resp.json();
      if (result.success) {
        const candidates = result.data.rulesPdfCandidateUrls ?? [];
        const videos = result.data.howToPlayVideoUrls ?? [];
        patchGame({ rulesPdfCandidateUrls: candidates, howToPlayVideoUrls: videos });
        setEditVideoUrls(videos);
        message.success("Discovery complete");
      }
    } catch {
      message.error("Error during discovery");
    } finally {
      setDiscovering(false);
    }
  }

  async function approvePdf(url) {
    setApprovingUrl(url);
    try {
      const resp = await MovieAPI.approveBoardgameRulesPdf(game.id, url);
      if (!resp.ok) { const b = await resp.json().catch(() => ({})); message.error(b.message || "Approval failed"); return; }
      const result = await resp.json();
      if (result.success) {
        patchGame({ rulesPdfUrls: result.data.rulesPdfUrls, rulesPdfCandidateUrls: result.data.rulesPdfCandidateUrls });
        message.success("PDF downloaded and saved");
      }
    } catch {
      message.error("Error approving PDF");
    } finally {
      setApprovingUrl(null);
      setManualPdfUrl("");
    }
  }

  async function removePdf(slot) {
    setRemovingSlot(slot);
    try {
      const resp = await MovieAPI.removeBoardgameRulesPdf(game.id, slot);
      if (!resp.ok) { message.error("Remove failed"); return; }
      const result = await resp.json();
      if (result.success) {
        patchGame({ rulesPdfUrls: result.data.rulesPdfUrls });
        message.success("PDF removed");
      }
    } catch {
      message.error("Error removing PDF");
    } finally {
      setRemovingSlot(null);
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

              {approvedPdfs.length > 0 && (
                <div className="rules-pdf-links">
                  {approvedPdfs.map((_, slot) => (
                    <a
                      key={slot}
                      className="rules-pdf-link"
                      href={`/BoardgamePdf/${game.id}/${slot}`}
                      target="_blank"
                      rel="noreferrer"
                    >
                      📄 {approvedPdfs.length > 1 ? `Rulebook PDF ${slot + 1}` : "Rulebook PDF"}
                    </a>
                  ))}
                </div>
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

                <Button onClick={discoverRules} loading={discovering}>Find Rules &amp; Videos</Button>

                {/* PDF Candidates */}
                <div className="edit-field">
                  <label className="edit-field-label">PDF Candidates</label>
                  {candidatePdfs.length === 0 && <span className="rules-empty-hint">None found yet — run discovery or paste a URL below</span>}
                  {candidatePdfs.map((url) => (
                    <UrlRow
                      key={url}
                      url={url}
                      actionLabel="Approve"
                      actionDanger={false}
                      loading={approvingUrl === url}
                      onAction={() => approvePdf(url)}
                    />
                  ))}
                  <div className="rules-url-add-row">
                    <Input
                      value={manualPdfUrl}
                      onChange={(e) => setManualPdfUrl(e.target.value)}
                      onPressEnter={() => manualPdfUrl.trim() && approvePdf(manualPdfUrl.trim())}
                      placeholder="Paste a PDF URL to approve directly…"
                    />
                    <Button
                      onClick={() => manualPdfUrl.trim() && approvePdf(manualPdfUrl.trim())}
                      loading={approvingUrl === manualPdfUrl.trim()}
                      disabled={!manualPdfUrl.trim()}
                    >
                      Approve
                    </Button>
                  </div>
                </div>

                {/* Approved PDFs */}
                {approvedPdfs.length > 0 && (
                  <div className="edit-field">
                    <label className="edit-field-label">Approved PDFs</label>
                    {approvedPdfs.map((url, slot) => (
                      <UrlRow
                        key={slot}
                        url={`/BoardgamePdf/${game.id}/${slot}`}
                        actionLabel="Remove"
                        actionDanger={true}
                        loading={removingSlot === slot}
                        onAction={() => removePdf(slot)}
                      />
                    ))}
                  </div>
                )}

                {/* How to Play Videos */}
                <div className="edit-field">
                  <label className="edit-field-label">How to Play Videos</label>
                  {editVideoUrls.length === 0 && <span className="rules-empty-hint">None added yet</span>}
                  {editVideoUrls.map((url) => (
                    <UrlRow
                      key={url}
                      url={url}
                      actionLabel="Remove"
                      actionDanger={true}
                      onAction={() => setEditVideoUrls((prev) => prev.filter((u) => u !== url))}
                    />
                  ))}
                  <div className="rules-url-add-row">
                    <Input
                      value={newVideoUrl}
                      onChange={(e) => setNewVideoUrl(e.target.value)}
                      onPressEnter={addVideoUrl}
                      placeholder="YouTube URL…"
                    />
                    <Button onClick={addVideoUrl}>Add</Button>
                  </div>
                </div>

                <Button type="primary" onClick={saveVideos} loading={savingVideos}>Save Videos</Button>
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
