import { useEffect, useState } from "react";
import { Modal, Spin, Button } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import MusicSongRow from "./MusicSongRow";
import "./MusicPage.css";
import "./MusicPlaylists.css";

// Album detail + tracklist (music-plan.md §2.6): hero header → scrolling tracklist → play actions.
// Follows the site's modal convention (GameModal peers) in spirit; antd Modal carries the shell.

function formatTime(sec) {
  if (!Number.isFinite(sec) || sec <= 0) return "";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s < 10 ? "0" : ""}${s}`;
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
    <Modal open={albumId != null} onCancel={onClose} footer={null} width={560} destroyOnHidden>
      {!album && (
        <div style={{ display: "flex", justifyContent: "center", padding: "48px 0" }}>
          <Spin />
        </div>
      )}
      {album && album.missing && <p>This album couldn't be loaded.</p>}
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
              <div className="music-album-detail-actions">
                <Button type="primary" disabled={!playable} onClick={() => player.playTracks(toQueueEntries(), 0)}>
                  ▶ Play
                </Button>
                <Button disabled={!playable} onClick={() => player.shuffleTracks(toQueueEntries())}>
                  🔀 Shuffle
                </Button>
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
                time={formatTime(t.durationSec)}
                disabled={!player.isPlayable(t)}
                hint={t.missing
                  ? "File is missing"
                  : t.requiresTranscode && !player.canTranscode
                    ? "This format can't be streamed yet"
                    : t.title}
                onPlay={() => player.playTracks(toQueueEntries(), i)}
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
