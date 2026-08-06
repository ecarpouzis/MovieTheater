import { useEffect, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { useMusicPlayer } from "./MusicPlayerContext";
import MusicAlbumArt from "./MusicAlbumArt";
import MusicVisualizer from "./MusicVisualizer";
import MusicLyricsPane from "./MusicLyricsPane";
import MusicPlaylistPickerModal from "../Pages/Music/MusicPlaylistPickerModal";
import "./MusicMiniPlayer.css";

// ── The persistent bottom bar (music-plan.md §2.6) ──────────────────────────
// Rendered by the provider whenever a track is loaded, on EVERY page of the site. Subscribes to the
// <audio> element's own events for the playhead so only this bar re-renders per tick — the context
// (and the rest of the app) never sees position changes.

function formatTime(sec) {
  if (!Number.isFinite(sec) || sec < 0) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s < 10 ? "0" : ""}${s}`;
}

function MusicMiniPlayer() {
  const player = useMusicPlayer();
  const history = useHistory();
  const location = useLocation();
  // The bar is on every route, so it also OWNS the visualizer overlay — except on Now Playing,
  // which shows the visualizer inline in its art slot. Exactly one of the two mounts it: a second
  // butterchurn instance would be a whole extra WebGL context rendering the same audio.
  const onNowPlaying = location.pathname.startsWith("/music/now-playing");
  const barRef = useRef(null);
  const [barHeight, setBarHeight] = useState(0);
  const [position, setPosition] = useState(0);
  const [duration, setDuration] = useState(0);
  const [volume, setVolumeState] = useState(() => {
    const stored = parseFloat(window.localStorage.getItem("music.volume"));
    return Number.isFinite(stored) ? stored : 1;
  });
  // While the user drags the seek bar, the drag position wins over timeupdate ticks.
  const draggingRef = useRef(false);
  const [queueOpen, setQueueOpen] = useState(false);
  // The bar lives outside /music, so it carries its own playlist picker (music-plan.md Phase 3)
  // rather than reaching for the library page's one.
  const [pickerTracks, setPickerTracks] = useState(null);

  const audio = player?.audioRef?.current;

  useEffect(() => {
    if (!audio) return undefined;
    const onTime = () => {
      if (!draggingRef.current) setPosition(audio.currentTime || 0);
    };
    const onDuration = () => setDuration(Number.isFinite(audio.duration) ? audio.duration : 0);
    audio.addEventListener("timeupdate", onTime);
    audio.addEventListener("durationchange", onDuration);
    audio.addEventListener("loadedmetadata", onDuration);
    return () => {
      audio.removeEventListener("timeupdate", onTime);
      audio.removeEventListener("durationchange", onDuration);
      audio.removeEventListener("loadedmetadata", onDuration);
    };
  }, [audio]);

  // The overlay stops exactly where the bar starts; the bar's height is content-driven, so measure
  // it rather than hard-coding a number that drifts the moment the layout changes.
  useEffect(() => {
    const el = barRef.current;
    if (!el) return undefined;
    const measure = () => setBarHeight(el.offsetHeight);
    measure();
    if (typeof ResizeObserver === "undefined") return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [player?.current?.id]);

  if (!player || !player.current) return null;
  const { current, playing, error, queue, index, visualizerOn, lyricsOn } = player;
  const effectiveDuration = duration || current.durationSec || 0;

  return (
    <>
    {visualizerOn && !onNowPlaying && (
      <div
        className="music-viz-overlay"
        style={barHeight ? { bottom: barHeight } : undefined}
        data-testid="music-visualizer-overlay"
      >
        <MusicVisualizer player={player} onClose={player.closeVisualizer} />
        {/* Lyrics ON TOP of Butterchurn when both switches are on — one overlay, not two
            fighting for the same space. */}
        {lyricsOn && (
          <div className="music-lyrics-over-viz" data-testid="music-lyrics-over-visualizer">
            <MusicLyricsPane trackId={current.id} position={position} variant="overlay" />
          </div>
        )}
      </div>
    )}
    {/* Lyrics with no visualizer: their own panel above the bar. */}
    {lyricsOn && !visualizerOn && !onNowPlaying && (
      <div
        className="music-lyrics-overlay"
        style={barHeight ? { bottom: barHeight } : undefined}
        data-testid="music-lyrics-overlay"
      >
        <button className="music-lyrics-overlay-close" onClick={player.closeLyrics} aria-label="Hide lyrics">✕</button>
        <MusicLyricsPane trackId={current.id} position={position} variant="overlay" />
      </div>
    )}
    <div className="music-miniplayer" data-testid="music-miniplayer" ref={barRef}>
      {/* Clicking the track info opens the full player; with nothing loaded it falls back to the
          library (the bar doesn't render then, but the fallback keeps the intent explicit). */}
      <div
        className="music-miniplayer-info"
        onClick={() => history.push(current ? "/music/now-playing" : "/music")}
        role="button"
        tabIndex={0}
      >
        {/* Queue entries don't carry the album's hasArt flag (they come from four different
            producers), so the bar just ASKS for the art whenever there's an album and falls back to
            the initials tile on a 404 — the image route answers misses cheaply and doesn't cache them. */}
        <MusicAlbumArt
          albumId={current.albumId}
          hasArt={current.albumId != null}
          title={current.album || current.title}
          className="music-miniplayer-note"
        />
        <div className="music-miniplayer-titles">
          <div className="music-miniplayer-title" title={current.title}>{current.title}</div>
          <div className="music-miniplayer-artist" title={current.artist}>
            {error ? <span className="music-miniplayer-error">{error}</span> : [current.artist, current.album].filter(Boolean).join(" — ")}
          </div>
        </div>
      </div>

      <div className="music-miniplayer-transport">
        <button className="music-miniplayer-btn" onClick={player.prev} aria-label="Previous track">⏮</button>
        <button className="music-miniplayer-btn music-miniplayer-play" onClick={player.toggle} aria-label={playing ? "Pause" : "Play"}>
          {playing ? "⏸" : "▶"}
        </button>
        <button className="music-miniplayer-btn" onClick={player.next} aria-label="Next track">⏭</button>
      </div>

      <div className="music-miniplayer-seek">
        <span className="music-miniplayer-time">{formatTime(position)}</span>
        <input
          type="range"
          min={0}
          max={effectiveDuration || 1}
          step="any"
          value={Math.min(position, effectiveDuration || 1)}
          onPointerDown={() => { draggingRef.current = true; }}
          onPointerUp={() => { draggingRef.current = false; }}
          onChange={(e) => {
            const v = parseFloat(e.target.value);
            setPosition(v);
            player.seek(v);
          }}
          aria-label="Seek"
        />
        <span className="music-miniplayer-time">{formatTime(effectiveDuration)}</span>
      </div>

      <div className="music-miniplayer-right">
        <input
          className="music-miniplayer-volume"
          type="range"
          min={0}
          max={1}
          step="any"
          value={volume}
          onChange={(e) => {
            const v = parseFloat(e.target.value);
            setVolumeState(v);
            player.setVolume(v);
          }}
          aria-label="Volume"
        />
        {/* Deliberately NOT one of the ghost icon buttons: this is the bar's feature button, so it
            carries a label and the accent fill and reads as the one thing worth clicking here. */}
        <button
          className={`music-miniplayer-viz${visualizerOn ? " music-miniplayer-viz--on" : ""}`}
          onClick={player.toggleVisualizer}
          aria-pressed={visualizerOn}
          title={visualizerOn ? "Hide visualizer" : "Show visualizer"}
          data-testid="music-visualizer-toggle-bar"
        >
          <span className="music-miniplayer-viz-icon" aria-hidden="true">◉</span>
          <span className="music-miniplayer-viz-label">{visualizerOn ? "Hide" : "Visualizer"}</span>
        </button>
        <button
          className={`music-miniplayer-viz${lyricsOn ? " music-miniplayer-viz--on" : ""}`}
          onClick={player.toggleLyrics}
          title={lyricsOn ? "Hide lyrics" : "Show lyrics while this plays"}
          aria-pressed={lyricsOn}
        >
          <span className="music-miniplayer-viz-icon" aria-hidden="true">♪</span>
          <span className="music-miniplayer-viz-label">{lyricsOn ? "Hide" : "Lyrics"}</span>
        </button>
        <button
          className={`music-miniplayer-btn${queueOpen ? " music-miniplayer-btn--active" : ""}`}
          onClick={() => setQueueOpen((o) => !o)}
          aria-label="Queue"
          title={`Queue (${queue.length})`}
        >
          ☰
        </button>
        <button className="music-miniplayer-btn" onClick={player.stop} aria-label="Close player" title="Close">✕</button>
      </div>

      {queueOpen && (
        <div className="music-miniplayer-queue">
          <div className="music-miniplayer-queue-head">
            <span>Queue · {queue.length}</span>
            <button
              className="music-miniplayer-queue-save"
              onClick={() => setPickerTracks(queue.map((t) => ({ id: t.id, title: t.title })))}
              disabled={queue.length === 0}
            >
              ＋ Save as playlist
            </button>
          </div>
          {queue.map((t, i) => (
            <div
              key={`${t.id}-${i}`}
              className={`music-miniplayer-queue-row${i === index ? " music-miniplayer-queue-row--current" : ""}`}
            >
              <button className="music-miniplayer-queue-play" onClick={() => player.playAt(i)} title={t.title}>
                <span className="music-miniplayer-queue-no">{i === index ? "▶" : i + 1}</span>
                <span className="music-miniplayer-queue-title">{t.title}</span>
                <span className="music-miniplayer-queue-artist">{t.artist}</span>
              </button>
              <button
                className="music-miniplayer-queue-remove"
                onClick={() => setPickerTracks([{ id: t.id, title: t.title }])}
                aria-label="Add to playlist"
                title="Add to playlist"
              >
                ＋
              </button>
              <button className="music-miniplayer-queue-remove" onClick={() => player.removeAt(i)} aria-label="Remove from queue">✕</button>
            </div>
          ))}
        </div>
      )}

      <MusicPlaylistPickerModal
        open={pickerTracks != null}
        tracks={pickerTracks || []}
        defaultName={pickerTracks && pickerTracks.length === 1 ? pickerTracks[0].title : (current?.album || "")}
        onClose={() => setPickerTracks(null)}
      />
    </div>
    </>
  );
}

export default MusicMiniPlayer;
