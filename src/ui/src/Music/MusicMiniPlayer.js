import { useEffect, useRef, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { useMusicPlayer } from "./MusicPlayerContext";
import MusicAlbumArt from "./MusicAlbumArt";
import MusicVisualizer from "./MusicVisualizer";
import MusicLyricsPane from "./MusicLyricsPane";
import MusicLyricsSettingsButton, { LYRICS_DEFAULTS } from "./MusicLyricsSettings";
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
  // Whether the full error text is open in its sheet above the bar. The error line is one
  // ellipsised row on a phone, and what the ellipsis eats is the diagnosis — the player goes to
  // real trouble to say WHICH of a dropped connection, a refused token and an unplayable file this
  // was, and a report of "an error that said something…" is this feature having not existed.
  const [errorOpen, setErrorOpen] = useState(false);
  const playerError = player?.error;
  useEffect(() => {
    if (!playerError) setErrorOpen(false); // recovered: the sheet must not outlive its message
  }, [playerError]);

  const audioRef = player?.audioRef;
  const playing = !!player?.playing;
  const currentId = player?.current?.id;

  // Subscribe to the element, not to a value read during render: on the first render after a track
  // is picked, audioRef.current can still be null (the ref attaches in the same commit), and a
  // `[audio]` dependency read at render time would never see it fill in. Re-running on the track id
  // guarantees a rebind for the life of the bar.
  // Read through the player's mapping, never off the element. Under the MSE engine the element's
  // clock counts the WHOLE QUEUE (one SourceBuffer, all tracks back to back), so `audio.currentTime`
  // is 43-minute nonsense for a bar that is drawing one song; `trackTime()` is the same number on
  // the deck path and the track-relative one under the engine (music-mse-plan.md §Phase 3).
  const trackTime = player?.trackTime;
  useEffect(() => {
    const audio = audioRef?.current;
    if (!audio) return undefined;
    const read = () => (trackTime ? trackTime() : { position: audio.currentTime || 0, duration: Number.isFinite(audio.duration) ? audio.duration : 0 });
    const onTime = () => {
      if (!draggingRef.current) setPosition(read().position);
      setDuration(read().duration);
    };
    const onDuration = () => setDuration(read().duration);
    onTime();
    onDuration(); // metadata may already be in before we got here
    audio.addEventListener("timeupdate", onTime);
    audio.addEventListener("durationchange", onDuration);
    audio.addEventListener("loadedmetadata", onDuration);
    return () => {
      audio.removeEventListener("timeupdate", onTime);
      audio.removeEventListener("durationchange", onDuration);
      audio.removeEventListener("loadedmetadata", onDuration);
    };
  }, [audioRef, currentId, trackTime]);

  // Safety net: while the element says it is playing, the bar must advance. timeupdate is the cheap
  // path, but it is not guaranteed — a throttled/backgrounded tab, a rebind that lost the race, or a
  // stalled event stream all end with a bar frozen under music that is audibly still going. Polling
  // to the same value is a no-op render (setState bails on Object.is), so this costs nothing when
  // the events are working.
  useEffect(() => {
    if (!playing) return undefined;
    const id = setInterval(() => {
      const audio = audioRef?.current;
      if (!audio || draggingRef.current) return;
      const read = trackTime ? trackTime() : { position: audio.currentTime || 0, duration: 0 };
      setPosition(read.position);
      if (read.duration) setDuration(read.duration);
    }, 500);
    return () => clearInterval(id);
  }, [playing, audioRef, trackTime]);

  // The drag latch must be released by something that ALWAYS fires. On a phone it frequently isn't
  // pointerup: the browser claims the gesture for a scroll and sends pointercancel instead, and the
  // element-level handler alone then leaves draggingRef stuck true — every subsequent timeupdate is
  // discarded and the bar sits at whatever second the touch happened on, forever.
  useEffect(() => {
    const end = () => { draggingRef.current = false; };
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
    return () => {
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
    };
  }, []);

  // The overlay stops exactly where the bar starts; the bar's height is content-driven, so measure
  // it rather than hard-coding a number that drifts the moment the layout changes.
  // It also publishes itself as a CSS variable, because the bar is not the only surface that has to
  // stop where it starts: any full-height overlay (the visualizer, the lyrics panel, the album modal
  // on a phone) has to leave the bar reachable, and a hard-coded 72px is wrong the moment this
  // wraps to three rows.
  useEffect(() => {
    const publish = (h) => document.documentElement.style.setProperty("--music-miniplayer-height", `${h}px`);
    const el = barRef.current;
    if (!el) {
      setBarHeight(0);
      publish(0);
      return undefined;
    }
    const measure = () => {
      setBarHeight(el.offsetHeight);
      publish(el.offsetHeight);
    };
    measure();
    if (typeof ResizeObserver === "undefined") return () => publish(0);
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => {
      observer.disconnect();
      publish(0);
    };
  }, [player?.current?.id]);

  // The visualizer overlay covers the whole viewport, so the page behind it must not scroll while
  // it's up. touch-action on the canvas stops the swipe itself (MusicVisualizer.css); this stops
  // everything else — the wheel, the keyboard, and a fling already in flight when it opened.
  const vizOverlayOpen = !!player?.current && !!player?.visualizerOn && !onNowPlaying;
  useEffect(() => {
    if (!vizOverlayOpen) return undefined;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => { document.body.style.overflow = previous; };
  }, [vizOverlayOpen]);

  if (!player || !player.current) return null;
  const { current, error, buffering, queue, index, visualizerOn, lyricsOn } = player;
  const effectiveDuration = duration || current.durationSec || 0;
  const favorited = !!player.isFavorite?.(current.id);
  const lyricsSettings = player.lyricsSettings || LYRICS_DEFAULTS;
  // Offered only while lyrics are actually on screen — a panel of lyrics controls on a bar with no
  // lyrics under it is the same dead control the Lyrics button avoids on Now Playing.
  const lyricsTools = lyricsOn ? (
    <MusicLyricsSettingsButton settings={lyricsSettings} onChange={player.setLyricsSetting} />
  ) : null;

  return (
    <>
    {visualizerOn && !onNowPlaying && (
      <div
        className="music-viz-overlay"
        style={barHeight ? { bottom: barHeight } : undefined}
        data-testid="music-visualizer-overlay"
      >
        <MusicVisualizer player={player} onClose={player.closeVisualizer} lyricsTools={lyricsTools} />
        {/* Lyrics ON TOP of Butterchurn when both switches are on — one overlay, not two
            fighting for the same space. */}
        {lyricsOn && (
          <div
            className={`music-lyrics-over-viz${lyricsSettings.scrim ? "" : " music-lyrics-over-viz--plain"}`}
            data-testid="music-lyrics-over-visualizer"
          >
            <MusicLyricsPane
              trackId={current.id}
              position={position}
              variant="overlay"
              settings={lyricsSettings}
              playing={playing}
            />
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
        {/* No visualizer strip to hang them off in this mode, so the options sit with the close
            button — the panel's only other control. */}
        <div className="music-lyrics-overlay-tools">
          <MusicLyricsSettingsButton settings={lyricsSettings} onChange={player.setLyricsSetting} />
          <button className="music-lyrics-overlay-close" onClick={player.closeLyrics} aria-label="Hide lyrics">✕</button>
        </div>
        <MusicLyricsPane
          trackId={current.id}
          position={position}
          variant="overlay"
          settings={lyricsSettings}
          playing={playing}
        />
      </div>
    )}
    {/* The full error text, wrapped, above the bar. A sheet rather than un-truncating the line in
        place: the bar's rows are load-bearing layout on a phone, and a three-line sentence in the
        artist slot would shove the transport off it. */}
    {error && errorOpen && (
      <div
        className="music-miniplayer-error-sheet"
        style={barHeight ? { bottom: barHeight } : undefined}
        onClick={() => setErrorOpen(false)}
        role="button"
        tabIndex={0}
        data-testid="music-error-sheet"
      >
        {error}
        <div className="music-miniplayer-error-sheet-hint">Tap to dismiss</div>
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
            {/* A real <button>, and stopPropagation is load-bearing: the whole info block is a
                click target that navigates to Now Playing, which would swallow the tap and hide
                the one sentence the tap was for. */}
            {error ? (
              <button
                className="music-miniplayer-error"
                onClick={(e) => { e.stopPropagation(); setErrorOpen((o) => !o); }}
                aria-expanded={errorOpen}
                title="Show the full message"
                data-testid="music-error-line"
              >
                {error}
              </button>
            ) : buffering ? (
              // A big track is being fetched whole before it can play (it is over Chrome's media
              // buffer cap, so streaming it would break mid-song). On a WAN uplink that is a real
              // wait, and a bar that just sits there reads as broken — so it says what it is doing.
              <span className="music-miniplayer-buffering" data-testid="music-buffering">
                <span className="music-miniplayer-buffering-dot" />
                Buffering…
              </span>
            ) : [current.artist, current.album].filter(Boolean).join(" — ")}
          </div>
        </div>
      </div>

      {/* A SIBLING of the info block, not a child: the info block is itself a button that navigates
          to Now Playing, and a button inside a button is invalid markup that swallows this click.
          Sits here — right after the title, Spotify's placement — because the heart is about the
          track you can see, not about the transport. */}
      <button
        className={`music-miniplayer-btn music-miniplayer-heart${favorited ? " music-miniplayer-heart--on" : ""}`}
        onClick={() => player.toggleFavorite(current.id)}
        aria-pressed={favorited}
        aria-label={favorited ? "Remove from Favorites" : "Add to Favorites"}
        title={favorited ? "In your Favorites" : "Add to your Favorites"}
        data-testid="music-favorite-toggle"
      >
        {favorited ? "♥" : "♡"}
      </button>

      <div className="music-miniplayer-transport">
        <button className="music-miniplayer-btn" onClick={player.prev} aria-label="Previous track">⏮</button>
        <button className="music-miniplayer-btn music-miniplayer-play" onClick={player.toggle} aria-label={playing ? "Pause" : "Play"}>
          {playing ? "⏸" : "▶"}
        </button>
        <button className="music-miniplayer-btn" onClick={player.next} aria-label="Next track">⏭</button>
      </div>

      <div className={`music-miniplayer-seek${buffering ? " music-miniplayer-seek--buffering" : ""}`}>
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
        {/* Not on Now Playing: that page shows the lyrics column unconditionally and both overlays
            are suppressed there, so this button would toggle a switch with nothing to show for it —
            a dead control on the one page most likely to be open when you want lyrics. */}
        {!onNowPlaying && (
          <button
            className={`music-miniplayer-viz${lyricsOn ? " music-miniplayer-viz--on" : ""}`}
            onClick={player.toggleLyrics}
            title={lyricsOn ? "Hide lyrics" : "Show lyrics while this plays"}
            aria-pressed={lyricsOn}
          >
            <span className="music-miniplayer-viz-icon" aria-hidden="true">♪</span>
            <span className="music-miniplayer-viz-label">{lyricsOn ? "Hide" : "Lyrics"}</span>
          </button>
        )}
        <button
          className={`music-miniplayer-btn${queueOpen ? " music-miniplayer-btn--active" : ""}`}
          onClick={() => setQueueOpen((o) => !o)}
          aria-label="Queue"
          title={`Queue (${queue.length})`}
        >
          ☰
        </button>
        {/* Closing the player from the Now Playing page would otherwise strand you on that page's
            "Nothing playing" screen — the one surface that exists only because there IS a track.
            Dismissing the music should hand you back to the library, not to a dead end. */}
        <button
          className="music-miniplayer-btn music-miniplayer-close"
          onClick={() => {
            player.stop();
            if (onNowPlaying) history.replace("/music");
          }}
          aria-label="Close player"
          title="Close"
        >
          ✕
        </button>
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
