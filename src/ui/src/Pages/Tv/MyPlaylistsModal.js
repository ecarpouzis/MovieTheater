import { useCallback, useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import Modal from "antd/es/modal";
import Popconfirm from "antd/es/popconfirm";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import PlaylistManageModal from "./PlaylistManageModal";
import PlaylistPickerModal from "./PlaylistPickerModal";
import "./MyPlaylistsModal.css";
import "../../Components/SheetModal.css";
import { SHEET_Z } from "../../Components/sheetModal";

// A 2×2 poster collage tile for a playlist (falls back to a tinted initial when empty).
function Collage({ posters, name }) {
  const cells = (posters || []).slice(0, 4);
  if (cells.length === 0) {
    return <div className="mypl-collage mypl-collage--empty"><span>{(name || "?").charAt(0)}</span></div>;
  }
  return (
    <div className={`mypl-collage mypl-collage--n${cells.length}`}>
      {cells.map((p, i) => (
        <img key={i} src={MovieAPI.getPosterThumbnail(p.posterId, p.posterVersion, p.kind)} alt="" loading="lazy" decoding="async" />
      ))}
    </div>
  );
}

/**
 * "My Playlists" as a modal (was an on-page shelf). Opened from the sidebar Playlists pill (Movies
 * only, streaming accounts). Each playlist is a user-owned channel; opening one tunes the TV player
 * and closes the modal. Self-contained: it owns its own load + the create picker + the manage modal,
 * so it no longer needs to be wired through Browse.
 */
export default function MyPlaylistsModal({ open, onClose, userData }) {
  const history = useHistory();
  const [playlists, setPlaylists] = useState(null);
  const [manageId, setManageId] = useState(null);
  const [pickerOpen, setPickerOpen] = useState(false);

  const load = useCallback(() => {
    MovieAPI.getMyPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]));
  }, []);

  useEffect(() => {
    if (open && userData?.hasPassword) load();
  }, [open, userData?.hasPassword, load]);

  const remove = async (id, name) => {
    try {
      const r = await MovieAPI.deletePlaylist(id);
      if (!r.ok) throw new Error();
      message.success(`Deleted “${name}”.`);
      setPlaylists((prev) => (prev || []).filter((p) => p.id !== id));
    } catch {
      message.error("Couldn't delete that playlist.");
    }
  };

  const openPlaylist = (p) => {
    onClose();
    if (p.watchpartyToken) history.push(`/watch-together/${p.watchpartyToken}`);
    else history.push(`/tv/${p.id}`);
  };

  if (!userData?.hasPassword) return null;
  const list = playlists || [];

  return (
    <>
      <Modal open={open} onCancel={onClose} footer={null} width={640} title="My Playlists" destroyOnHidden wrapClassName="sheet-modal" zIndex={SHEET_Z}>
        <div className="mypl-grid">
          {list.map((p) => (
            <div className="mypl-card" key={p.id}>
              <button className="mypl-open" onClick={() => openPlaylist(p)} title={p.watchpartyToken ? `Rejoin ${p.name}` : `Watch ${p.name}`}>
                <Collage posters={p.posters} name={p.name} />
                {p.watchpartyToken && <span className="mypl-badge">WATCH PARTY</span>}
              </button>
              <div className="mypl-meta">
                <div className="mypl-name" title={p.name}>{p.name}</div>
                <div className="mypl-sub">{p.count} {p.count === 1 ? "title" : "titles"}</div>
              </div>
              <div className="mypl-actions">
                {!p.watchpartyToken && (
                  <button className="mypl-act" title="Manage" onClick={() => setManageId(p.id)}>⚙</button>
                )}
                <Popconfirm
                  title={`Delete “${p.name}”?`}
                  okText="Delete"
                  okButtonProps={{ danger: true }}
                  onConfirm={() => remove(p.id, p.name)}
                >
                  <button className="mypl-act mypl-act--del" title="Delete">✕</button>
                </Popconfirm>
              </div>
            </div>
          ))}
          <button className="mypl-card mypl-new" onClick={() => setPickerOpen(true)} title="New playlist">
            <div className="mypl-collage mypl-collage--new"><span>＋</span></div>
            <div className="mypl-meta"><div className="mypl-name">New playlist</div></div>
          </button>
        </div>
      </Modal>

      {manageId != null && (
        <PlaylistManageModal
          playlistId={manageId}
          open={manageId != null}
          onClose={() => setManageId(null)}
          onChanged={load}
        />
      )}

      <PlaylistPickerModal
        open={pickerOpen}
        items={[]}
        defaultName="My playlist"
        onClose={() => setPickerOpen(false)}
        onDone={load}
      />
    </>
  );
}
