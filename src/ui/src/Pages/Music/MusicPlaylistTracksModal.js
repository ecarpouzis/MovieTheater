import { useEffect, useState } from "react";
import Modal from "antd/es/modal";
import Spin from "antd/es/spin";
import Button from "antd/es/button";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicSongRow from "./MusicSongRow";
import "./MusicPage.css";
import "./MusicPlaylists.css";

// ── Playlist tracklist ──────────────────────────────────────────────────────
// Until this existed a playlist was all-or-nothing: Play and Shuffle both REPLACED the queue with
// the whole list, and the only way to see inside one was Manage — an editing surface with a
// Save/Cancel contract, where playing a track mid-edit means either losing the edit or queueing
// from a lineup that isn't saved yet. So there was no way to take one song off a playlist.
//
// This is the album modal's shape applied to a playlist, deliberately: same header verbs, same
// MusicSongRow rows, same ☰/＋ trailing buttons. A playlist and an album are both "a list of tracks
// you want to do something with", and someone who has used one should not have to learn the other.

function formatTime(sec) {
  if (!Number.isFinite(sec) || sec <= 0) return "";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s < 10 ? "0" : ""}${s}`;
}

export default function MusicPlaylistTracksModal({ playlistId, open, onClose, onAddToPlaylist }) {
  const player = useMusicPlayer();
  const [data, setData] = useState(null);

  useEffect(() => {
    if (!open || playlistId == null) {
      setData(null);
      return undefined;
    }
    // `alive` because the modal can be closed (or another playlist opened) while this is in flight,
    // and a late response would otherwise repopulate a closed sheet — or show the wrong playlist.
    let alive = true;
    setData(null);
    MovieAPI.getMusicPlaylistItems(playlistId)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((d) => alive && setData(d))
      .catch(() => alive && setData({ missing: true }));
    return () => { alive = false; };
  }, [open, playlistId]);

  // The player's queue entries are a different shape from the API's rows, and the mapping is the
  // same one the playlists page uses for Play/Shuffle — one shape for the whole playlist, so
  // "queue this one" and "play from here" can never disagree about what a track is.
  function toQueueEntries() {
    return (data.items || []).map((t) => ({
      id: t.id,
      title: t.title,
      artist: t.artistName,
      album: t.albumTitle,
      albumId: t.albumId,
      durationSec: t.durationSec,
      requiresTranscode: t.requiresTranscode,
      missing: t.missing,
    }));
  }

  const items = data && !data.missing ? (data.items || []) : [];
  const playable = items.some(player.isPlayable);

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={560}
      // No antd `title`: wrapClassName turns this into a full-height sheet whose body is a flex
      // column with zero padding, and a header bar outside that column gets none of the sheet's
      // gutters. The name goes in .music-album-detail-head instead, where the album's does.
      // Same band as the album modal: above the nav bar, below nothing that matters, and stopping
      // short of the play bar so the bar stays live behind it (see antdPopupLayer.css).
      zIndex={1500}
      destroyOnHidden
      wrapClassName="music-album-modal"
      rootClassName="music-album-modal-root"
    >
      {!data && (
        <div className="mplman-loading"><Spin /></div>
      )}
      {data && data.missing && <p>This playlist couldn&apos;t be loaded.</p>}
      {data && !data.missing && (
        // Deliberately the ALBUM sheet's container chain, class for class. The sheet delegates its
        // scrolling to .music-album-detail-tracks, and that only works if every box above it is a
        // flex column that may shrink — .ant-modal-body → .music-album-detail → the list. Wrapping
        // this in a container of my own instead broke the chain at one link, which does not look
        // like a bug: the sheet renders, and the tracklist simply grows past the bottom of the
        // screen and scrolls nothing.
        <div className="music-album-detail">
          <div className="music-album-detail-head">
            <div className="music-album-detail-meta">
              <h2 className="music-album-detail-title">
                {data.isFavorites ? "♥ Favorites" : data.name}
              </h2>
              <div className="music-album-detail-sub">
                {items.length} track{items.length === 1 ? "" : "s"}
              </div>

              <div className="music-album-detail-actions">
                <Button type="primary" disabled={!playable} onClick={() => player.playTracks(toQueueEntries(), 0)}>
                  ▶ Play
                </Button>
                <Button disabled={!playable} onClick={() => player.shuffleTracks(toQueueEntries())}>
                  🔀 Shuffle
                </Button>
                {/* The whole-playlist counterpart to each row's ☰. Play and Shuffle both REPLACE
                    the queue; this is the one that adds to what is already playing, which is the
                    thing the card's two buttons could never do. */}
                <Button disabled={!playable} onClick={() => player.enqueue(toQueueEntries())}>
                  ☰ Queue
                </Button>
              </div>
            </div>
          </div>

          {items.length === 0 ? (
            <div className="music-playlist-tracks-empty">
              {data.isFavorites
                ? "Nothing favorited yet. Hit the ♥ in the player while a song is playing."
                : "This playlist is empty. Add tracks from an album or a song row."}
            </div>
          ) : (
            <div className="music-song-list music-album-detail-tracks">
              {items.map((t, i) => (
                // A track can legitimately appear twice in a playlist, so the key carries the
                // position too — the same reason the manage modal keys on id+ordinal.
                <MusicSongRow
                  key={`${t.id}-${i}`}
                  no={i + 1}
                  title={t.title}
                  // Artist only, not "Artist — Album". In a playlist the artist is what tells two
                  // rows apart; the album almost never is, and carrying it doubled the length of
                  // the one element competing with the title for a phone's width.
                  meta={t.artistName}
                  time={formatTime(t.durationSec)}
                  disabled={!player.isPlayable(t)}
                  hint={t.missing
                    ? "File is missing"
                    : t.requiresTranscode && !player.canTranscode
                      ? "This format can't be streamed yet"
                      : t.title}
                  // Plays the playlist FROM here rather than this track alone — the album modal's
                  // behaviour, and the one that matches what clicking a track in a list means.
                  onPlay={() => player.playTracks(toQueueEntries(), i)}
                  onQueue={() => player.enqueue([toQueueEntries()[i]])}
                  onAdd={onAddToPlaylist ? () => onAddToPlaylist([{ id: t.id, title: t.title }], t.title) : undefined}
                />
              ))}
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}
