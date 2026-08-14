import { useEffect, useState } from "react";
import { createPortal } from "react-dom";
import { Input, Modal, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoPersonPicker from "./PhotoPersonPicker";

// Batch actions for selection mode (docs/photos-plan.md §2.9: albums are "created/edited by any
// family member from selection mode in any view", and curation flags are confirmed batch-wise).
//
// Everything this bar does is a row or a flag. Hiding takes photos out of the timeline and out of
// albums and leaves them in the folder view; nothing here deletes, moves or renames a file, and there
// is no action in this vertical that can.
//
// It is a DOCK at the bottom of the screen rather than a strip above the grid, and that is the whole
// difference between batch work being worth starting and not. The strip lived at the top of the page:
// forty photographs into a timeline you had scrolled a thousand pixels down, the only way to reach
// "Add to album" was to scroll back up to it — at which point you had lost your place in the list you
// were picking FROM. The dock rides the viewport, so picking and acting happen in the same place.
//
// PORTALED to <body> deliberately: the gallery paints its wall with `clip-path` (PhotosPage.css
// §2.12), and a clip-path'd ancestor becomes the containing block for `position: fixed` descendants —
// a dock rendered inside the page would be pinned to the bottom of the PAGE on exactly the surface
// this is most used on.

export default function PhotoSelectionBar({
  ids,
  active = true,
  onChanged,
  onCurated,
  onClear,
  onSelectAll,
  onDone,
  people = [],
  onReloadPeople,
}) {
  const [busy, setBusy] = useState(false);
  const [albumOpen, setAlbumOpen] = useState(false);
  const [tagOpen, setTagOpen] = useState(false);

  // Nothing selected and not selecting: no dock. Selecting with an empty pick keeps it, because
  // "Select all" and "Done" are the two things somebody who just turned the mode on wants.
  if (!active && !ids.length) return null;

  /** Report what a write DID, so the list can patch itself in place. See PhotosPage's `curated`:
   *  the alternative is a re-fetch, which throws the reader back to the top of the timeline holding
   *  nothing — the exact cost that made a forty-photo job feel like forty separate jobs. */
  const settled = (changes) => {
    if (onCurated) onCurated(ids, changes || {});
    else onChanged?.();
  };

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
      settled({ hidden });
    } catch {
      message.error("Could not update those photos.");
    } finally {
      setBusy(false);
    }
  };

  // §2.12: move the selection between the family timeline and the gallery. Member-permitted at
  // exactly hide's level — deciding a picture is art rather than family record is ordinary curation.
  // The server expands the move across settled duplicate groups and reports what it dragged along,
  // which is said out loud here for the same reason the album and tag actions say it: a member who
  // moved six cards and changed nine is owed the reason.
  const setShelf = async (shelf) => {
    setBusy(true);
    try {
      const response = await MovieAPI.setPhotosShelf(ids, shelf);
      if (!response.ok) {
        message.error("Could not move those photos.");
        return;
      }
      const body = await response.json();
      message.success(
        `${body.changed} moved to the ${shelf === "Archive" ? "gallery" : "timeline"}.` +
          (body.groupMembersIncluded
            ? ` ${body.groupMembersIncluded} duplicate copies moved with them.`
            : "")
      );
      settled({ shelf });
    } catch {
      message.error("Could not move those photos.");
    } finally {
      setBusy(false);
    }
  };

  const dock = (
    <div className="photo-selection-dock">
      <div className="photo-selection-bar">
        <span className="photo-selection-count">
          {ids.length ? `${ids.length} selected` : "Tap photos to select"}
        </span>

        {/* The batch job this mode exists for leads, and is drawn as the primary action. */}
        <button
          type="button"
          className="photos-button photos-button--stamp"
          disabled={busy || !ids.length}
          onClick={() => setAlbumOpen(true)}
        >
          Add to album
        </button>
        <button
          type="button"
          className="photos-button"
          disabled={busy || !ids.length}
          onClick={() => setShelf("Archive")}
        >
          Send to gallery
        </button>
        <button
          type="button"
          className="photos-button"
          disabled={busy || !ids.length}
          onClick={() => setTagOpen(true)}
        >
          Tag someone
        </button>
        <button type="button" className="photos-button" disabled={busy || !ids.length} onClick={() => setHidden(true)}>
          Hide
        </button>
        <button type="button" className="photos-button" disabled={busy || !ids.length} onClick={() => setHidden(false)}>
          Unhide
        </button>
        <button
          type="button"
          className="photos-button"
          disabled={busy || !ids.length}
          onClick={() => setShelf("Timeline")}
        >
          Return to timeline
        </button>

        <span className="photo-selection-spacer" />

        {onSelectAll && (
          <button type="button" className="photos-button" disabled={busy} onClick={onSelectAll}>
            Select all
          </button>
        )}
        <button type="button" className="photos-button" disabled={!ids.length} onClick={onClear}>
          Clear
        </button>
        {onDone && (
          <button type="button" className="photos-button" onClick={onDone}>
            Done
          </button>
        )}
      </div>
    </div>
  );

  return (
    <>
      {typeof document === "undefined" ? dock : createPortal(dock, document.body)}

      <BatchTagModal
        open={tagOpen}
        ids={ids}
        people={people}
        onReloadPeople={onReloadPeople}
        onClose={() => setTagOpen(false)}
        onDone={() => {
          setTagOpen(false);
          // A tag changes nothing about where these photographs sit, so the list is left alone.
          settled({});
        }}
      />

      <AddToAlbumModal
        open={albumOpen}
        ids={ids}
        onClose={() => setAlbumOpen(false)}
        onDone={(changes) => {
          setAlbumOpen(false);
          settled(changes || {});
        }}
      />
    </>
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
 *  slug is minted server-side, so nothing here has to invent a URL.
 *
 *  A new album can be filed straight onto the GALLERY shelf (§2.12) as it is created. That is one
 *  question asked at the moment it has an answer — "these forty are an artist's, not the family's" —
 *  rather than a second trip through the album's Edit panel afterwards, which is where it lived and
 *  where nobody found it. Existing albums are listed with the shelf they are on, so adding to one
 *  never has to be a guess about where the pictures are about to appear. */
export function AddToAlbumModal({ open, ids, onClose, onDone }) {
  const [albums, setAlbums] = useState([]);
  const [title, setTitle] = useState("");
  const [gallery, setGallery] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    setTitle("");
    setGallery(false);
    // Both shelves, because "add these to that album" is a question about an album, not about which
    // index it happens to be listed on. Each index is fetched inside its own chain: one shelf failing
    // must still leave the other pickable, since the point of this modal is to file the selection
    // somewhere, not to render a complete catalogue.
    const shelf = (call) =>
      Promise.resolve()
        .then(call)
        .then((r) => (r?.ok ? r.json() : { albums: [] }))
        .catch(() => ({ albums: [] }));

    Promise.all([shelf(() => MovieAPI.getPhotoAlbums()), shelf(() => MovieAPI.getPhotoGallery())])
      .then(([timeline, archive]) => setAlbums((timeline.albums || []).concat(archive.albums || [])))
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
      onDone?.({});
    } finally {
      setBusy(false);
    }
  };

  const create = async () => {
    if (!title.trim()) return;
    setBusy(true);
    try {
      const response = await MovieAPI.createPhotoAlbum({
        title: title.trim(),
        assetIds: ids,
        shelf: gallery ? "Archive" : undefined,
      });
      if (!response.ok) {
        message.error("Could not create that album.");
        return;
      }
      const body = await response.json();
      message.success(
        `Created ${body.album.title} with ${body.added}${gallery ? " in the gallery" : ""}.` +
          (body.redirectedToMasters ? ` ${body.redirectedToMasters} were duplicates of photos already added.` : "")
      );
      onDone?.({});
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
        <label className="photo-album-shelf-choice">
          <input type="checkbox" checked={gallery} onChange={(e) => setGallery(e.target.checked)} disabled={busy} />
          <span>File the new album in the gallery — art and memes, off the family timeline</span>
        </label>

        {albums.length > 0 && (
          <ul className="photo-album-list">
            {albums.map((album) => (
              <li key={album.id}>
                <button type="button" className="photo-album-choice" disabled={busy} onClick={() => addTo(album)}>
                  <span>
                    {album.title}
                    {album.shelf === "Archive" && <span className="photo-album-shelf-mark">gallery</span>}
                  </span>
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
