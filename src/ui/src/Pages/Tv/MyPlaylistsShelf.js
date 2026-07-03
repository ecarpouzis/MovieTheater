import { useCallback, useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import Popconfirm from "antd/es/popconfirm";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import PlaylistManageModal from "./PlaylistManageModal";
import "./MyPlaylistsShelf.css";

// A 2×2 poster collage tile for a playlist (falls back to a tinted initial when a playlist is empty).
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
 * The viewer's private "My Playlists" shelf on the home page (docs/playlists-watchparty-plan.md). Each
 * playlist is a user-owned channel; tapping it tunes the TV player, and it can be managed or deleted right
 * here. A watch party (transient) shows a badge and rejoins its lobby instead. Streaming-gated like the
 * "Now Playing" rail.
 */
export default function MyPlaylistsShelf({ userData, refreshKey, onNew }) {
  const history = useHistory();
  const [playlists, setPlaylists] = useState(null);
  const [manageId, setManageId] = useState(null);

  const load = useCallback(() => {
    MovieAPI.getMyPlaylists()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]));
  }, []);

  useEffect(() => {
    if (!userData?.hasPassword) return;
    load();
  }, [userData?.hasPassword, refreshKey, load]);

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

  const open = (p) => {
    if (p.watchpartyToken) history.push(`/watch-together/${p.watchpartyToken}`);
    else history.push(`/tv/${p.id}`);
  };

  if (!userData?.hasPassword) return null;
  // Render even when empty so the "＋ New playlist" tile is a discoverable entry point.
  const list = playlists || [];

  return (
    <div className="mypl">
      <div className="mypl-head">
        <span className="mypl-title">My Playlists</span>
      </div>
      <div className="mypl-rail">
        {list.map((p) => (
          <div className="mypl-card" key={p.id}>
            <button className="mypl-open" onClick={() => open(p)} title={p.watchpartyToken ? `Rejoin ${p.name}` : `Watch ${p.name}`}>
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
        <button className="mypl-card mypl-new" onClick={() => onNew && onNew()} title="New playlist">
          <div className="mypl-collage mypl-collage--new"><span>＋</span></div>
          <div className="mypl-meta"><div className="mypl-name">New playlist</div></div>
        </button>
      </div>

      {manageId != null && (
        <PlaylistManageModal
          playlistId={manageId}
          open={manageId != null}
          onClose={() => setManageId(null)}
          onChanged={load}
        />
      )}
    </div>
  );
}
