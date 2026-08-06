import { useCallback, useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicPlaylistManageModal from "./MusicPlaylistManageModal";
import "./MusicPlaylists.css";

// ── Playlist manager (music-plan.md §2.4) ───────────────────────────────────
// Playlists used to sit in a strip above the browse grid, which pushed the library down the page
// and got worse with every playlist made. They get their own route instead, so browsing is browsing
// and playlists are a place you go.
//
// The list is "mine + shared with me": a playlist someone shared has to surface somewhere, and this
// is that somewhere. Ownership decides which controls a card offers — a member can play, shuffle and
// edit the contents, but deleting it or changing who else has access stays with the owner.

export default function MusicPlaylistsPage({ userData }) {
  const player = useMusicPlayer();
  const history = useHistory();
  const [playlists, setPlaylists] = useState([]);
  const [loading, setLoading] = useState(true);
  const [manageId, setManageId] = useState(null);

  const gated = !userData?.hasPassword;

  const reload = useCallback(() => {
    if (gated) { setLoading(false); return; }
    MovieAPI.getMyMusicPlaylists()
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((list) => setPlaylists(list || []))
      .catch(() => setPlaylists([]))
      .finally(() => setLoading(false));
  }, [gated]);

  useEffect(() => { reload(); }, [reload]);

  function play(id, { shuffle = false } = {}) {
    MovieAPI.getMusicPlaylistItems(id)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => {
        const tracks = (data.items || []).map((t) => ({
          id: t.id,
          title: t.title,
          artist: t.artistName,
          album: t.albumTitle,
          albumId: t.albumId,
          durationSec: t.durationSec,
          requiresTranscode: t.requiresTranscode,
          missing: t.missing,
        }));
        if (shuffle) player.shuffleTracks(tracks);
        else player.playTracks(tracks, 0);
      })
      .catch(() => {});
  }

  if (gated) {
    return (
      <div className="music-page">
        <h1 className="music-title">Playlists</h1>
        <p className="music-empty">Music needs an account with a password.</p>
      </div>
    );
  }

  const mine = playlists.filter((p) => p.isOwner);
  const shared = playlists.filter((p) => !p.isOwner);

  function card(p) {
    return (
      <div className="music-playlist-card" key={p.id} data-testid="music-playlist-card">
        <div className="music-playlist-card-name" title={p.name}>{p.name}</div>
        <div className="music-playlist-card-sub" title={(p.trackTitles || []).join(", ")}>
          {p.count} track{p.count === 1 ? "" : "s"}
          {!p.isOwner && p.ownerName ? ` · shared by ${p.ownerName}` : ""}
          {p.isOwner && p.sharedWith > 0 ? ` · shared with ${p.sharedWith}` : ""}
          {p.trackTitles && p.trackTitles.length > 0 ? ` · ${p.trackTitles.join(", ")}` : ""}
        </div>
        <div className="music-playlist-card-actions">
          <button className="music-playlist-btn" disabled={p.count === 0} onClick={() => play(p.id)}>
            ▶ Play
          </button>
          <button className="music-playlist-btn" disabled={p.count === 0} onClick={() => play(p.id, { shuffle: true })}>
            🔀 Shuffle
          </button>
          <button className="music-playlist-btn" onClick={() => setManageId(p.id)}>Manage</button>
        </div>
      </div>
    );
  }

  return (
    <div className="music-page">
      <div className="music-playlists-head">
        <h1 className="music-title">Playlists</h1>
        <button className="music-playlist-btn" onClick={() => history.push("/music")}>← Browse library</button>
      </div>

      {loading && <p className="music-empty">Loading…</p>}

      {!loading && playlists.length === 0 && (
        <p className="music-empty">
          No playlists yet. Add tracks to one from any album or song row in the library.
        </p>
      )}

      {mine.length > 0 && (
        <section className="music-section">
          <h2 className="music-section-head">
            Yours <span className="music-count">{mine.length}</span>
          </h2>
          <div className="music-playlist-grid">{mine.map(card)}</div>
        </section>
      )}

      {shared.length > 0 && (
        <section className="music-section">
          <h2 className="music-section-head">
            Shared with you <span className="music-count">{shared.length}</span>
          </h2>
          <div className="music-playlist-grid">{shared.map(card)}</div>
        </section>
      )}

      <MusicPlaylistManageModal
        open={manageId != null}
        playlistId={manageId}
        onClose={() => setManageId(null)}
        onChanged={reload}
      />
    </div>
  );
}
