import { useEffect, useState } from "react";
import Modal from "antd/es/modal";
import Input from "antd/es/input";
import Button from "antd/es/button";
import Spin from "antd/es/spin";
import message from "antd/es/message";
import Popconfirm from "antd/es/popconfirm";
import Select from "antd/es/select";
import {
  DndContext,
  closestCenter,
  KeyboardSensor,
  PointerSensor,
  TouchSensor,
  useSensor,
  useSensors,
} from "@dnd-kit/core";
import {
  SortableContext,
  verticalListSortingStrategy,
  arrayMove,
  sortableKeyboardCoordinates,
  useSortable,
} from "@dnd-kit/sortable";
import { restrictToVerticalAxis, restrictToParentElement } from "@dnd-kit/modifiers";
import { CSS } from "@dnd-kit/utilities";
import { MovieAPI } from "../../MovieAPI";
import "./MusicPlaylists.css";
import "../../Components/SheetModal.css";

/**
 * Edit a music playlist: rename, drag-reorder, remove tracks, delete the whole thing.
 * Saves the ordered lineup in one SetItems call (music-plan.md Phase 3) — positions are rewritten
 * server-side, so the client only has to send ids in the order it shows them.
 */

function SortableTrackRow({ item, index, onRemove }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: item.key });
  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    zIndex: isDragging ? 2 : undefined,
    opacity: isDragging ? 0.9 : 1,
  };
  return (
    <li ref={setNodeRef} style={style} className={`mplman-row${isDragging ? " mplman-row--dragging" : ""}`}>
      {/* Only the handle starts a drag, so ✕ stays clickable. */}
      <span className="mplman-handle" {...attributes} {...listeners} title="Drag to reorder">⠿</span>
      <span className="mplman-no">{index + 1}</span>
      <span className="mplman-title" title={item.title}>{item.title}</span>
      <span className="mplman-meta">{item.artistName}</span>
      <button className="mplman-del" onClick={() => onRemove(item.key)} title="Remove" aria-label="Remove track">✕</button>
    </li>
  );
}

