import { useCallback, useEffect, useState } from "react";
import { Input, Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

// The album index (docs/photos-plan.md §2.9). Albums are curated DB rows, never folders: the tree
// holds device dumps and misc piles that are not albums, so the folder view is a browse surface and
// a seed, and the folder is never an album's identity.
//
// Any family member can create one — the plan says so, and a shared family album with an owner-only
// curation model would be one person's album.

export default function PhotoAlbums({ onOpenAlbum }) {
  const [albums, setAlbums] = useState([]);
  const [state, setState] = useState("loading");
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const response = await MovieAPI.getPhotoAlbums();
      if (!response.ok) {
        setState("error");
        return;
      }
      const body = await response.json();
      setAlbums(body.albums || []);
      setState("ready");
    } catch {
      setState("error");
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const create = async () => {
    if (!title.trim()) return;
    setBusy(true);
    try {
      const response = await MovieAPI.createPhotoAlbum({ title: title.trim() });
      if (!response.ok) {
        message.error("Could not create that album.");
        return;
      }
      setTitle("");
      await load();
    } finally {
      setBusy(false);
    }
  };

  if (state === "loading") return <Spin />;
  if (state === "error") return <p className="photos-note">Could not load the albums.</p>;

  return (
    <div className="photo-albums">
      <div className="photo-album-new">
        <Input
          placeholder="New album title"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          onPressEnter={create}
          disabled={busy}
        />
        <button type="button" className="photos-button" disabled={busy || !title.trim()} onClick={create}>
          Create album
        </button>
      </div>

      <AlbumCards
        albums={albums}
        onOpenAlbum={onOpenAlbum}
        emptyText="No albums yet. Make one here, from a selection in any view, or from a folder in the folder browser."
      />
    </div>
  );
}

/**
 * The shelf of album cards, shared by the family album index and the Gallery (§2.12).
 *
 * One component for both because they are the same object seen on two shelves — and because two
 * copies of a card would drift, which on a page whose whole subject is "these pictures belong
 * together" is the one inconsistency the eye actually catches.
 *
 * An album carrying an `artistName` is drawn as an ARTIST COLLECTION: the artist's name leads in the
 * plate under the picture and the album's own title drops to the line beneath, but only when the two
 * differ — a collection titled with its artist's name should not print it twice.
 */
export function AlbumCards({ albums, onOpenAlbum, emptyText }) {
  if (!albums.length) return <p className="photos-note">{emptyText}</p>;

  return (
    <ul className="photo-album-cards">
      {albums.map((album) => {
        const artist = album.artistName || null;
        const subtitle = artist && artist !== album.title ? album.title : null;
        return (
          <li key={album.id}>
            <button
              type="button"
              className={`photo-album-card${artist ? " is-artist" : ""}`}
              onClick={() => onOpenAlbum(album.slug)}
            >
              <span className="photo-album-cover">
                {album.coverUrl ? <img src={album.coverUrl} alt="" loading="lazy" /> : <span>◇</span>}
              </span>
              <span className="photo-album-card-title">{artist || album.title}</span>
              {subtitle && <span className="photo-album-card-sub">{subtitle}</span>}
              <span className="photo-album-card-meta">
                {album.count.toLocaleString()} {album.count === 1 ? "item" : "items"}
                {formatRange(album)}
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/** The album's HAND-SET range, printed as the wall-clock dates they are — independent of member
 *  dates on purpose (§2.9), because an album of undated scans still knows it was "Summer 1994". */
export function formatRange(album) {
  const start = album.rangeStart ? String(album.rangeStart).split("T")[0] : null;
  const end = album.rangeEnd ? String(album.rangeEnd).split("T")[0] : null;
  if (!start && !end) return "";
  if (start && end) return ` · ${start} – ${end}`;
  return ` · ${start || end}`;
}
