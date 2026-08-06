import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { useMediaSession } from "../useMediaSession";
import MusicMiniPlayer from "./MusicMiniPlayer";

// ── The site's first persistent player (music-plan.md §2.6) ─────────────────
// Every video player dies on route change; music must not. The provider mounts ONCE in App.js
// above the route <Switch> and owns a single <audio> element for the app's lifetime, so playback
// survives navigation anywhere on the site. This is also the codebase's first React context —
// everything else stays props-and-URL; the context exists only because playback must outlive routes.
//
// Re-render discipline: the context value carries queue/index/playing + controls, NOT the playhead.
// Position ticks every ~250ms; anything that wants it (the mini-player's seek bar, a future
// visualizer/lyrics pane) subscribes to the <audio> element's own events via audioRef instead of
// dragging the whole tree through a render per tick.

const MusicPlayerContext = createContext(null);

export function useMusicPlayer() {
  return useContext(MusicPlayerContext);
}

// Can this track's bytes reach the <audio> element? Natively, or via the gateway's ffmpeg route
// when the server has transcoding switched on.
//
// Exported and pure because it is the single gate for playback: the queue filters on it and every
// row's disabled state derives from it. It used to be spelled out inline in four places as
// `!t.requiresTranscode`, which silently greyed out 92 tracks (85 .wma, 6 .aif, 1 .aiff) on a
// server that could have transcoded every one of them.
export function trackIsPlayable(track, canTranscode) {
  if (!track || track.missing) return false;
  return !track.requiresTranscode || !!canTranscode;
}

