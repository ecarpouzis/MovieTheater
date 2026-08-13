import { useCallback, useEffect, useRef, useState } from "react";
import { Input, Popconfirm, Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import PhotoGrid from "./PhotoGrid";
import { formatRange } from "./PhotoAlbums";

// One album (docs/photos-plan.md §2.9): the same justified grid and lightbox as every other browse
// surface, plus the edits any family member may make — retitle, describe, hand-set date range, pick
// a cover, reorder, remove, delete.
//
// Reordering is expressed through SELECTION rather than drag-and-drop: pick the photos, then "Move
// to front". That maps exactly onto the server's partial-reorder rule (the ids sent take the front,
// everything else keeps its order behind them), so what the UI promises and what the API does are
// the same sentence.

export default function PhotoAlbumDetail({ slug, onBack, onOpen, onTitle, onMeta }) {
  const [album, setAlbum] = useState(null);
  const [items, setItems] = useState([]);
  const [state, setState] = useState("loading");
  const [hasMore, setHasMore] = useState(false);
  const [selected, setSelected] = useState([]);
  const [selecting, setSelecting] = useState(false);
  const [editing, setEditing] = useState(false);
  const skipRef = useRef(0);
  const inFlightRef = useRef(false);

  const load = useCallback(
    async (append) => {
      if (inFlightRef.current) return;
      inFlightRef.current = true;
      try {
        const response = await MovieAPI.getPhotoAlbum(slug, { skip: append ? skipRef.current : 0 });
        if (!response.ok) {
          setState(response.status === 404 ? "missing" : "error");
          return;
        }
        const body = await response.json();
        setAlbum(body.album);
        // The page's own <h1> carries the album's name (the section label moves up to the eyebrow),
        // so the title is published rather than printed twice.
        onTitle?.(body.album?.title ?? null);
        // §2.12: and the shelf + artist, which decide the eyebrow, the headline and whether the page
        // is lit as a gallery wall. Published rather than re-fetched — this component is the only one
        // that has the album.
        onMeta?.(
          body.album ? { shelf: body.album.shelf, artistName: body.album.artistName || null } : null
        );
        const cards = (body.items || []).map((entry) => entry.card);
        setItems((prev) => (append ? prev.concat(cards) : cards));
        skipRef.current = (append ? skipRef.current : 0) + cards.length;
        setHasMore(!!body.hasMore);
        setState("ready");
      } catch {
        setState("error");
      } finally {
        inFlightRef.current = false;
      }
    },
    [slug, onTitle, onMeta]
  );

  useEffect(() => {
    setState("loading");
    setItems([]);
    setSelected([]);
    skipRef.current = 0;
    load(false);
  }, [slug, load]);

  const { sentinelRef, recheck } = useInfiniteScroll({
    enabled: state === "ready",
    hasMore,
    onLoadMore: () => load(true),
  });

  useEffect(() => {
    recheck();
  }, [items.length, recheck]);

  const reload = () => {
    skipRef.current = 0;
    setSelected([]);
    load(false);
  };


  const selection = {
    // An explicit mode, like every other view: without it the first click could only ever open a
    // photo, and there would be no way to begin selecting at all.
    active: selecting,
    has: (id) => selected.includes(id),
    toggle: (id) => setSelected((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : prev.concat(id))),
  };

  if (state === "loading") return <Spin />;
  if (state === "missing") return <p className="photos-note">That album is gone.</p>;
  if (state === "error") return <p className="photos-note">Could not load that album.</p>;

  const act = async (promise, done) => {
    const response = await promise;
    if (!response.ok) {
      message.error("That did not work.");
      return;
    }
    done?.();
  };

  return (
    <div className="photo-album-detail">
      <nav className="photo-crumbs">
        <button type="button" className="photo-crumb" onClick={onBack}>
          {album.shelf === "Archive" ? "The gallery" : "All albums"}
        </button>
        <button type="button" className="photo-crumb">
          {album.title}
        </button>
      </nav>

      <div className="photos-head">
        <div>
          <p className="photos-note">
            {items.length.toLocaleString()}
            {hasMore ? "+" : ""} shown{formatRange(album)}
            {album.description ? ` · ${album.description}` : ""}
          </p>
        </div>
        <div className="photo-album-actions">
          <button
            type="button"
            className="photos-button"
            onClick={() => {
              setSelecting((v) => !v);
              setSelected([]);
            }}
          >
            {selecting ? "Done selecting" : "Select"}
          </button>
          <button type="button" className="photos-button" onClick={() => setEditing((v) => !v)}>
            {editing ? "Done" : "Edit"}
          </button>
          <Popconfirm
            title="Delete this album?"
            // Rows only: the photos and the files are untouched. Confirmed because it is hand-built
            // curation (§2.11), not because anything on disk is at risk.
            description="The photos stay exactly where they are — only the album is removed."
            okText="Delete"
            cancelText="Keep"
            onConfirm={() => act(MovieAPI.deletePhotoAlbum(album.id), onBack)}
          >
            <button type="button" className="photos-button">
              Delete album
            </button>
          </Popconfirm>
        </div>
      </div>

      {editing && <AlbumEditor album={album} onSaved={reload} />}

      {selected.length > 0 && (
        <div className="photo-selection-bar">
          <span className="photo-selection-count">{selected.length} selected</span>
          <button
            type="button"
            className="photos-button"
            onClick={() => act(MovieAPI.reorderPhotoAlbum(album.id, selected), reload)}
          >
            Move to front
          </button>
          <button
            type="button"
            className="photos-button"
            disabled={selected.length !== 1}
            onClick={() => act(MovieAPI.updatePhotoAlbum(album.id, { coverAssetId: selected[0] }), reload)}
          >
            Set as cover
          </button>
          <button
            type="button"
            className="photos-button"
            onClick={() => act(MovieAPI.removeFromPhotoAlbum(album.id, selected), reload)}
          >
            Remove from album
          </button>
          <button type="button" className="photos-button" onClick={() => setSelected([])}>
            Clear
          </button>
        </div>
      )}

      {/* §2.12: an artist collection is hung, not stacked. The grid gets more air and each picture
          gets a plaque — the filename-derived title and the artist — because that is how you read a
          wall of paintings, and it is exactly wrong for a hundred snapshots of one afternoon. */}
      <PhotoGrid
        items={items}
        groupBySection={false}
        onOpen={onOpen}
        selection={selection}
        gallery={!!album.artistName}
        plaqueArtist={album.artistName || null}
        emptyText="This album is empty. Add photos from any view's selection mode."
      />
      <div ref={sentinelRef} className="photos-sentinel">
        {hasMore && <Spin size="small" />}
      </div>
    </div>
  );
}

/** Title, description and the hand-set range (§2.9). The range is deliberately independent of the
 *  member dates: an album of undated scans still knows it was "Summer 1994". */
function AlbumEditor({ album, onSaved }) {
  const [title, setTitle] = useState(album.title);
  const [description, setDescription] = useState(album.description || "");
  const [rangeStart, setRangeStart] = useState(album.rangeStart ? String(album.rangeStart).split("T")[0] : "");
  const [rangeEnd, setRangeEnd] = useState(album.rangeEnd ? String(album.rangeEnd).split("T")[0] : "");
  // §2.12: which shelf the album is indexed on, and the artist that makes it an artist collection.
  const [shelf, setShelf] = useState(album.shelf === "Archive" ? "Archive" : "Timeline");
  const [artistName, setArtistName] = useState(album.artistName || "");
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    try {
      const response = await MovieAPI.updatePhotoAlbum(album.id, {
        title,
        description,
        // Sent WITH their "was this field touched" flags: a null date means "clear it", which is a
        // different instruction from "leave it alone", and a nullable field alone cannot say which.
        rangeStartSet: true,
        rangeStart: rangeStart ? `${rangeStart}T00:00:00` : null,
        rangeEndSet: true,
        rangeEnd: rangeEnd ? `${rangeEnd}T00:00:00` : null,
        shelf,
        artistNameSet: true,
        artistName: artistName.trim() || null,
      });
      if (!response.ok) {
        message.error("Could not save the album.");
        return;
      }
      onSaved?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="photo-album-editor">
      <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Title" disabled={busy} />
      <Input
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        placeholder="Description"
        disabled={busy}
      />
      <Input type="date" value={rangeStart} onChange={(e) => setRangeStart(e.target.value)} disabled={busy} />
      <Input type="date" value={rangeEnd} onChange={(e) => setRangeEnd(e.target.value)} disabled={busy} />
      {/* Moving an album between shelves moves the ALBUM, never its photographs: which index a
          collection appears on and whether a picture is part of the family record are two different
          questions, and the selection bar answers the second one. */}
      <label className="photo-album-shelf">
        <span>Shelf</span>
        <select
          value={shelf}
          onChange={(e) => setShelf(e.target.value)}
          disabled={busy}
          aria-label="Album shelf"
        >
          <option value="Timeline">Family album</option>
          <option value="Archive">Gallery</option>
        </select>
      </label>
      {shelf === "Archive" && (
        <Input
          value={artistName}
          onChange={(e) => setArtistName(e.target.value)}
          placeholder="Artist (makes this an artist collection)"
          disabled={busy}
        />
      )}
      <button type="button" className="photos-button" disabled={busy || !title.trim()} onClick={save}>
        Save
      </button>
    </div>
  );
}
