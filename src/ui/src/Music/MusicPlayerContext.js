import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { useMediaSession } from "../useMediaSession";
import MusicMiniPlayer from "./MusicMiniPlayer";
import { LYRICS_DEFAULTS, normalizeLyricsSettings } from "./MusicLyricsSettings";

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

/// One favorited-id set, plus or minus one track. Exported and pure so the optimistic write and its
/// rollback are literally the same function called with opposite intents — hand-written inverses are
/// how a failed request ends up leaving a filled heart over a favorite the server never took.
/// Always returns a NEW Set: mutating in place keeps the reference identical, and React would then
/// re-render nothing.
export function withFavorite(ids, trackId, favorite) {
  const next = new Set(ids);
  if (favorite) next.add(trackId);
  else next.delete(trackId);
  return next;
}

// ── Failure recovery (a stream that dies mid-song) ──────────────────────────
// A transient failure used to end the listening session outright: the element errored, the bar said
// "Playback failed.", and nothing retried, resumed or advanced. A stall was worse — nothing listened
// for one at all, so the audio simply stopped making progress while the UI went on claiming it was
// playing. Both are now recovered from, under a bound that always terminates.
const RECOVERIES_PER_TRACK = 2;        // re-mints of the URL before this track is written off
const RECOVERY_RESET_SEC = 30;         // …forgiven after this much uninterrupted progress
const MAX_CONSECUTIVE_FAILURES = 3;    // tracks written off back-to-back before we stop entirely
const STALL_GRACE_MS = 12000;          // silence from timeupdate that is no longer just buffering
const STALL_POLL_MS = 2000;
// Resume a shade EARLIER than where it died: the last second was being decoded when the stream
// went, and landing exactly on it invites the same failure.
const RESUME_REWIND_SEC = 1;

/// Ask the stream host what it actually said. "Playback failed." is what the player showed for every
/// distinct prod outage this vertical has had — a gateway that couldn't SEE the files, an expired or
/// mis-signed token, a wrong ACAO — and told nobody which. The element itself won't say (a media
/// error carries no status), but a HEAD against the same URL will, and the answers are diagnostic:
/// 404 means the gateway can't reach the file, 403 means the token was refused, and a thrown fetch
/// means the host didn't answer at all or its CORS headers are wrong (the element sets
/// crossOrigin=anonymous, so a bad ACAO kills playback outright).
export async function diagnoseStreamUrl(url, doFetch = fetch) {
  if (!url) return null;
  try {
    const r = await doFetch(url, { method: "HEAD" });
    if (r.status === 404) return "the stream host can't find the file (404)";
    if (r.status === 403) return "the stream host refused the token (403)";
    if (!r.ok) return `the stream host answered ${r.status}`;
    return "the stream host answered, but the browser couldn't play it";
  } catch {
    return "the stream host didn't answer (it may be down, or its CORS headers are wrong)";
  }
}

/// What to do about a track that just failed. Pure, exported and unit-tested because it is the part
/// that must terminate: every path either makes progress (retry with a fresh URL, or move on) or
/// stops, and no input combination loops. "stop" is the backstop for a dead gateway — without it, a
/// server that fails everything would walk the whole queue at speed, briefly, and call it playback.
/// How wide to open the Web Audio output for a track of `channels` on a device that can emit at most
/// `maxChannelCount`. Pulled out as a pure function because the rule is counter-intuitive enough to
/// be "simplified" into a bug — see applyOutputChannels for why widening unconditionally is wrong.
export function outputChannelCount(channels, maxChannelCount) {
  const max = maxChannelCount > 0 ? maxChannelCount : 2;
  // Only a source that actually HAS more than two channels earns a wider output. Unknown (0, null,
  // undefined, or a bogus value) means stereo, which is also the safe pre-existing behaviour.
  if (!(channels > 2)) return 2;
  return Math.min(channels, max);
}

export function recoveryDecision({ attempts, consecutiveFailures, hasNext }) {
  if (attempts < RECOVERIES_PER_TRACK) return "retry";
  if (consecutiveFailures + 1 >= MAX_CONSECUTIVE_FAILURES) return "stop";
  return hasNext ? "skip" : "stop";
}

