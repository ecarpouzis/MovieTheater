import { useEffect, useRef, useState } from "react";
import { useHistory } from "react-router-dom";
import { useMusicPlayer } from "./MusicPlayerContext";
import MusicAlbumArt from "./MusicAlbumArt";
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

  if (!player || !player.current) return null;
  const { current, playing, error, queue, index } = player;
  const effectiveDuration = duration || current.durationSec || 0;

  return (
    <div className="music-miniplayer" data-testid="music-miniplayer">
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
  );
}

export default MusicMiniPlayer;