export default function MusicPlaylistManageModal({ playlistId, open, onClose, onChanged }) {
  const [loading, setLoading] = useState(true);
  const [name, setName] = useState("");
  const [origName, setOrigName] = useState("");
  const [items, setItems] = useState([]);
  const [busy, setBusy] = useState(false);
  // The Favorites list is the same table with a flag, so it reorders and empties like any other —
  // but it can't be renamed, shared or deleted (the server refuses all three). Those controls are
  // dropped rather than shown-and-rejected.
  const [isFavorites, setIsFavorites] = useState(false);
  // Sharing (music-plan.md §2.4). `access` carries who owns it and who it's shared with; a member
  // sees the roster but only the owner gets the add/remove controls.
  const [access, setAccess] = useState(null);
  const [targets, setTargets] = useState([]);
  const [pending, setPending] = useState([]);

  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );

  useEffect(() => {
    if (!open || playlistId == null) return;
    setLoading(true);
    MovieAPI.getMusicPlaylistItems(playlistId)
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => {
        if (!data) throw new Error();
        setName(data.name || "");
        setOrigName(data.name || "");
        setIsFavorites(!!data.isFavorites);
        // A track can legitimately appear twice in a playlist, so the sortable key is
        // id+ordinal, not the bare track id (dnd-kit needs unique ids).
        setItems((data.items || []).map((it, i) => ({ ...it, key: `${it.id}-${i}` })));
      })
      .catch(() => message.error("Couldn't load that playlist."))
      .finally(() => setLoading(false));
  }, [open, playlistId]);

  const onDragEnd = ({ active, over }) => {
    if (!over || active.id === over.id) return;
    setItems((prev) => {
      const from = prev.findIndex((it) => it.key === active.id);
      const to = prev.findIndex((it) => it.key === over.id);
      if (from < 0 || to < 0) return prev;
      return arrayMove(prev, from, to);
    });
  };

  const removeKey = (key) => setItems((prev) => prev.filter((it) => it.key !== key));

  const save = async () => {
    setBusy(true);
    try {
      const trimmed = (name || "").trim();
      if (!isFavorites && trimmed && trimmed !== origName) {
        const rr = await MovieAPI.renameMusicPlaylist(playlistId, trimmed);
        if (!rr.ok) throw new Error();
      }
      const r = await MovieAPI.setMusicPlaylistItems(playlistId, items.map((it) => it.id));
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

  const remove = async () => {
    setBusy(true);
    try {
      const r = await MovieAPI.deleteMusicPlaylist(playlistId);
      if (!r.ok) throw new Error();
      message.success("Playlist deleted.");
      onChanged && onChanged();
      onClose && onClose();
    } catch {
      message.error("Couldn't delete the playlist.");
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    if (!open || playlistId == null) { setAccess(null); setPending([]); return; }
    MovieAPI.getMusicPlaylistShares(playlistId)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then(setAccess)
      .catch(() => setAccess(null));
    MovieAPI.getMusicShareTargets()
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => setTargets(list || []))
      .catch(() => setTargets([]));
  }, [open, playlistId]);

  function applyShare(userIds) {
    setBusy(true);
    MovieAPI.shareMusicPlaylist(playlistId, userIds)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then(() => MovieAPI.getMusicPlaylistShares(playlistId).then((r) => r.json()).then(setAccess))
      .then(() => { setPending([]); onChanged && onChanged(); })
      .catch(() => message.error("Couldn't share that playlist."))
      .finally(() => setBusy(false));
  }

  function revoke(userId) {
    setBusy(true);
    MovieAPI.unshareMusicPlaylist(playlistId, [userId])
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then(() => MovieAPI.getMusicPlaylistShares(playlistId).then((r) => r.json()).then(setAccess))
      .then(() => onChanged && onChanged())
      .catch(() => message.error("Couldn't change access."))
      .finally(() => setBusy(false));
  }

  const isOwner = access?.isOwner !== false;
  const sharedIds = new Set((access?.shares || []).map((sh) => sh.userId));
  const addable = targets.filter((t) => !sharedIds.has(t.id));

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={560}
      title={isFavorites ? "Favorites" : "Edit playlist"}
      destroyOnHidden
      wrapClassName="sheet-modal"
    >
      {loading ? (
        <div className="mplman-loading"><Spin /></div>
      ) : (
        <div className="mplman" data-testid="music-playlist-manage">
          {isFavorites ? (
            <div className="mplman-fixed-name">
              ♥ Favorites <span>· only you can see or change this list</span>
            </div>
          ) : (
            <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Playlist name" />
          )}

          {items.length === 0 ? (
            <div className="mplman-empty">
              {isFavorites
                ? "Nothing favorited yet. Hit the ♥ in the player while a song is playing."
                : "This playlist is empty. Add tracks from an album or a song row."}
            </div>
          ) : (
            <DndContext
              sensors={sensors}
              collisionDetection={closestCenter}
              modifiers={[restrictToVerticalAxis, restrictToParentElement]}
              onDragEnd={onDragEnd}
            >
              <SortableContext items={items.map((it) => it.key)} strategy={verticalListSortingStrategy}>
                <ol className="mplman-list">
                  {items.map((it, i) => (
                    <SortableTrackRow key={it.key} item={it} index={i} onRemove={removeKey} />
                  ))}
                </ol>
              </SortableContext>
            </DndContext>
          )}

          {access && !isFavorites && (
            <div className="mplman-share">
              <div className="mplman-share-head">
                Shared with
                {!isOwner && access.ownerName ? ` · owned by ${access.ownerName}` : ""}
              </div>
              {(access.shares || []).length === 0 && (
                <div className="mplman-share-empty">Nobody yet — this playlist is just yours.</div>
              )}
              <div className="mplman-share-list">
                {(access.shares || []).map((sh) => (
                  <span className="mplman-share-chip" key={sh.userId}>
                    {sh.username}
                    {isOwner && (
                      <button className="mplman-share-x" disabled={busy}
                              onClick={() => revoke(sh.userId)} aria-label={`Remove ${sh.username}`}>✕</button>
                    )}
                  </span>
                ))}
              </div>
              {isOwner ? (
                <div className="mplman-share-add">
                  <Select
                    mode="multiple" allowClear style={{ flex: "1 1 auto", minWidth: 0 }}
                    placeholder="Add people…" value={pending} onChange={setPending}
                    options={addable.map((t) => ({ value: t.id, label: t.username }))}
                  />
                  <Button disabled={busy || pending.length === 0} onClick={() => applyShare(pending)}>Share</Button>
                </div>
              ) : (
                <div className="mplman-share-add">
                  {/* A member's own exit. Leaving is theirs to do; removing anyone ELSE is the owner's. */}
                  <Button danger disabled={busy} onClick={() => { revoke(access.meId ?? -1); onClose(); }}>
                    Leave this playlist
                  </Button>
                </div>
              )}
            </div>
          )}

          <div className="mplman-actions">
            {isOwner && !isFavorites && (
              <Popconfirm title="Delete this playlist?" okText="Delete" cancelText="Keep" onConfirm={remove}>
                <Button danger disabled={busy}>Delete</Button>
              </Popconfirm>
            )}
            <span className="mplman-spacer" />
            <Button onClick={onClose} disabled={busy}>Cancel</Button>
            <Button type="primary" loading={busy} onClick={save}>Save</Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