// Fisher-Yates over a COPY. Exported and pure so the shuffle order can be unit-tested without a
// player: "every track exactly once, in some order" is the property that matters, and a subtly
// biased or lossy shuffle is invisible by eye.
export function shuffled(tracks, rand = Math.random) {
  const out = (tracks || []).slice();
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

const VOLUME_KEY = "music.volume"; // arcade-style namespaced localStorage key
const LYRICS_KEY = "music.lyrics"; // on-screen lyrics toggle, remembered across routes/reloads
const QUEUE_KEY = "music.queue";   // { queue, index } — restored PAUSED on reload (§Phase 7)
const QUEUE_PERSIST_MAX = 500;     // bounds what a runaway "queue everything" can put in localStorage

export function MusicPlayerProvider({ children, enabled = true }) {
  // A queue entry: { id, title, artist, album, albumId, durationSec }
  const [queue, setQueue] = useState([]);
  const [index, setIndex] = useState(-1);
  const [playing, setPlaying] = useState(false);
  const [error, setError] = useState(null);
  // Visualizer on/off lives HERE, not on the Now Playing page: the play bar is on every route and
  // owns the toggle, so the two surfaces have to read one switch or they disagree the moment you
  // navigate. Which surface DRAWS it is decided by route (see MusicMiniPlayer) — never both, since
  // two butterchurn instances on one source is a second GL context for no gain.
  const [visualizerOn, setVisualizerOn] = useState(false);
  // Whether the gateway will transcode a non-native codec for us. Until this answers we assume it
  // WON'T, so a .wma track never looks playable and then fails — the optimistic direction is the
  // one that produces a dead click.
  const [canTranscode, setCanTranscode] = useState(false);
  // On-screen lyrics. Independent of the visualizer on purpose: with both on, the lyrics ride
  // OVER Butterchurn; with only lyrics on they get their own panel. Two switches compose into
  // three useful states without a third mode to keep in sync.
  const [lyricsOn, setLyricsOn] = useState(
    () => { try { return window.localStorage.getItem(LYRICS_KEY) === "1"; } catch { return false; } }
  );
  const audioRef = useRef(null);
  // Guards the async Start round-trip: a fast next/next must only apply the LAST track's URL.
  const loadSeqRef = useRef(0);
  // Web Audio graph for the visualizer (§2.8). Refs, not state: creating it must never re-render,
  // and it has to survive for the element's whole lifetime.
  const audioGraphRef = useRef(null);
  // A queue restored from localStorage loads its track but must NOT start playing — a reload that
  // suddenly blasts music is exactly the behaviour nobody wants (and browsers would refuse it
  // anyway, with no gesture yet). One-shot: the next track change plays normally.
  const suppressAutoplayRef = useRef(false);

  const current = index >= 0 && index < queue.length ? queue[index] : null;

  // Asked once per session. A failure leaves canTranscode false, which is the same behaviour the
  // player had before this endpoint existed.
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    MovieAPI.getMusicCapabilities()
      .then((r) => (r.ok ? r.json() : null))
      .then((c) => { if (!cancelled && c) setCanTranscode(!!c.transcodeEnabled); })
      .catch(() => { /* leave it false; nothing becomes newly broken */ });
    return () => { cancelled = true; };
  }, [enabled]);

  const isPlayable = useCallback((t) => trackIsPlayable(t, canTranscode), [canTranscode]);

  // Load + play whenever the current track changes. The signed URL comes from Stream/Start;
  // the <audio> element then streams straight off the gateway (Range requests, native decode).
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    if (!current) {
      audio.pause();
      audio.removeAttribute("src");
      return;
    }
    const seq = ++loadSeqRef.current;
    setError(null);
    MovieAPI.startMusicTrack(current.id)
      .then((r) => (r.ok ? r.json() : r.json().catch(() => ({})).then((b) => Promise.reject(b))))
      .then((data) => {
        if (seq !== loadSeqRef.current) return; // superseded by a newer pick
        audio.src = data.url;
        if (suppressAutoplayRef.current) {
          suppressAutoplayRef.current = false;
          setPlaying(false);
          return;
        }
        audio.play().catch(() => {
          // Autoplay refused (no user gesture yet) — leave it paused; the bar shows Play.
          setPlaying(false);
        });
      })
      .catch((body) => {
        if (seq !== loadSeqRef.current) return;
        setError(body?.message || "This track can't be played.");
        setPlaying(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id]);

  // Restore volume once the element exists.
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    const stored = parseFloat(window.localStorage.getItem(VOLUME_KEY));
    if (Number.isFinite(stored)) audio.volume = Math.min(Math.max(stored, 0), 1);
  }, []);

  // Restore the queue on mount, PAUSED (§Phase 7). Corrupt/legacy JSON is simply dropped — a bad
  // stored queue must never keep the player from starting.
  useEffect(() => {
    // No password on this account = no streaming (§3.1). Every /API/Music/* route enforces this
    // server-side via the StreamingUser policy; restoring a queue here would only produce a bar
    // that 401s on the first Stream/Start, so the player simply doesn't come back.
    if (!enabled) return;
    try {
      const raw = window.localStorage.getItem(QUEUE_KEY);
      if (!raw) return;
      const saved = JSON.parse(raw);
      const tracks = Array.isArray(saved?.queue) ? saved.queue.filter((t) => t && t.id != null) : [];
      if (tracks.length === 0) return;
      const at = Number.isInteger(saved.index) ? Math.min(Math.max(saved.index, 0), tracks.length - 1) : 0;
      suppressAutoplayRef.current = true;
      setQueue(tracks);
      setIndex(at);
      setPlaying(false);
    } catch {
      window.localStorage.removeItem(QUEUE_KEY);
    }
  }, [enabled]);

  // Persist every queue/position change. Cheap (a few KB) and synchronous, but bounded.
  useEffect(() => {
    try {
      if (queue.length === 0) window.localStorage.removeItem(QUEUE_KEY);
      else window.localStorage.setItem(QUEUE_KEY, JSON.stringify({
        queue: queue.slice(0, QUEUE_PERSIST_MAX),
        index: Math.min(index, QUEUE_PERSIST_MAX - 1),
      }));
    } catch {
      // Storage full/blocked (private mode): persistence is a convenience, never a hard failure.
    }
  }, [queue, index]);

  const playTracks = useCallback((tracks, startIndex = 0) => {
    if (!enabled) return;
    const playable = (tracks || []).filter(isPlayable);
    if (playable.length === 0) return;
    // startIndex referred to the ORIGINAL list; re-locate that track among the playable ones.
    const wanted = tracks[startIndex];
    const at = Math.max(0, playable.findIndex((t) => t.id === wanted?.id));
    setQueue(playable);
    setIndex(at);
    setPlaying(true);
  }, [enabled, isPlayable]);

  const enqueue = useCallback((tracks) => {
    if (!enabled) return;
    const playable = (tracks || []).filter(isPlayable);
    if (playable.length === 0) return;
    setQueue((q) => {
      if (q.length === 0) {
        setIndex(0);
        setPlaying(true);
      }
      return [...q, ...playable];
    });
  }, [enabled, isPlayable]);

  const next = useCallback(() => {
    setIndex((i) => {
      if (i + 1 < queue.length) return i + 1;
      setPlaying(false);
      return i; // end of queue: stay on the last track, paused
    });
  }, [queue.length]);

  const prev = useCallback(() => {
    const audio = audioRef.current;
    // Convention: past a few seconds in, "previous" restarts the track.
    if (audio && audio.currentTime > 4) {
      audio.currentTime = 0;
      return;
    }
    setIndex((i) => Math.max(0, i - 1));
  }, []);

  const toggle = useCallback(() => {
    const audio = audioRef.current;
    if (!audio || !audio.src) return;
    if (audio.paused) audio.play().catch(() => {});
    else audio.pause();
  }, []);

  const seek = useCallback((seconds) => {
    const audio = audioRef.current;
    if (audio && Number.isFinite(seconds)) audio.currentTime = seconds;
  }, []);

  const playAt = useCallback((i) => {
    setIndex((old) => {
      if (i === old) {
        const audio = audioRef.current;
        if (audio) audio.currentTime = 0;
        return old;
      }
      return i;
    });
    setPlaying(true);
  }, []);

  const removeAt = useCallback((i) => {
    setQueue((q) => {
      const nq = q.filter((_, j) => j !== i);
      setIndex((old) => (i < old ? old - 1 : Math.min(old, nq.length - 1)));
      return nq;
    });
  }, []);

  const stop = useCallback(() => {
    setQueue([]);
    setIndex(-1);
    setPlaying(false);
  }, []);

  // ── Web Audio graph (music-plan.md §2.8) ──────────────────────────────────
  // Created LAZILY, on the first visualizer open, and then kept forever:
  //   • createMediaElementSource permanently reroutes the element's audio through the graph, so the
  //     source MUST also be connected to the destination or playback goes silent for good;
  //   • a SECOND createMediaElementSource on the same element throws InvalidStateError — hence the
  //     ref guard, which makes this idempotent;
  //   • the AudioContext starts suspended and may only be resumed from a user gesture, so callers
  //     invoke this from the click handler that opens the visualizer.
  // The element carries crossOrigin="anonymous" and the gateway sends CORS headers, so the graph
  // isn't tainted and the analyser sees real samples rather than zeroes.
  const ensureAudioGraph = useCallback(() => {
    const audio = audioRef.current;
    if (!audio) return null;

    if (!audioGraphRef.current) {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) return null;
      try {
        const audioContext = new Ctx();
        const source = audioContext.createMediaElementSource(audio);
        const analyser = audioContext.createAnalyser();
        analyser.fftSize = 2048;
        source.connect(analyser);
        source.connect(audioContext.destination);
        audioGraphRef.current = { audioContext, source, analyser };
      } catch {
        return null; // no Web Audio here — the caller shows the fallback message
      }
    }

    const graph = audioGraphRef.current;
    if (graph.audioContext.state === "suspended") graph.audioContext.resume().catch(() => {});
    return graph;
  }, []);

  /// Must be called FROM the click handler: ensureAudioGraph resumes the AudioContext, and a
  /// browser only honours that inside a user gesture — an effect a tick later is not one.
  const toggleVisualizer = useCallback(() => {
    setVisualizerOn((on) => {
      if (!on) ensureAudioGraph();
      return !on;
    });
  }, [ensureAudioGraph]);

  const closeVisualizer = useCallback(() => setVisualizerOn(false), []);

  const toggleLyrics = useCallback(() => {
    setLyricsOn((on) => {
      try { window.localStorage.setItem(LYRICS_KEY, on ? "0" : "1"); } catch { /* private mode */ }
      return !on;
    });
  }, []);

  const closeLyrics = useCallback(() => {
    setLyricsOn(false);
    try { window.localStorage.setItem(LYRICS_KEY, "0"); } catch { /* private mode */ }
  }, []);

  /// Queue the same tracks in random order. Filters through isPlayable first so a shuffle can't
  /// stall on a format this server won't stream.
  const shuffleTracks = useCallback((tracks) => {
    if (!enabled) return;
    const playable = shuffled((tracks || []).filter(isPlayable));
    if (playable.length === 0) return;
    setQueue(playable);
    setIndex(0);
    setPlaying(true);
  }, [enabled, isPlayable]);

  const setVolume = useCallback((v) => {
    const audio = audioRef.current;
    if (audio) audio.volume = v;
    window.localStorage.setItem(VOLUME_KEY, String(v));
  }, []);

  // Keep `playing` truthful to the element (covers OS media keys, autoplay refusals, errors).
  const onPlay = useCallback(() => setPlaying(true), []);
  const onPause = useCallback(() => setPlaying(false), []);
  const onEnded = useCallback(() => next(), [next]);
  const onError = useCallback(() => {
    setError("Playback failed.");
    setPlaying(false);
  }, []);

  // OS lock-screen / media-key card. The shared hook only touches standard HTMLMediaElement
  // APIs, so the <audio> ref rides the videoRef parameter unchanged.
  useMediaSession({
    videoRef: audioRef,
    title: current?.title,
    subtitle: current ? [current.artist, current.album].filter(Boolean).join(" — ") : "",
    poster: null,
    actions: {
      play: toggle,
      pause: toggle,
      previoustrack: prev,
      nexttrack: next,
      seekto: (d) => d && Number.isFinite(d.seekTime) && seek(d.seekTime),
    },
  });

  const value = useMemo(
    () => ({ queue, index, current, playing, error, audioRef, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks }),
    [queue, index, current, playing, error, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks]
  );

  return (
    <MusicPlayerContext.Provider value={value}>
      {children}
      {/* crossOrigin: required for the future Web Audio graph (visualizer §2.8) — a
          MediaElementAudioSourceNode over a CORS-tainted source outputs silence. */}
      <audio
        ref={audioRef}
        crossOrigin="anonymous"
        onPlay={onPlay}
        onPause={onPause}
        onEnded={onEnded}
        onError={onError}
      />
      {enabled && <MusicMiniPlayer />}
    </MusicPlayerContext.Provider>
  );
}
