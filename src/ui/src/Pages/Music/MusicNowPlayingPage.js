import { useEffect, useMemo, useRef, useState } from "react";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { useMusicPlayer } from "../../Music/MusicPlayerContext";
import MusicAlbumArt from "../../Music/MusicAlbumArt";
import { parseLrc, activeLineIndex } from "../../Music/lrc";
import MusicVisualizer from "../../Music/MusicVisualizer";
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

function LyricsPane({ trackId, position }) {
  const [state, setState] = useState({ status: "loading" });
  const containerRef = useRef(null);
  const activeRef = useRef(null);

  useEffect(() => {
    if (trackId == null) {
      setState({ status: "empty" });
      return undefined;
    }
    let alive = true;
    setState({ status: "loading" });
    MovieAPI.getMusicTrackLyrics(trackId)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => alive && setState({ status: "ready", ...data }))
      .catch(() => alive && setState({ status: "empty" }));
    return () => { alive = false; };
  }, [trackId]);

  const lines = useMemo(() => parseLrc(state.syncedLrc), [state.syncedLrc]);
  const active = activeLineIndex(lines, position);

  // Auto-scroll inside the pane only — scrollIntoView would drag the whole page.
  useEffect(() => {
    const container = containerRef.current;
    const el = activeRef.current;
    if (!container || !el) return;
    const target = el.offsetTop - container.clientHeight / 2 + el.clientHeight / 2;
    container.scrollTo({ top: Math.max(0, target), behavior: "smooth" });
  }, [active]);

  if (state.status === "loading") return <div className="music-np-lyrics-empty">Loading lyrics…</div>;
  if (state.status === "empty")
    return (
      <div className="music-np-lyrics-empty">
        No lyrics for this track yet.
        <span>Lyrics come from the file's own tags, a sidecar .lrc, or LRCLIB.</span>
      </div>
    );

  if (lines.length > 0) {
    return (
      <div className="music-np-lyrics music-np-lyrics--synced" ref={containerRef} data-testid="music-lyrics-synced">
        {lines.map((line, i) => (
          <p
            key={`${line.time}-${i}`}
            ref={i === active ? activeRef : null}
            className={`music-np-line${i === active ? " music-np-line--active" : ""}`}
          >
            {line.text || "♪"}
          </p>
        ))}
      </div>
    );
  }

  return (
    <div className="music-np-lyrics" ref={containerRef} data-testid="music-lyrics-plain">
      <pre className="music-np-plain">{state.plainText || state.syncedLrc}</pre>
    </div>
  );
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

  useEffect(() => {
    if (!audio) return undefined;
    const onTime = () => setPosition(audio.currentTime || 0);
    const onDuration = () => setDuration(Number.isFinite(audio.duration) ? audio.duration : 0);
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
  }, [audio]);

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

  return (
    <div className="music-np" data-testid="music-now-playing">
      <div className="music-np-stage">
        <div className="music-np-artwrap">
          {visualizerOn ? (
            <MusicVisualizer player={player} onClose={player.closeVisualizer} />
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
        <h3 className="music-np-subhead">Lyrics</h3>
        <LyricsPane trackId={current.id} position={position} />
      </div>
    </div>
  );
}