const VOLUME_KEY = "music.volume"; // arcade-style namespaced localStorage key
const LYRICS_KEY = "music.lyrics"; // on-screen lyrics toggle, remembered across routes/reloads
const LYRICS_DISPLAY_KEY = "music.lyrics.display"; // size/font/scrim/follow — see MusicLyricsSettings
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
  // HOW the lyrics look, as opposed to whether they're on: size, font, backdrop, follow. Kept here
  // beside the on/off switch because the same three surfaces read both, and normalized on the way in
  // so a stale or hand-edited storage value can't leave the pane unreadable.
  const [lyricsSettings, setLyricsSettings] = useState(() => {
    try {
      return normalizeLyricsSettings(JSON.parse(window.localStorage.getItem(LYRICS_DISPLAY_KEY)));
    } catch {
      return { ...LYRICS_DEFAULTS };
    }
  });
  // Favorited track ids. Held HERE rather than fetched per surface because three different places
  // draw the same heart (the play bar, Now Playing, and the manager's Favorites card) and they must
  // agree the instant one of them is clicked. One fetch per session; a Set so the bar's per-render
  // lookup is O(1) rather than a scan of a list that only grows.
  const [favoriteIds, setFavoriteIds] = useState(() => new Set());
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
  // ── Recovery bookkeeping (see recoveryDecision) ─────────────────────────────
  // The live track, readable from handlers that are bound for the element's lifetime.
  const currentRef = useRef(null);
  // { sec, at } — where the playhead was, and when we last saw it move. Feeds the stall watchdog
  // and tells a retry where to resume.
  const progressRef = useRef({ sec: 0, at: 0 });
  // { trackId, attempts, attemptedAtSec } — this track's spent recovery budget.
  const recoveryRef = useRef({ trackId: null, attempts: 0, attemptedAtSec: 0 });
  // Tracks written off back-to-back with no successful playback between them. Cleared by progress.
  const consecutiveFailuresRef = useRef(0);
  // Indirection for the mutual reference between loadTrack and failTrack.
  const failTrackRef = useRef(() => {});
  // The URL the element was last given, so a failure can ask the host what it actually said.
  const lastUrlRef = useRef(null);
  // Channel count of the loaded track, from Stream/Start. Kept in a ref because the graph may be
  // built LONG after the track loaded (the visualizer opens mid-song) and has to size itself then.
  const trackChannelsRef = useRef(0);

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

  // ── Favorites ──────────────────────────────────────────────────────────────
  // Loaded once, alongside capabilities. A failure leaves the set empty, which draws every heart
  // hollow — wrong, but harmless and self-correcting on the next toggle, whereas guessing full would
  // show tracks as favorited that aren't.
  useEffect(() => {
    if (!enabled) return undefined;
    let cancelled = false;
    MovieAPI.getMusicFavorites()
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => {
        if (!cancelled && data) setFavoriteIds(new Set(data.trackIds || []));
      })
      .catch(() => { /* hearts stay hollow until a toggle says otherwise */ });
    return () => { cancelled = true; };
  }, [enabled]);

  const isFavorite = useCallback((trackId) => favoriteIds.has(trackId), [favoriteIds]);

  /// Heart / un-heart a track. Optimistic, because the heart is a one-click gesture and a round trip
  /// of dead time is exactly what makes one feel broken — but it ROLLS BACK on failure rather than
  /// leaving a filled heart over a favorite the server never recorded.
  const toggleFavorite = useCallback((trackId) => {
    if (!enabled || trackId == null) return;
    // Read the intent from state, NOT from inside the updater below: an updater runs during the next
    // render, so anything it assigns is still unset by the time the request is sent.
    const want = !favoriteIds.has(trackId);
    setFavoriteIds((prev) => withFavorite(prev, trackId, want));
    MovieAPI.setMusicFavorite(trackId, want)
      .then((r) => {
        if (r.ok) return;
        throw new Error(String(r.status));
      })
      .catch(() => setFavoriteIds((prev) => withFavorite(prev, trackId, !want)));
  }, [enabled, favoriteIds]);

  // Mirrors, so the failure handlers (which run from element events and an interval, never from
  // render) can read the live track without every one of them re-binding on each change.
  currentRef.current = current;

  // Size the graph's output to the track so the visualizer can't collapse surround to stereo.
  //
  // AudioDestinationNode defaults to channelCount 2 with channelCountMode "explicit", which means it
  // DOWN-MIXES whatever reaches it. That is a real trap here: createMediaElementSource reroutes the
  // element permanently, so opening the visualizer once folds every later track to stereo for the
  // rest of the session — visualizer closed or not.
  //
  // The fix is to size the destination to the SOURCE, not to maxChannelCount. Pinning it wide open
  // would be worse than the bug: a stereo track fed to a 6-channel destination is up-mixed by the
  // "speakers" rules into L, R and four silent channels, so Chrome hands Windows a 5.1 stream whose
  // centre and surrounds are digital silence — and the OS/receiver upmixer, the only thing that puts
  // stereo music into those speakers at all, sees a stream that is already 5.1 and stays out of it.
  // So: surround sources get their real width, stereo stays at 2 and keeps the upmix path intact.
  //
  // channelInterpretation stays "speakers" deliberately — on a 7.1 file played through a 5.1 device
  // we clamp to 6, and "speakers" folds it properly where "discrete" would just drop the extra pair.
  const applyOutputChannels = useCallback(() => {
    const graph = audioGraphRef.current;
    if (!graph) return; // no graph yet: ensureAudioGraph applies this when it builds one
    const dest = graph.audioContext.destination;
    const want = outputChannelCount(trackChannelsRef.current, dest.maxChannelCount);
    if (dest.channelCount === want) return;
    try {
      dest.channelCount = want;
    } catch {
      // Assigning above maxChannelCount throws IndexSizeError, and some engines refuse the write
      // outright. Either way the previous (working) value stands — audio keeps playing, just folded.
    }
  }, []);

  // Fetch a fresh signed URL and put it on the element. Shared by the track-change effect and by
  // recovery, which differ only in where they resume from — a re-mint is exactly a load, and having
  // two spellings of it is how the two paths would drift.
  const loadTrack = useCallback((track, { resumeAt = 0, autoplay = true } = {}) => {
    const audio = audioRef.current;
    if (!audio || !track) return;
    const seq = ++loadSeqRef.current;
    // Start the stall clock at the load: a track that never produces a single timeupdate (the
    // gateway accepted the request and then said nothing) has to be recoverable too.
    progressRef.current = { sec: resumeAt, at: Date.now() };
    MovieAPI.startMusicTrack(track.id)
      .then((r) => (r.ok ? r.json() : r.json().catch(() => ({})).then((b) => Promise.reject(b))))
      .then((data) => {
        if (seq !== loadSeqRef.current) return; // superseded by a newer pick
        setError(null);
        lastUrlRef.current = data.url; // kept for diagnoseStreamUrl if this load dies
        // Before the element gets the source, not after: the destination's width should already be
        // right when the first buffer is decoded, so no part of the track is folded on the way in.
        trackChannelsRef.current = Number(data.channels) || 0;
        applyOutputChannels();
        audio.src = data.url;
        if (resumeAt > 0) {
          // currentTime can only be set once the element knows the media's shape.
          audio.addEventListener("loadedmetadata", () => {
            try { audio.currentTime = resumeAt; } catch { /* unseekable: start over rather than fail */ }
          }, { once: true });
        }
        if (!autoplay) {
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
        // A Start that refuses is the same class of failure as an element that errors: the track is
        // not reaching the speakers. It goes through the same bounded recovery.
        failTrackRef.current(body?.message || "This track can't be played.");
      });
  }, [applyOutputChannels]);

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
    // A new pick is a clean slate: this track has spent none of its recovery budget.
    recoveryRef.current = { trackId: current.id, attempts: 0, attemptedAtSec: 0 };
    const autoplay = !suppressAutoplayRef.current;
    suppressAutoplayRef.current = false;
    loadTrack(current, { autoplay });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id]);

  // The progress record behind both the stall watchdog and the recovery budget. A ref, not state:
  // it updates ~4x/second and nothing renders from it.
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return undefined;
    const onTime = () => {
      const sec = audio.currentTime || 0;
      progressRef.current = { sec, at: Date.now() };
      // A track that has genuinely got going clears the "everything is failing" count…
      if (sec > 3) consecutiveFailuresRef.current = 0;
      // …and one that has run cleanly for a while earns its recovery budget back, so a long track
      // on a flaky link isn't written off for troubles it already recovered from. This can't spin:
      // refunding requires progress, and a failure loop makes none.
      const state = recoveryRef.current;
      if (state.attempts > 0 && sec - state.attemptedAtSec > RECOVERY_RESET_SEC) state.attempts = 0;
    };
    audio.addEventListener("timeupdate", onTime);
    return () => audio.removeEventListener("timeupdate", onTime);
  }, []);

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

  // Retry, skip, or give up — the single place a failed track is decided about. Element errors,
  // refused Starts and detected stalls all arrive here, so there is exactly one policy.
  const failTrack = useCallback((message) => {
    const track = currentRef.current;
    if (!track) return; // nothing loaded (e.g. the element erroring as stop() clears its src)
    if (recoveryRef.current.trackId !== track.id) {
      recoveryRef.current = { trackId: track.id, attempts: 0, attemptedAtSec: 0 };
    }
    const state = recoveryRef.current;
    const hasNext = index + 1 < queue.length;

    switch (recoveryDecision({
      attempts: state.attempts,
      consecutiveFailures: consecutiveFailuresRef.current,
      hasNext,
    })) {
      case "retry":
        state.attempts += 1;
        state.attemptedAtSec = progressRef.current.sec;
        // A fresh URL, resumed just behind where it died — a re-mint costs one round trip and is
        // the only thing that helps when the old connection is what broke.
        loadTrack(track, {
          resumeAt: Math.max(0, progressRef.current.sec - RESUME_REWIND_SEC),
          autoplay: true,
        });
        return;

      case "skip":
        // One unplayable file must not end the session. Say so, and move on.
        consecutiveFailuresRef.current += 1;
        setError("Skipped a track that wouldn't stream.");
        next();
        return;

      default: {
        // Out of road: either nowhere to skip to, or enough tracks have failed in a row that the
        // problem is plainly the stream and not the file. This is the one place worth spending a
        // round trip to find out WHICH, because this is the message the user is left staring at.
        consecutiveFailuresRef.current += 1;
        const audio = audioRef.current;
        if (audio) audio.pause();
        setError(message || "Playback stopped.");
        setPlaying(false);
        const url = lastUrlRef.current;
        diagnoseStreamUrl(url).then((why) => {
          if (why) setError(`Playback stopped — ${why}.`);
        });
      }
    }
  }, [index, queue.length, loadTrack, next]);

  // Kept in a ref so loadTrack — defined earlier, and deliberately dependency-free — can reach it.
  useEffect(() => { failTrackRef.current = failTrack; }, [failTrack]);

  // Stall watchdog. There is no event for "the bytes stopped arriving and nobody said anything":
  // `stalled`/`waiting` fire inconsistently across browsers, and not at all when a connection dies
  // quietly. The honest test is that the playhead has not moved while the element still claims to be
  // playing. The grace period is long enough that ordinary rebuffering never trips it.
  useEffect(() => {
    if (!current) return undefined;
    const id = setInterval(() => {
      const audio = audioRef.current;
      if (!audio || audio.paused || audio.ended || audio.seeking) return;
      if (Date.now() - progressRef.current.at < STALL_GRACE_MS) return;
      // Re-arm before handing off, so a reload in flight isn't re-triggered on the next poll.
      progressRef.current = { ...progressRef.current, at: Date.now() };
      failTrackRef.current("Playback stopped — the stream isn't answering.");
    }, STALL_POLL_MS);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id]);

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
        // Two INDEPENDENT connections from the same source. The analyser branch folds its input down
        // for the FFT, but that fold is local to the branch and cannot reach the destination — which
        // is why the visualizer still works on a surround track without costing it any channels.
        source.connect(analyser);
        source.connect(audioContext.destination);
        audioGraphRef.current = { audioContext, source, analyser };
      } catch {
        return null; // no Web Audio here — the caller shows the fallback message
      }
      // The graph usually appears mid-song, so the track that is ALREADY playing has to re-assert
      // its width — otherwise opening the visualizer on a 5.1 track folds it until the next track.
      applyOutputChannels();
    }

    const graph = audioGraphRef.current;
    if (graph.audioContext.state === "suspended") graph.audioContext.resume().catch(() => {});
    return graph;
  }, [applyOutputChannels]);

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

  /// One field at a time — every control in the panel edits exactly one, and a whole-object setter
  /// would make each of them responsible for preserving the other four.
  const setLyricsSetting = useCallback((key, value) => {
    setLyricsSettings((prev) => {
      const next = normalizeLyricsSettings({ ...prev, [key]: value });
      try { window.localStorage.setItem(LYRICS_DISPLAY_KEY, JSON.stringify(next)); } catch { /* private mode */ }
      return next;
    });
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
  // The element gave up on this source. That used to end the listening session; now it goes through
  // the same bounded recovery as everything else. The message is only reached once retries and a
  // skip are exhausted — it says what actually happened rather than the bare "Playback failed."
  const onError = useCallback(() => {
    failTrackRef.current("Playback failed — the stream isn't answering.");
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
    () => ({ queue, index, current, playing, error, audioRef, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks, favoriteIds, isFavorite, toggleFavorite, lyricsSettings, setLyricsSetting }),
    [queue, index, current, playing, error, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks, favoriteIds, isFavorite, toggleFavorite, lyricsSettings, setLyricsSetting]
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
