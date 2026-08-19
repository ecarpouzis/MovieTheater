import { useCallback, useEffect, useState } from "react";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { AlbumCards } from "./PhotoAlbums";
import LoadFailure from "../../Components/LoadFailure";

// The Gallery (docs/photos-plan.md §2.12).
//
// The tree carries piles that are not the family record — art the owner collects, memes, reference
// scrap. The owner's verdict: they "are not album material … remove them from the typical timeline …
// We'll want a place to store art and memes eventually, but it isn't the timeline, put them in
// another section." This is that section.
//
// It is NOT the hidden pile. Hidden is admin-only (Phase 4), so hiding art would take it away from
// the family; everything here is browsable by every member. The rooms are ordinary albums on the
// archive shelf, and their detail pages are the ordinary /photos/albums/<slug> URLs — one album
// component, two shelves, and every link ever sent still resolves.
//
// The index leads with ARTIST COLLECTIONS (an archive album carrying an artist name), then the plain
// collections. The server does that ordering; this renders it, so the two cannot disagree about what
// "first" means.

export default function PhotoGallery({ onOpenAlbum }) {
  const [albums, setAlbums] = useState([]);
  const [state, setState] = useState("loading");

  const load = useCallback(async () => {
    try {
      const response = await MovieAPI.getPhotoGallery();
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

  if (state === "loading") return <Spin />;
  if (state === "error") return <LoadFailure message="Could not load the gallery." />;

  const artists = albums.filter((album) => album.artistName);
  const collections = albums.filter((album) => !album.artistName);

  return (
    // The mount tone is what marks this as a different room without inventing a second design: the
    // page is the same warm paper, and the gallery sits on it as a deeper, recessed wall.
    <div className="photo-gallery">
      <p className="photos-note">
        Art, memes and reference pictures — kept out of the family timeline, kept here. Nothing was
        moved or deleted on disk; these are the same files, filed on a different shelf.
      </p>

      {artists.length > 0 && (
        <section className="photo-gallery-shelf">
          <h2 className="photos-panel-head">Artists</h2>
          <AlbumCards albums={artists} onOpenAlbum={onOpenAlbum} />
        </section>
      )}

      {collections.length > 0 && (
        <section className="photo-gallery-shelf">
          {/* The heading only earns its place once there is something above it to be distinguished
              from. A gallery of nothing but meme piles is just a gallery. */}
          {artists.length > 0 && <h2 className="photos-panel-head">Collections</h2>}
          <AlbumCards albums={collections} onOpenAlbum={onOpenAlbum} />
        </section>
      )}

      {albums.length === 0 && (
        <p className="photos-note">
          The gallery is empty. Send pictures here with “Send to gallery” in any view’s selection
          mode, or file a whole folder with the <code>photos-shelf</code> command.
        </p>
      )}
    </div>
  );
}
