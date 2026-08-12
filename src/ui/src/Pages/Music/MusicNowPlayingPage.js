import { useEffect, useState } from "react";
import { useHistory } from "react-router-dom";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import MusicLyricsPane from "../../Music/MusicLyricsPane";
import MusicVisualizer from "../../Music/MusicVisualizer";
import MusicLyricsSettingsButton, { LYRICS_DEFAULTS } from "../../Music/MusicLyricsSettings";
import "./MusicNowPlaying.css";

// ── Now Playing (music-plan.md §2.6/§2.7/§2.8) ──────────────────────────────
// The full-player view: big art (or the visualizer in its place), the queue, and a lyrics pane that
// follows the audio. The playhead is read off the <audio> element via player.audioRef and kept in
// THIS component's state — deliberately not in the context, which would re-render the whole app
// four times a second.

function formatTime(sec) {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s < 10 ? "0" : ""}${s}`;
}

export default function MusicNowPlayingPage({ userData }) {
  const player = useMusicPlayer();
  const history = useHistory();
  const [position, setPosition] = useState(0);
  const [duration, setDuration] = useState(0);
  // Shared with the play bar's button (music-plan.md §2.8) — the switch lives in the player context
  // so navigating between here and anywhere else doesn't silently flip the visualizer off.
  const visualizerOn = !!player?.visualizerOn;

  const audio = player?.audioRef?.current;
  const current = player?.current;

  // Through the player's mapping, not off the element: under the MSE engine the element clock counts
  // the whole queue, and lyrics are the consumer that notices a wrong offset first — a line at a
  // time (music-mse-plan.md §Phase 3).
  const trackTime = player?.trackTime;
  useEffect(() => {
    if (!audio) return undefined;
    const read = () => (trackTime ? trackTime() : { position: audio.currentTime || 0, duration: Number.isFinite(audio.duration) ? audio.duration : 0 });
    const onTime = () => setPosition(read().position);
    const onDuration = () => setDuration(read().duration);
    onTime();
    onDuration();
    audio.addEventListener("timeupdate", onTime);
    audio.addEventListener("durationchange", onDuration);
    audio.addEventListener("loadedmetadata", onDuration);
    return () => {
      audio.removeEventListener("timeupdate", onTime);
      audio.removeEventListener("durationchange", onDuration);
      audio.removeEventListener("loadedmetadata", onDuration);
    };
  }, [audio, trackTime]);

  // Streaming is password-only (§3.1). The route was previously reachable by URL for anyone —
  // harmless (the API 401s) but confusing; say so plainly instead.
  if (!userData?.hasPassword) {
    return (
      <div className="music-np music-np--idle">
        <h2>Music needs a password-protected account</h2>
        <p>Ask the site admin to add a password to your account to listen.</p>
      </div>
    );
  }

  if (!player || !current) {
    return (
      <div className="music-np music-np--idle">
        <h2>Nothing playing</h2>
        <p>Pick an album or a song to start listening.</p>
        <button className="music-playlist-btn" onClick={() => history.push("/music")}>Browse music</button>
      </div>
    );
  }

  const effectiveDuration = duration || current.durationSec || 0;
  const favorited = !!player.isFavorite?.(current.id);
  const lyricsSettings = player.lyricsSettings || LYRICS_DEFAULTS;
  // This page always shows the lyrics column, so the options are always live here — unlike the play
  // bar, where they appear only while the Lyrics switch is on. Two hosts, two surfaces: the
  // visualizer strip is dark over the canvas, the heading takes the content-area tokens.
  const vizLyricsTools = (
    <MusicLyricsSettingsButton settings={lyricsSettings} onChange={player.setLyricsSetting} tone="dark" />
  );
  const paneLyricsTools = (
    <MusicLyricsSettingsButton settings={lyricsSettings} onChange={player.setLyricsSetting} tone="page" />
  );

  return (
    <div className="music-np" data-testid="music-now-playing">
      <div className="music-np-stage">
        <div className="music-np-artwrap">
          {visualizerOn ? (
            <MusicVisualizer player={player} onClose={player.closeVisualizer} lyricsTools={vizLyricsTools} />
          ) : (
            <MusicAlbumArt
              albumId={current.albumId}
              hasArt={current.albumId != null}
              title={current.album || current.title}
              thumb={false}
              className="music-np-art"
            />
          )}
        </div>

        <div className="music-np-info">
          <h1 className="music-np-title" title={current.title}>{current.title}</h1>
          <div className="music-np-sub">
            {[current.artist, current.album].filter(Boolean).join(" — ")}
          </div>
          <div className="music-np-times">
            {formatTime(position)} / {formatTime(effectiveDuration)}
          </div>
          <div className="music-np-actions">
            {/* The same switch the play bar's heart flips — both read player.favoriteIds, so
                favoriting here fills the bar's heart at once and vice versa. */}
            <button
              className={`music-playlist-btn music-np-heart${favorited ? " music-np-heart--on" : ""}`}
              onClick={() => player.toggleFavorite(current.id)}
              aria-pressed={favorited}
              data-testid="music-favorite-toggle-np"
            >
              {favorited ? "♥ Favorited" : "♡ Favorite"}
            </button>
            <button className="music-playlist-btn" onClick={player.prev}>⏮ Prev</button>
            <button className="music-playlist-btn" onClick={player.toggle}>
              {player.playing ? "⏸ Pause" : "▶ Play"}
            </button>
            <button className="music-playlist-btn" onClick={player.next}>⏭ Next</button>
            {/* toggleVisualizer resumes the AudioContext inside this gesture — a browser only
                honours that from a user gesture, never from an effect a tick later. */}
            <button
              className={`music-playlist-btn music-np-vizbtn${visualizerOn ? " music-np-vizbtn--on" : ""}`}
              onClick={player.toggleVisualizer}
              aria-pressed={visualizerOn}
              data-testid="music-visualizer-toggle"
            >
              {visualizerOn ? "◼ Hide visualizer" : "◉ Show visualizer"}
            </button>
          </div>

          <h3 className="music-np-subhead">Queue</h3>
          <div className="music-np-queue">
            {player.queue.map((t, i) => (
              <button
                key={`${t.id}-${i}`}
                className={`music-np-queue-row${i === player.index ? " music-np-queue-row--current" : ""}`}
                onClick={() => player.playAt(i)}
              >
                <span className="music-np-queue-no">{i === player.index ? "▶" : i + 1}</span>
                <span className="music-np-queue-title" title={t.title}>{t.title}</span>
                <span className="music-np-queue-artist">{t.artist}</span>
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="music-np-lyricswrap">
        {/* The same options button as the visualizer strip, so the column is adjustable on this page
            even with the visualizer switched off — otherwise the only way to reach the settings
            would be to turn Butterchurn on. */}
        <h3 className="music-np-subhead music-np-subhead--tools">
          Lyrics
          {paneLyricsTools}
        </h3>
        <MusicLyricsPane
          trackId={current.id}
          position={position}
          settings={lyricsSettings}
          playing={!!player.playing}
        />
      </div>
    </div>
  );
}
