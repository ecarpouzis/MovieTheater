import { useEffect, useState } from "react";
import Modal from "antd/es/modal";
import Input from "antd/es/input";
import Button from "antd/es/button";
import Spin from "antd/es/spin";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import "./PlaylistManageModal.css";
import "../../Components/SheetModal.css";

/**
 * Edit a playlist: rename it, reorder its titles, and drop ones you no longer want. Saves the whole ordered
 * lineup at once (SetItems). See docs/playlists-watchparty-plan.md.
 */
export default function PlaylistManageModal({ playlistId, open, onClose, onChanged }) {
  const [loading, setLoading] = useState(true);
  const [name, setName] = useState("");
  const [origName, setOrigName] = useState("");
  const [items, setItems] = useState([]); // [{ playableId, title, posterId, kind, posterVersion }]
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    setLoading(true);
    MovieAPI.getPlaylistItems(playlistId)
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => {
        if (!data) throw new Error();
        setName(data.name || "");
        setOrigName(data.name || "");
        setItems(data.items || []);
      })
      .catch(() => message.error("Couldn't load that playlist."))
      .finally(() => setLoading(false));
  }, [open, playlistId]);

  const move = (i, delta) => {
    setItems((prev) => {
      const next = [...prev];
      const j = i + delta;
      if (j < 0 || j >= next.length) return prev;
      [next[i], next[j]] = [next[j], next[i]];
      return next;
    });
  };

  const removeAt = (i) => setItems((prev) => prev.filter((_, idx) => idx !== i));

  const save = async () => {
    setBusy(true);
    try {
      const trimmed = (name || "").trim();
      if (trimmed && trimmed !== origName) {
        const rr = await MovieAPI.renamePlaylist(playlistId, trimmed);
        if (!rr.ok) throw new Error();
      }
      const r = await MovieAPI.setPlaylistItems(playlistId, items.map((it) => it.playableId));
      if (!r.ok) throw new Error();
      message.success("Playlist saved.");
      onChanged && onChanged();
      onClose && onClose();
    } catch {
      message.error("Couldn't save the playlist.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={520} title="Edit playlist" destroyOnHidden wrapClassName="sheet-modal">
      {loading ? (
        <div className="plman-loading"><Spin /></div>
      ) : (
        <div className="plman">
          <Input
            value={name}
            maxLength={64}
            onChange={(e) => setName(e.target.value)}
            placeholder="Playlist name"
          />

          {items.length === 0 ? (
            <div className="plman-empty">This playlist is empty. Add titles from a movie or show.</div>
          ) : (
            <ol className="plman-list">
              {items.map((it, i) => (
                <li className="plman-row" key={`${it.playableId}-${i}`}>
                  <img
                    className="plman-poster"
                    src={MovieAPI.getPosterThumbnail(it.posterId, it.posterVersion, it.kind)}
                    alt=""
                    loading="lazy"
                  />
                  <span className="plman-title" title={it.title}>{it.title}</span>
                  <span className="plman-ctrls">
                    <button disabled={i === 0} onClick={() => move(i, -1)} title="Move up">▲</button>
                    <button disabled={i === items.length - 1} onClick={() => move(i, 1)} title="Move down">▼</button>
                    <button className="plman-del" onClick={() => removeAt(i)} title="Remove">✕</button>
                  </span>
                </li>
              ))}
            </ol>
          )}

          <div className="plman-actions">
            <Button onClick={onClose} disabled={busy}>Cancel</Button>
            <Button type="primary" loading={busy} onClick={save}>Save</Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
