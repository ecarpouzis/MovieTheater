import { useCallback, useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicPlaylistManageModal from "./MusicPlaylistManageModal";
import MusicPlaylistTracksModal from "./MusicPlaylistTracksModal";
import MusicPlaylistPickerModal from "./MusicPlaylistPickerModal";
// ⚠ BOTH sheets, and MusicPage.css is the load-bearing one. Every /music route is its own lazy
// chunk, so a page only has the CSS it imports ITSELF — this page's shell is `.music-page`, which
// lives in MusicPage.css and carries the site's content max-width. Without the import the shell had
// no rules at all and the playlists ran the full width of the monitor, but only when this route was
// the first one visited: browsing the library first pulled MusicPage.css in and hid the bug.
import "./MusicPage.css";
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
  // Looking inside a playlist is a DIFFERENT job from editing it: this one plays and queues, and
  // never writes. Manage owns the reorder/remove/share half and its Save/Cancel contract.
  const [tracksId, setTracksId] = useState(null);
  // The tracklist's ＋ needs somewhere to put a track, and this route had no picker of its own —
  // the library page carries one for exactly the same reason (music-plan.md Phase 3).
  const [pickerTracks, setPickerTracks] = useState(null);
  const [pickerName, setPickerName] = useState("");

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
      <div
        className={`music-playlist-card${p.isFavorites ? " music-playlist-card--favorites" : ""}`}
        key={p.id}
        data-testid="music-playlist-card"
      >
        {/* The name is the way in, because "click the thing to see what's in it" is what an album
            card already does and it is what anyone tries first. A real button, so it is reachable
            by keyboard — the card itself can't be one, since it contains buttons. */}
        <button
          className="music-playlist-card-name"
          title={`See the tracks in ${p.name}`}
          onClick={() => setTracksId(p.id)}
        >
          {p.isFavorites && <span className="music-playlist-card-heart" aria-hidden="true">♥</span>}
          {p.name}
        </button>
        <div className="music-playlist-card-sub" title={(p.trackTitles || []).join(", ")}>
          {p.count} track{p.count === 1 ? "" : "s"}
          {p.isFavorites ? " · only yours" : ""}
          {!p.isOwner && p.ownerName ? ` · shared by ${p.ownerName}` : ""}
          {p.isOwner && !p.isFavorites && p.sharedWith > 0 ? ` · shared with ${p.sharedWith}` : ""}
          {p.trackTitles && p.trackTitles.length > 0 ? ` · ${p.trackTitles.join(", ")}` : ""}
        </div>
        <div className="music-playlist-card-actions">
          <button className="music-playlist-btn" disabled={p.count === 0} onClick={() => play(p.id)}>
            ▶ Play
          </button>
          <button className="music-playlist-btn" disabled={p.count === 0} onClick={() => play(p.id, { shuffle: true })}>
            🔀 Shuffle
          </button>
          {/* Spelled out next to Play and Shuffle rather than left to the name click alone: this is
              the only control on the card that doesn't throw away what you are already listening
              to, and nothing about a card name advertises that. */}
          <button
            className="music-playlist-btn"
            disabled={p.count === 0}
            onClick={() => setTracksId(p.id)}
            title="Pick tracks to play or add to the queue"
          >
            ☰ Tracks
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
          No playlists yet. Add tracks to one from any album or song row in the library — or hit the
          ♥ in the player while something&apos;s playing, and a Favorites list appears here.
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

      <MusicPlaylistTracksModal
        open={tracksId != null}
        playlistId={tracksId}
        onClose={() => setTracksId(null)}
        onAddToPlaylist={(tracks, suggestedName) => {
          setPickerTracks(tracks);
          setPickerName(suggestedName || "");
        }}
      />

      <MusicPlaylistPickerModal
        open={pickerTracks != null}
        tracks={pickerTracks || []}
        defaultName={pickerName}
        onClose={() => setPickerTracks(null)}
        onDone={reload}
      />

      <MusicPlaylistManageModal
        open={manageId != null}
        playlistId={manageId}
        onClose={() => setManageId(null)}
        onChanged={reload}
      />
    </div>
  );
}
