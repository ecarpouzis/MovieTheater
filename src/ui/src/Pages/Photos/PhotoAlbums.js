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

      {albums.length === 0 ? (
        <p className="photos-note">
          No albums yet. Make one here, from a selection in any view, or from a folder in the folder
          browser.
        </p>
      ) : (
        <ul className="photo-album-cards">
          {albums.map((album) => (
            <li key={album.id}>
              <button type="button" className="photo-album-card" onClick={() => onOpenAlbum(album.slug)}>
                <span className="photo-album-cover">
                  {album.coverUrl ? <img src={album.coverUrl} alt="" loading="lazy" /> : <span>◇</span>}
                </span>
                <span className="photo-album-card-title">{album.title}</span>
                <span className="photo-album-card-meta">
                  {album.count.toLocaleString()} {album.count === 1 ? "item" : "items"}
                  {formatRange(album)}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
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
