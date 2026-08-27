import { useEffect, useState } from "react";
import Modal from "antd/es/modal";
import Input from "antd/es/input";
import Radio from "antd/es/radio";
import Button from "antd/es/button";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import "./MusicPlaylists.css";
import "../../Components/SheetModal.css";
import { SHEET_STACK_Z } from "../../Components/sheetModal";

/**
 * Add tracks to a music playlist — the shared "＋ Playlist" surface (album modal, a song row, the
 * queue flyout). Drop them into an existing playlist or spin up a new one. Mirrors the TV
 * PlaylistPickerModal's UX; the storage underneath is the Music* tables (music-plan.md §2.2/Phase 3).
 *
 * Props:
 *   open        – whether the modal is shown
 *   tracks      – [{ id, title }] to add (may be empty to create an empty playlist)
 *   defaultName – suggested name for a new playlist
 *   onClose     – close without changes
 *   onDone      – called after a successful create/add (so a shelf can refresh)
 */
export default function MusicPlaylistPickerModal({ open, tracks = [], defaultName = "", onClose, onDone }) {
  const [playlists, setPlaylists] = useState(null);
  const [target, setTarget] = useState("new"); // "new" | playlist id
  const [name, setName] = useState(defaultName);
  const [busy, setBusy] = useState(false);

  const trackIds = (tracks || []).map((t) => t.id).filter((x) => x != null);

  useEffect(() => {
    if (!open) return;
    setName(defaultName || (tracks.length === 1 ? tracks[0].title || "" : ""));
    setTarget("new");
    setPlaylists(null);
    MovieAPI.getMyMusicPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]));
  }, [open, defaultName]); // eslint-disable-line react-hooks/exhaustive-deps

  const submit = async () => {
    setBusy(true);
    try {
      if (target !== "new") {
        const r = await MovieAPI.addMusicPlaylistItems(target, trackIds);
        if (!r.ok) throw new Error();
        const pl = (playlists || []).find((p) => String(p.id) === String(target));
        message.success(`Added ${trackIds.length} to “${pl?.name || "playlist"}”.`);
      } else {
        const finalName = (name || "").trim() || "My playlist";
        const r = await MovieAPI.createMusicPlaylist(finalName, trackIds);
        if (!r.ok) throw new Error();
        message.success(`Created “${finalName}”.`);
      }
      onDone && onDone();
      onClose && onClose();
    } catch {
      message.error("Sorry — that didn't work. Try again.");
    } finally {
      setBusy(false);
    }
  };

  const count = trackIds.length;

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={440}
      // Above the album sheet, because "＋ Playlist" opens this WITHOUT closing that — both are on
      // screen at once, and at antd's default 1000 this would have opened behind it and read as a
      // dead button. That is exactly what SHEET_STACK_Z is (Components/sheetModal.js).
      zIndex={SHEET_STACK_Z}
      title={count > 0 ? `Add ${count} ${count === 1 ? "track" : "tracks"} to a playlist` : "New playlist"}
      destroyOnHidden
      wrapClassName="sheet-modal"
    >
      <div className="mplpick" data-testid="music-playlist-picker">
        <Radio.Group className="mplpick-targets" value={target} onChange={(e) => setTarget(e.target.value)}>
          <Radio value="new">New playlist…</Radio>
          {(playlists || []).map((p) => (
            <Radio key={p.id} value={p.id}>
              {p.name} <span className="mplpick-count">· {p.count}</span>
            </Radio>
          ))}
        </Radio.Group>

        {target === "new" && (
          <div className="mplpick-new">
            <Input
              placeholder="Playlist name"
              value={name}
              maxLength={200}
              onChange={(e) => setName(e.target.value)}
              onPressEnter={submit}
              autoFocus
            />
          </div>
        )}

        <div className="mplpick-actions">
          <Button onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="primary" loading={busy} onClick={submit}>
            {target === "new" ? "Create playlist" : "Add"}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
