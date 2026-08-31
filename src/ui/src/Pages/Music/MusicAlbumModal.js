import { useEffect, useState } from "react";
import { Modal, Spin, Button, Slider, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import MusicSongRow from "./MusicSongRow";
// For the shared close-chip tokens (--sheet-close-*); every rule in that file is scoped to
// .sheet-modal, which this hand-rolled sheet is not, so it brings nothing else with it.
import "../../Components/SheetModal.css";
import "./MusicPage.css";
import "./MusicPlaylists.css";
import { formatDuration } from "../../utils/format";
import { SHEET_Z } from "../../Components/sheetModal";

// Album detail + tracklist (music-plan.md §2.6): hero header → scrolling tracklist → play actions.
// Follows the site's modal convention (GameModal peers) in spirit; antd Modal carries the shell.

/**
 * The listener's own 0–100 score for this record (R9 S10) — the movie modal's YourRating, in the
 * one sheet the site deliberately keeps off the shared shell.
 *
 * The rules are the movie side's, because they were learned there: **0 is a real score and unrated
 * is no row**, so "Clear" removes the rating rather than writing a zero; the value is committed on
 * RELEASE (`onChangeComplete`), never on every drag frame, so one gesture is one request; and the
 * change is applied optimistically and rolled back on failure, because this modal sits over a
 * playing queue and a spinner across the hero would be the loudest thing on the screen.
 */
function AlbumRating({ album, onRated }) {
  const rated = typeof album.myRating === "number";
  const stored = rated ? album.myRating : 0;
  const [value, setValue] = useState(stored);
  const [open, setOpen] = useState(rated);

  useEffect(() => {
    setValue(stored);
    setOpen(rated);
  }, [album.id, stored, rated]);

  // v is a real 0–100 score, or null to clear (unrate).
  const persist = (v) => {
    const previous = album.myRating ?? null;
    onRated(v);
    if (v == null) setOpen(false);
    MovieAPI.setMusicRatings([{ albumId: album.id, value: v }])
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .catch(() => {
        message.error("Couldn't save your rating.");
        onRated(previous);
        setValue(previous ?? 0);
        setOpen(previous != null);
      });
  };

  if (!open) {
    return (
      <div className="music-album-rating">
        <Button size="small" onClick={() => setOpen(true)}>★ Rate</Button>
        <AlbumScoreLine album={album} />
      </div>
    );
  }

  return (
    <div className="music-album-rating music-album-rating--open">
      <span className="music-album-rating-label">Your rating</span>
      <Slider className="music-album-rating-slider" min={0} max={100} value={value} onChange={setValue} onChangeComplete={persist} />
      <span className="music-album-rating-score">{rated ? value : "—"}</span>
      {rated && (
        <button type="button" className="music-album-rating-clear" title="Remove your rating" onClick={() => persist(null)}>
          Clear
        </button>
      )}
    </div>
  );
}

/**
 * What everyone else thinks. THREE different facts, never merged into one number here: the house's
 * own average (with its vote count, because an average of one is not an average), the outside
 * community's rating (likewise), and the popularity signal — which says how KNOWN the record is and
 * not how good it is. The blend behind the Top-rated order lives on the server; showing it here as a
 * single score would invite reading it as a verdict the house has not reached.
 *
 * Each part names its own source, because the whole point is that they are answers to different
 * questions and the site says so consistently — on the tile, in the Sort pill, and here.
 */
function AlbumScoreLine({ album }) {
  const count = album.ratingCount ?? 0;
  const votes = album.externalRatingVotes ?? 0;
  const parts = [];
  if (count > 0) parts.push(`${Math.round(album.ratingAvg)} from ${count} here`);
  if (typeof album.externalRating === "number") {
    parts.push(votes > 0
      ? `${album.externalRating} rated by ${votes} elsewhere`
      : `${album.externalRating} rated elsewhere`);
  }
  if (typeof album.popularity === "number") parts.push(`${album.popularity} popularity`);
  if (!parts.length) return null;
  return <span className="music-album-rating-house">{parts.join(" · ")}</span>;
}

function MusicAlbumModal({ albumId, onClose, onAddToPlaylist }) {
  const player = useMusicPlayer();
  const [album, setAlbum] = useState(null);

  useEffect(() => {
    if (albumId == null) {
      setAlbum(null);
      return undefined;
    }
    let alive = true;
    setAlbum(null);
    MovieAPI.getMusicAlbum(albumId)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => alive && setAlbum(data))
      .catch(() => alive && setAlbum({ missing: true }));
    return () => { alive = false; };
  }, [albumId]);

  function toQueueEntries() {
    return album.tracks.map((t) => ({
      id: t.id,
      title: t.title,
      artist: album.artistName,
      album: album.title,
      albumId: album.id,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
      missing: t.missing,
    }));
  }

  const playable = album && !album.missing && album.tracks.some(player.isPlayable);

  return (
    <Modal
      open={albumId != null}
      onCancel={onClose}
      footer={null}
      width={560}
      // The site's dialog layer (Components/sheetModal.js). Harmless while this was a small card
      // parked 100px down the page; the moment it became a full-height sheet its top ran under the
      // fixed nav bar, which swallowed the ✕ — on a phone that left no way to close it at all.
      zIndex={SHEET_Z}
      destroyOnHidden
      /* Both classes are needed: the wrap carries the dialog, the root carries the mask, and BOTH
         have to stop above the play bar or the bar is either covered or dimmed-and-dead. */
      wrapClassName="music-album-modal"
      rootClassName="music-album-modal-root"
    >
      {!album && (
        <div style={{ display: "flex", justifyContent: "center", padding: "48px 0" }}>
          <Spin />
        </div>
      )}
      {album && album.missing && <p>This album couldn&apos;t be loaded.</p>}
      {album && !album.missing && (
        <div className="music-album-detail">
          <div className="music-album-detail-head">
            <MusicAlbumArt
              albumId={album.id}
              hasArt={album.hasArt}
              title={album.title}
              dominantColor={album.dominantColor}
              thumb={false}
              className="music-album-hero"
            />
            <div className="music-album-detail-meta">
              <h2 className="music-album-detail-title">{album.title}</h2>
              <div className="music-album-detail-sub">
                {album.artistName}
                {album.year != null && ` · ${album.year}`}
                {album.tag && ` · ${album.tag}`}
                {` · ${album.tracks.length} track${album.tracks.length === 1 ? "" : "s"}`}
              </div>
              {album.genres?.length > 0 && (
                <div className="music-album-detail-genres">
                  {album.genres.map((g) => (
                    <span className="music-album-genre" key={g}>{g}</span>
                  ))}
                </div>
              )}
              <AlbumRating
                album={album}
                onRated={(v) => setAlbum((prev) => (prev && prev.id === album.id ? { ...prev, myRating: v } : prev))}
              />
              <div className="music-album-detail-actions">
                <Button type="primary" disabled={!playable} onClick={() => player.playTracks(toQueueEntries(), 0)}>
                  ▶ Play
                </Button>
                <Button disabled={!playable} onClick={() => player.shuffleTracks(toQueueEntries())}>
                  🔀 Shuffle
                </Button>
              </div>
              {/* A second row rather than one wrapping row: Play and Shuffle start listening now,
                  Queue and Playlist put the album somewhere for later. Left to wrap on its own, the
                  break landed between the two "add it to something" buttons and grouped them wrong. */}
              <div className="music-album-detail-actions music-album-detail-actions--more">
                <Button disabled={!playable} onClick={() => player.enqueue(toQueueEntries())}>
                  + Queue
                </Button>
                {onAddToPlaylist && (
                  <Button
                    disabled={!playable}
                    onClick={() => onAddToPlaylist(
                      album.tracks.map((t) => ({ id: t.id, title: t.title })),
                      album.title
                    )}
                  >
                    ＋ Playlist
                  </Button>
                )}
              </div>
            </div>
          </div>

          <div className="music-song-list music-album-detail-tracks">
            {album.tracks.map((t, i) => (
              <MusicSongRow
                key={t.id}
                no={t.trackNo ?? "·"}
                title={t.title}
                disc={t.discNo != null && t.discNo > 1 ? `CD${t.discNo}` : null}
                time={formatDuration(t.durationSec)}
                disabled={!player.isPlayable(t)}
                hint={t.missing
                  ? "File is missing"
                  : t.requiresTranscode && !player.canTranscode
                    ? "This format can't be streamed yet"
                    : t.title}
                onPlay={() => player.playTracks(toQueueEntries(), i)}
                onQueue={() => player.enqueue([toQueueEntries()[i]])}
                onAdd={onAddToPlaylist ? () => onAddToPlaylist([{ id: t.id, title: t.title }], t.title) : undefined}
              />
            ))}
          </div>
        </div>
      )}
    </Modal>
  );
}

export default MusicAlbumModal;
