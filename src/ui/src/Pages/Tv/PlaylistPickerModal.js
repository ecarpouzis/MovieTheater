import { useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import Modal from "antd/es/modal";
import Input from "antd/es/input";
import Radio from "antd/es/radio";
import Checkbox from "antd/es/checkbox";
import Button from "antd/es/button";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import "./PlaylistPickerModal.css";

/**
 * Add one or more titles to a playlist — the shared "＋ Add to playlist" surface (movie modal, a season of
 * a show, a bulk selection). You can drop the titles into an existing playlist or spin up a new one, and a
 * checkbox turns the new one into a watch party (which then opens its shareable lobby). See
 * docs/playlists-watchparty-plan.md.
 *
 * Props:
 *   open        – whether the modal is shown
 *   items       – [{ playableId, title }] to add (may be empty to create an empty playlist)
 *   defaultName – suggested name for a new playlist
 *   onClose     – close without changes
 *   onDone      – called after a successful create/add (so a shelf can refresh)
 */
export default function PlaylistPickerModal({ open, items = [], defaultName = "", onClose, onDone }) {
  const history = useHistory();
  const [playlists, setPlaylists] = useState(null); // existing plain playlists to add to
  const [target, setTarget] = useState("new"); // "new" | playlist id
  const [name, setName] = useState(defaultName);
  const [watchparty, setWatchparty] = useState(false);
  const [busy, setBusy] = useState(false);

  const playableIds = items.map((i) => i.playableId).filter((x) => x != null);

  useEffect(() => {
    if (!open) return;
    setName(defaultName || (items.length === 1 ? items[0].title || "" : ""));
    setTarget("new");
    setWatchparty(false);
    setPlaylists(null);
    MovieAPI.getMyPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists((list || []).filter((p) => !p.watchpartyToken)))
      .catch(() => setPlaylists([]));
  }, [open, defaultName]); // eslint-disable-line react-hooks/exhaustive-deps

  const submit = async () => {
    setBusy(true);
    try {
      if (target !== "new") {
        const r = await MovieAPI.addPlaylistItems(target, playableIds);
        if (!r.ok) throw new Error();
        const pl = playlists.find((p) => String(p.id) === String(target));
        message.success(`Added ${playableIds.length} to “${pl?.name || "playlist"}”.`);
        onDone && onDone();
        onClose && onClose();
        return;
      }

      const finalName = (name || "").trim() || (watchparty ? "Watch party" : "My playlist");
      const r = await MovieAPI.createPlaylist(finalName, playableIds, watchparty);
      if (!r.ok) throw new Error();
      const result = await r.json();
      if (watchparty && result.watchpartyToken) {
        onDone && onDone();
        onClose && onClose();
        history.push(`/watch-together/${result.watchpartyToken}`);
        return;
      }
      message.success(`Created “${finalName}”.`);
      onDone && onDone();
      onClose && onClose();
    } catch {
      message.error("Sorry — that didn't work. Try again.");
    } finally {
      setBusy(false);
    }
  };

  const count = playableIds.length;

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={460}
      title={count > 0 ? `Add ${count} ${count === 1 ? "title" : "titles"} to a playlist` : "New playlist"}
      destroyOnHidden
    >
      <div className="plpick">
        <Radio.Group
          className="plpick-targets"
          value={target}
          onChange={(e) => setTarget(e.target.value)}
        >
          <Radio value="new">New playlist…</Radio>
          {(playlists || []).map((p) => (
            <Radio key={p.id} value={p.id}>
              {p.name} <span className="plpick-count">· {p.count}</span>
            </Radio>
          ))}
        </Radio.Group>

        {target === "new" && (
          <div className="plpick-new">
            <Input
              placeholder="Playlist name"
              value={name}
              maxLength={64}
              onChange={(e) => setName(e.target.value)}
              onPressEnter={submit}
              autoFocus
            />
            {count > 0 && (
              <Checkbox
                className="plpick-wp"
                checked={watchparty}
                onChange={(e) => setWatchparty(e.target.checked)}
              >
                Make this a <strong>watch party</strong> — everyone presses Begin together, with a shareable link.
              </Checkbox>
            )}
          </div>
        )}

        <div className="plpick-actions">
          <Button onClick={onClose} disabled={busy}>Cancel</Button>
          <Button type="primary" loading={busy} onClick={submit}>
            {target === "new" ? (watchparty ? "Create & open party" : "Create playlist") : "Add"}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
