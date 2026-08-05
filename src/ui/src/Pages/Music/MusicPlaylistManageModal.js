import { useEffect, useState } from "react";
import Modal from "antd/es/modal";
import Input from "antd/es/input";
import Button from "antd/es/button";
import Spin from "antd/es/spin";
import message from "antd/es/message";
import Popconfirm from "antd/es/popconfirm";
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
      if (trimmed && trimmed !== origName) {
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

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={560} title="Edit playlist" destroyOnHidden>
      {loading ? (
        <div className="mplman-loading"><Spin /></div>
      ) : (
        <div className="mplman" data-testid="music-playlist-manage">
          <Input value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Playlist name" />

          {items.length === 0 ? (
            <div className="mplman-empty">This playlist is empty. Add tracks from an album or a song row.</div>
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

          <div className="mplman-actions">
            <Popconfirm title="Delete this playlist?" okText="Delete" cancelText="Keep" onConfirm={remove}>
              <Button danger disabled={busy}>Delete</Button>
            </Popconfirm>
            <span className="mplman-spacer" />
            <Button onClick={onClose} disabled={busy}>Cancel</Button>
            <Button type="primary" loading={busy} onClick={save}>Save</Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
