import { useEffect, useState } from "react";
import { Input, Modal, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoPersonPicker from "./PhotoPersonPicker";

// Batch actions for selection mode (docs/photos-plan.md §2.9: albums are "created/edited by any
// family member from selection mode in any view", and curation flags are confirmed batch-wise).
//
// Everything this bar does is a row or a flag. Hiding takes photos out of the timeline and out of
// albums and leaves them in the folder view; nothing here deletes, moves or renames a file, and there
// is no action in this vertical that can.

export default function PhotoSelectionBar({ ids, onChanged, onClear, people = [], onReloadPeople }) {
  const [busy, setBusy] = useState(false);
  const [albumOpen, setAlbumOpen] = useState(false);
  const [tagOpen, setTagOpen] = useState(false);

  if (!ids.length) return null;

  const setHidden = async (hidden) => {
    setBusy(true);
    try {
      const response = await MovieAPI.setPhotosHidden(ids, hidden);
      if (!response.ok) {
        message.error("Could not update those photos.");
        return;
      }
      const body = await response.json();
      message.success(`${body.changed} ${hidden ? "hidden" : "unhidden"}.`);
      onChanged?.();
    } catch {
      message.error("Could not update those photos.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="photo-selection-bar">
      <span className="photo-selection-count">{ids.length} selected</span>
      <button type="button" className="photos-button" disabled={busy} onClick={() => setHidden(true)}>
        Hide
      </button>
      <button type="button" className="photos-button" disabled={busy} onClick={() => setHidden(false)}>
        Unhide
      </button>
      <button type="button" className="photos-button" disabled={busy} onClick={() => setAlbumOpen(true)}>
        Add to album
      </button>
      <button type="button" className="photos-button" disabled={busy} onClick={() => setTagOpen(true)}>
        Tag someone
      </button>
      <button type="button" className="photos-button" onClick={onClear}>
        Clear
      </button>

      <BatchTagModal
        open={tagOpen}
        ids={ids}
        people={people}
        onReloadPeople={onReloadPeople}
        onClose={() => setTagOpen(false)}
        onDone={() => {
          setTagOpen(false);
          onChanged?.();
        }}
      />

      <AddToAlbumModal
        open={albumOpen}
        ids={ids}
        onClose={() => setAlbumOpen(false)}
        onDone={() => {
          setAlbumOpen(false);
          onChanged?.();
        }}
      />
    </div>
  );
}

/**
 * Tag a whole selection with one person (docs/photos-plan.md §2.8).
 *
 * The write goes through the same endpoint the lightbox uses, so it obeys the same rule: every id is
 * redirected to its duplicate group's MASTER (§2.6). A selection that happened to include two copies
 * of one photograph makes one tag, not one tag plus an invisible row — and the count of redirects is
 * REPORTED, because "I picked six and got four" needs a reason attached to it.
 */
export function BatchTagModal({ open, ids, people, onReloadPeople, onClose, onDone }) {
  const [busy, setBusy] = useState(false);

  const tag = async (pick) => {
    setBusy(true);
    try {
      const response = await MovieAPI.addPhotoTags({
        assetIds: ids,
        familyPersonId: pick.familyPersonId,
        name: pick.name,
      });
      if (!response.ok) {
        message.error("Could not tag those photos.");
        return;
      }
      const body = await response.json();
      message.success(
        `Tagged ${body.added + body.promoted} with ${body.person.name}.` +
          (body.unchanged ? ` ${body.unchanged} already had it.` : "") +
          (body.redirectedToMasters
            ? ` ${body.redirectedToMasters} were duplicate copies — the tag went on the copy the album shows.`
            : "")
      );
      if (!pick.familyPersonId) onReloadPeople?.();
      onDone?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal className="photos-modal" open={open} onCancel={onClose} footer={null} title={`Tag ${ids.length} photo(s)`} destroyOnHidden>
      <div className="photo-album-picker">
        <p className="photos-note">
          Type a name. Anyone new is created for you, and the tag lands on every duplicate copy of each
          photo at once.
        </p>
        <PhotoPersonPicker people={people} onPick={tag} disabled={busy} autoFocus />
      </div>
    </Modal>
  );
}

/** Pick an existing album or make one from the selection. Both land as PhotoAlbumEntry rows; the
 *  slug is minted server-side, so nothing here has to invent a URL. */
export function AddToAlbumModal({ open, ids, onClose, onDone }) {
  const [albums, setAlbums] = useState([]);
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    setTitle("");
    MovieAPI.getPhotoAlbums()
      .then((r) => (r.ok ? r.json() : { albums: [] }))
      .then((body) => setAlbums(body.albums || []))
      .catch(() => setAlbums([]));
  }, [open]);

  const addTo = async (album) => {
    setBusy(true);
    try {
      const response = await MovieAPI.addToPhotoAlbum(album.id, { assetIds: ids });
      if (!response.ok) {
        message.error("Could not add to that album.");
        return;
      }
      const body = await response.json();
      // §2.6: adding a duplicate adds the copy the album will actually show. Said out loud, because
      // "I picked six and got four" needs a reason attached to it.
      message.success(
        `Added ${body.added} to ${album.title}.` +
          (body.redirectedToMasters ? ` ${body.redirectedToMasters} were duplicates of photos already added.` : "")
      );
      onDone?.();
    } finally {
      setBusy(false);
    }
  };

  const create = async () => {
    if (!title.trim()) return;
    setBusy(true);
    try {
      const response = await MovieAPI.createPhotoAlbum({ title: title.trim(), assetIds: ids });
      if (!response.ok) {
        message.error("Could not create that album.");
        return;
      }
      const body = await response.json();
      message.success(
        `Created ${body.album.title} with ${body.added}.` +
          (body.redirectedToMasters ? ` ${body.redirectedToMasters} were duplicates of photos already added.` : "")
      );
      onDone?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal className="photos-modal" open={open} onCancel={onClose} footer={null} title="Add to an album" destroyOnHidden>
      <div className="photo-album-picker">
        <div className="photo-album-new">
          <Input
            placeholder="New album title"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            onPressEnter={create}
            disabled={busy}
          />
          <button type="button" className="photos-button" disabled={busy || !title.trim()} onClick={create}>
            Create
          </button>
        </div>

        {albums.length > 0 && (
          <ul className="photo-album-list">
            {albums.map((album) => (
              <li key={album.id}>
                <button type="button" className="photo-album-choice" disabled={busy} onClick={() => addTo(album)}>
                  <span>{album.title}</span>
                  <span className="photo-album-count">{album.count}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  );
}
