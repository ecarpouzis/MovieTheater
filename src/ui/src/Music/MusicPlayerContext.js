import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { MovieAPI } from "../MovieAPI";
import { diagLog, snapshotAudio, diagEnabled, reportIncident, MEDIA_EVENTS } from "./musicDiag";
import MusicDiagPanel from "./MusicDiagPanel";
import { useMediaSession } from "../useMediaSession";
import MusicMiniPlayer from "./MusicMiniPlayer";
import { LYRICS_DEFAULTS, normalizeLyricsSettings } from "./MusicLyricsSettings";
import { createMseEngine } from "./MusicMseEngine";
import { buildCapabilityMatrix, chooseEngineMode } from "./musicTreatments";
import { seekPlan, trackTimeAt } from "./musicTimeline";

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
// A rebuffer is not a stall. While the element still has the network open it IS fetching — a big
// FLAC on a phone hits this every time Chrome's ~16 MiB media buffer runs dry and it re-requests
// the rest of the file — and the browser recovers on its own if left alone. Only after this much
// silence is a still-loading element considered genuinely stuck.
const LOADING_STALL_GRACE_MS = 45000;
// A tick that arrives this much later than it was scheduled means the PAGE wasn't running, not that
// the stream stopped: a phone whose screen went off, a backgrounded tab, a throttled or frozen
// renderer. See the watchdog for why that distinction is the whole bug.
const TICK_LATE_MS = STALL_POLL_MS * 3;
// Resume a shade EARLIER than where it died: the last second was being decoded when the stream
// went, and landing exactly on it invites the same failure.
const RESUME_REWIND_SEC = 1;
// Budgeted retries wait before re-minting. Without a delay the budget is no budget at all: a
// phone whose radio naps for one second rejects every fetch INSTANTLY, so 2 retries + a skip + the
// next track's retries — the whole session — burned down in about a second, and the HEAD probe then
// found a healthy host because the network was back by the time anyone asked it.
const RETRY_BACKOFF_MS = [1000, 5000];
// While parked (see shouldPark): how often to try again, and for how many tries. Browsers throttle
// background timers to ≥1/min, so the real cadence while hidden is slower than this reads. The cap
// keeps the loop finite; after it, only a wake or an `online` event retries — which means a phone
// left unplugged all night does a bounded amount of work, and still heals the moment it's looked at.
const PARKED_RETRY_MS = 20000;
const PARKED_MAX_BEATS = 90;
// ── The track boundary (why gapless is a correctness feature, not a nicety) ──
// Advancing a track used to be: `ended` → setIndex → effect → POST Stream/Start → await a signed
// URL → src → play(). Every step after `ended` needs the PAGE to be running, and the page's licence
// to run in the background is that it is playing audio — which, at that exact moment, it no longer
// is. On a phone with the screen off the renderer is throttled the instant the element goes idle,
// so the round trip lands late (or not until wake) and play() is then refused for having no audio
// in flight: the album silently stops, and the next track starts when you pick the phone up. That
// is the reported symptom, and it is NOT the stall watchdog (see stallVerdict) — nothing failed
// here, the gap itself was the bug.
//
// So the URL for the next track is minted while the current one is still playing, and `ended`
// installs it and calls play() SYNCHRONOUSLY, inside the event handler, with no await in between.
// The element never goes idle for longer than a source swap, the page never loses its audio
// licence, and React's index update follows behind as bookkeeping rather than as the mechanism.
// Minting early is free: /API/Music/Stream/Start only signs a capability (no play count, no
// server-side session) and the token is good for 6 hours.
const PREFETCH_LEAD_SEC = 30;
// Hold a whole track in memory rather than streaming it. Above this we fall back to streaming —
// a 277 MB live set is not worth an OOM on a phone.
const MAX_PRELOAD_BYTES = 120 * 1024 * 1024;
// How far into a track we start preparing the next one. Early, deliberately.
const PRELOAD_START_SEC = 5;
// How long before the end the idle deck starts PLAYING (muted).
//
// This is the fix for the boundary that kept coming back, and it is about who does the work rather
// than how early it is done. A JS `fetch()` is the first thing a backgrounded phone stops running,
// so downloading the next track in script is the least reliable way to have it ready — the memory
// download only ever worked because the screen was still on. A media element that is PLAYING is
// fetched by the browser's own media stack, which is the same pipeline keeping the current track
// alive with the screen off. So the next deck is started, muted, before the boundary: the bytes
// arrive natively, and the page never reaches an instant with no audio in flight, which is the
// whole of its licence to keep running. At `ended` the deck is already playing — it is rewound to
// 0 and unmuted, which needs no network and no round trip.
const PREROLL_LEAD_SEC = 8;
// Chrome's media buffer caps at 16 MiB - 32 KiB (16744448). A file bigger than that WILL be evicted
// and re-requested part-way through the song — proved in Caddy's access log as a second request for
// `bytes=16744448-` mid-track — and a phone whose screen has gone off cannot service that
// re-request. Anything over this is downloaded in full before it is played, never streamed.
const BUFFER_SAFE_BYTES = 15 * 1024 * 1024;

/// Ask the stream host what it actually said. "Playback failed." is what the player showed for every
/// distinct prod outage this vertical has had — a gateway that couldn't SEE the files, an expired or
/// mis-signed token, a wrong ACAO — and told nobody which. The element itself won't say (a media
/// error carries no status), but a HEAD against the same URL will, and the answers are diagnostic:
/// 404 means the gateway can't reach the file, 403 means the token was refused, and a thrown fetch
/// means the host didn't answer at all or its CORS headers are wrong (the element sets
/// crossOrigin=anonymous, so a bad ACAO kills playback outright).
/// The one answer a HEAD can give that ISN'T diagnostic — the host is fine and the probe found
/// nothing. Exported so the failure path can tell "the probe found the problem" from "the probe
/// found nothing", because a healthy host must never overwrite what the element itself reported.
export const HOST_HEALTHY = "the stream host answered, but the browser couldn't play it";

export async function diagnoseStreamUrl(url, doFetch = fetch) {
  if (!url) return null;
  try {
    const r = await doFetch(url, { method: "HEAD" });
    if (r.status === 404) return "the stream host can't find the file (404)";
    if (r.status === 403) return "the stream host refused the token (403)";
    if (!r.ok) return `the stream host answered ${r.status}`;
    return HOST_HEALTHY;
  } catch {
    return "the stream host didn't answer (it may be down, or its CORS headers are wrong)";
  }
}

/// What the ELEMENT said. A MediaError is the only first-hand account of why playback died, and it
/// was being thrown away in favour of the HEAD probe's guess — so a dropped connection, a bad
/// decode and a format the browser won't take all produced the same sentence, and all three of them
/// read as "the server is fine, no idea". Exported and pure so the wording is unit-testable.
export function mediaErrorReason(mediaError) {
  switch (mediaError?.code) {
    case 1: return "the browser aborted the download";
    case 2: return "the connection to the stream host dropped";
    case 3: return "the browser couldn't decode the audio";
    case 4: return "the browser can't play this file's format";
    default: return null;
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

/// Should the stall watchdog act on this tick? Exported and pure because it is the load-bearing
/// judgement in the player and its failure mode is invisible by ear: a watchdog that fires while the
/// page is merely asleep kills healthy playback, and you only find out that it did because the music
/// stopped while nobody was looking. See the watchdog itself for what each guard is protecting from.
/// "rearm" means our clock measured something other than the stream and the grace period should
/// start over; "fail" means the playhead really has gone quiet.
export function stallVerdict({ hidden, sinceTickMs, sinceProgressMs, loading }) {
  if (hidden || sinceTickMs > TICK_LATE_MS) return "rearm";
  return sinceProgressMs >= (loading ? LOADING_STALL_GRACE_MS : STALL_GRACE_MS) ? "fail" : "wait";
}

export function recoveryDecision({ attempts, consecutiveFailures, hasNext }) {
  if (attempts < RECOVERIES_PER_TRACK) return "retry";
  if (consecutiveFailures + 1 >= MAX_CONSECUTIVE_FAILURES) return "stop";
  return hasNext ? "skip" : "stop";
}

/// Is this failure the WORLD's fault rather than the stream's? A network-level failure on a hidden
/// page or an offline browser means the phone is napping, not that the server is broken — and the
/// listener is still listening. Spending the recovery budget there is how one Wi-Fi doze mid-album
/// terminally stopped the session; parking (hold position, retry when the world changes) is what a
/// native player does. A content-level failure (bad decode, refused token, unsupported format)
/// parks nowhere: that file will be exactly as broken when the network returns.
/// Exported and pure because it decides between "bounded budget that can end the session" and
/// "patient loop that must not" — the wrong answer in either direction is invisible until nobody
/// is watching.
export function shouldPark({ networkLevel, hidden, offline }) {
  return !!networkLevel && (!!hidden || !!offline);
}

/// How long a budgeted retry waits, by how many attempts this track has already spent. Clamped to
/// the last entry so an out-of-range index can only slow down, never go undefined-instant.
export function retryDelayMs(attemptsSpent) {
  return RETRY_BACKOFF_MS[Math.min(Math.max(attemptsSpent, 0), RETRY_BACKOFF_MS.length - 1)];
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
  // { trackId, attempts, attemptedAtSec, parkBeats } — this track's spent recovery budget, and how
  // many parked heartbeats it has used (see shouldPark; parking spends beats, never attempts).
  const recoveryRef = useRef({ trackId: null, attempts: 0, attemptedAtSec: 0, parkBeats: 0 });
  // The one recovery in flight: { trackId, timerId } — a budgeted retry waiting out its backoff, or
  // a parked failure waiting for the world to change (timerId null once the heartbeat cap is spent:
  // wake/online-only from then on). One at a time; while it exists, new failures and the watchdog
  // hold their fire — re-judging a corpse a second time only burns budget the pending retry may
  // be about to not need.
  const pendingRecoveryRef = useRef(null);
  // play() was refused on a hidden page during recovery. The listener is still there — retry the
  // play the moment the page is visible, instead of leaving a silently paused bar behind.
  const resumeOnWakeRef = useRef(false);
  // Has the stored queue been read back yet? Until it has, an empty `queue` means "not loaded",
  // not "cleared" — see the persist effect.
  const hydratedRef = useRef(false);
  // Which track's source is actually ON the element right now. Without this the player cannot tell
  // "paused, holding this track" from "stranded, still holding the PREVIOUS track's spent URL" —
  // and both `play()` and the wake retry then act on a source that can never produce audio again.
  const loadedTrackIdRef = useRef(null);
  // The whole queue, readable from handlers (the engine appends ahead of the playhead).
  const queueRef = useRef([]);
  // Which track a cross-engine flip has prepared a deck for, or null.
  const mseHandoffRef = useRef(null);

  // ── Two decks (A/B) ───────────────────────────────────────────────────────
  // Gapless used to be one <audio> whose `src` was swapped at the boundary. That swap is the one
  // operation a hidden page cannot survive: assigning a new source drops the element to
  // HAVE_NOTHING, and a backgrounded page's entire licence to keep running is that it IS playing
  // audio. Even done synchronously, playback still had to wait for bytes, and play() was refused.
  //
  // So the next track is buffered into the OTHER element while this one is still playing, and the
  // boundary is a flip between two ready elements — no load, no network, nothing the browser can
  // refuse for want of data. `audioRef` always points at whichever deck is live, so every other
  // reader (the play bar, the Now Playing page, the watchdog) is unchanged.
  const audioARef = useRef(null);
  const audioBRef = useRef(null);
  // ── The third element: the MSE engine's (music-mse-plan.md §Phase 2) ───────────────────────────
  // Modelled as a THIRD DECK rather than as a parallel player, and that is the whole integration
  // trick: `deckRef.current === "mse"` makes `audioRef` point at it, so the play bar, the media
  // session, the volume, the watchdog and the boundary machinery all keep working unchanged — and a
  // cross-engine flip is the deck flip that already exists, with a different element on one side.
  const audioMseRef = useRef(null);
  const deckRef = useRef("a");
  // Mirrored as state on purpose: effects that addEventListener on the live element must re-bind
  // when the deck flips. A ref alone leaves the timeupdate listener — and with it progress
  // tracking, the stall watchdog's clock and the prefetch that feeds the NEXT boundary — attached
  // to a deck that stopped playing an album ago.
  const [activeDeck, setActiveDeck] = useState("a");
  // True while a track is being downloaded before it can start. The bar says so rather than
  // leaving a dead-looking player: on a WAN uplink a 40 MB track is a real wait.
  const [buffering, setBuffering] = useState(false);
  // Which track the idle deck has been started (muted) for, or null. See PREROLL_LEAD_SEC.
  const prerollRef = useRef(null);

  // Which track the IDLE deck has buffered, or null. Distinct from prefetchRef (a URL in hand):
  // this one means the bytes are already decoded and ready to play.
  const deckLoadedRef = useRef(null);
  const preloadAbortRef = useRef(null);
  const liveFetchRef = useRef(null);

  // Blob URLs currently held by each deck, so they can be revoked the moment they stop being
  // the thing playing. An un-revoked object URL pins its whole ArrayBuffer for the life of the page.
  const deckBlobRef = useRef({ a: null, b: null });
  const revokeDeck = useCallback((deck) => {
    const url = deckBlobRef.current[deck];
    if (url) {
      try { URL.revokeObjectURL(url); } catch { /* already gone */ }
      deckBlobRef.current[deck] = null;
    }
  }, []);

  // element -> MediaElementAudioSourceNode; both decks must be routed once the graph exists.
  const graphSourcesRef = useRef(new Map());

  const elFor = useCallback((deck) => {
    if (deck === "mse") return audioMseRef.current;
    return deck === "a" ? audioARef.current : audioBRef.current;
  }, []);
  // Which deck the NEXT thing goes on. With the engine live ("mse") that is deck a — the engine's
  // own boundaries are buffer continuations and need no idle deck at all; the only reason to prepare
  // one is a cross-engine flip.
  const idleDeck = useCallback(() => (deckRef.current === "a" ? "b" : "a"), []);
  const idleEl = useCallback(() => elFor(idleDeck()), [elFor, idleDeck]);
  // The live element, and the ref every consumer already reads. Kept in sync on every render so a
  // deck flip is invisible outside this file.
  // The volume both decks must agree on — a flip that changes loudness is a bug the listener hears.
  const volumeOf = useCallback(() => {
    const live = elFor(deckRef.current);
    if (live && Number.isFinite(live.volume)) return live.volume;
    const stored = parseFloat(window.localStorage.getItem(VOLUME_KEY));
    return Number.isFinite(stored) ? Math.min(Math.max(stored, 0), 1) : 1;
  }, [elFor]);

  const syncActive = useCallback(() => {
    audioRef.current = elFor(deckRef.current);
    return audioRef.current;
  }, [elFor]);
  // Tracks written off back-to-back with no successful playback between them. Cleared by progress.
  const consecutiveFailuresRef = useRef(0);
  // Indirection for the mutual reference between loadTrack and failTrack.
  const failTrackRef = useRef(() => {});
  // Same mirror trick for loadTrack: the wake handler and the play button both need to be able to
  // re-drive a load that never landed, without re-binding on every render.
  const loadTrackRef = useRef(() => {});
  // The URL the element was last given, so a failure can ask the host what it actually said.
  const lastUrlRef = useRef(null);
  // Channel count of the loaded track, from Stream/Start. Kept in a ref because the graph may be
  // built LONG after the track loaded (the visualizer opens mid-song) and has to size itself then.
  const trackChannelsRef = useRef(0);
  // ── Gapless hand-off (see PREFETCH_LEAD_SEC) ───────────────────────────────
  // Whatever is queued after the current track, readable from the `ended` handler without making
  // that handler re-bind on every queue change.
  const nextTrackRef = useRef(null);
  // { trackId, url, channels } — the next track's signed URL, minted early. `url` is null while the
  // mint is in flight, which also marks the slot as claimed so the poll doesn't re-request it.
  const prefetchRef = useRef(null);
  // The track whose source `ended` already installed. The track-change effect consumes this and
  // skips its own load, rather than tearing a playing stream off the element to re-fetch its URL.
  const handedOffRef = useRef(null);

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

  // audioRef must point at the live deck before anything reads it. A layout effect on every
  // render (not just on flips) so it is right after the very first commit too, when both element
  // refs go from null to real in the same pass.
  useLayoutEffect(() => { syncActive(); });

  // Mirrors, so the failure handlers (which run from element events and an interval, never from
  // render) can read the live track without every one of them re-binding on each change.
  currentRef.current = current;
  nextTrackRef.current = index >= 0 && index + 1 < queue.length ? queue[index + 1] : null;
  // The whole queue, for the engine: it appends ahead of the playhead and must be able to read the
  // list from a handler, not from a render.
  queueRef.current = queue;

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

  // Download a track in full, then put the BYTES on the live deck. Used for anything over the
  // buffer cap (see BUFFER_SAFE_BYTES). Nothing is transcoded — the blob is the file, byte for byte.
  const fetchToDeck = useCallback((data, seq, { resumeAt, autoplay, track }) => {
    const audio = audioRef.current;
    const deck = deckRef.current;
    setBuffering(true);
    const controller = new AbortController();
    liveFetchRef.current?.abort();
    liveFetchRef.current = controller;
    diagLog("load:download", { track: track.id, mb: +(data.sizeBytes / 1048576).toFixed(1) });
    return fetch(data.url, { signal: controller.signal, credentials: "omit" })
      .then((res) => {
        if (!res.ok) throw new Error(`status ${res.status}`);
        return res.blob();
      })
      .then((blob) => {
        if (seq !== loadSeqRef.current) return;  // the listener moved on mid-download
        revokeDeck(deck);
        const objectUrl = URL.createObjectURL(blob);
        deckBlobRef.current[deck] = objectUrl;
        lastUrlRef.current = data.url;
        trackChannelsRef.current = Number(data.channels) || 0;
        applyOutputChannels();
        audio.src = objectUrl;
        loadedTrackIdRef.current = track.id;
        setBuffering(false);
        diagLog("load:downloaded", { track: track.id, mb: +(blob.size / 1048576).toFixed(1) });
        if (resumeAt > 0) {
          audio.addEventListener("loadedmetadata", () => {
            try { audio.currentTime = resumeAt; } catch { /* unseekable */ }
          }, { once: true });
        }
        if (!autoplay) { setPlaying(false); return; }
        audio.play().catch(() => {
          if (document.hidden) resumeOnWakeRef.current = true;
          setPlaying(false);
        });
      })
      .catch((e) => {
        if (controller.signal.aborted || seq !== loadSeqRef.current) return;
        setBuffering(false);
        // A download that fails is not fatal: stream it the old way and let the ordinary recovery
        // machinery deal with whatever happens next.
        diagLog("load:download-failed", { track: track.id, why: String(e?.message || e).slice(0, 40) });
        lastUrlRef.current = data.url;
        trackChannelsRef.current = Number(data.channels) || 0;
        applyOutputChannels();
        audio.src = data.url;
        loadedTrackIdRef.current = track.id;
        if (autoplay) audio.play().catch(() => { if (document.hidden) resumeOnWakeRef.current = true; setPlaying(false); });
      });
  }, [applyOutputChannels, revokeDeck]);

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
    // Arm the wake retry HERE, before the round trip — not only in play()'s catch below. The load
    // that follows may never reach that catch: a page whose track has just ended is no longer
    // playing audio, so it loses the exemption that kept it running in the background, and this
    // fetch can simply not land until the phone is picked up again. Arming after the await is
    // arming in code that a frozen renderer never reaches.
    resumeOnWakeRef.current = autoplay && document.hidden;
    diagLog("load:start", { track: track.id, autoplay, resumeAt: Math.round(resumeAt) });
    MovieAPI.startMusicTrack(track.id)
      .then((r) => (r.ok ? r.json() : r.json().catch(() => ({})).then((b) => Promise.reject(b))))
      .then((data) => {
        if (seq !== loadSeqRef.current) return; // superseded by a newer pick
        diagLog("load:minted", { track: track.id, url: (data.url || "").slice(-28), size: data.sizeBytes });
        setError(null);
        // Too big to stream safely? Fetch the whole thing first. This costs a wait on the FIRST
        // track of a session — every later one was already downloaded during the track before it —
        // and it is the only way a file over the buffer cap survives the screen going off.
        const size = Number(data.sizeBytes) || 0;
        if (size > BUFFER_SAFE_BYTES && size <= MAX_PRELOAD_BYTES) {
          return fetchToDeck(data, seq, { resumeAt, autoplay, track });
        }
        lastUrlRef.current = data.url; // kept for diagnoseStreamUrl if this load dies
        // Before the element gets the source, not after: the destination's width should already be
        // right when the first buffer is decoded, so no part of the track is folded on the way in.
        trackChannelsRef.current = Number(data.channels) || 0;
        applyOutputChannels();
        audio.src = data.url;
        loadedTrackIdRef.current = track.id;
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
          // Autoplay refused. In the foreground that means no user gesture yet — leave it paused;
          // the bar shows Play. On a HIDDEN page it means the browser wouldn't restart audio with
          // nobody looking — but the listener is still there, so retry on the next wake instead of
          // leaving a silently paused bar as the only trace.
          diagLog("load:play-refused", { track: track.id, armWake: document.hidden });
          if (document.hidden) resumeOnWakeRef.current = true;
          setPlaying(false);
        });
      })
      .catch((body) => {
        diagLog("load:failed", { track: track.id, why: String(body && body.message || body).slice(0, 80) });
        if (seq !== loadSeqRef.current) return;
        // Two different accounts land here. A rejected fetch (an Error) is the NETWORK's: the
        // request never got an answer, which on a sleeping phone means the radio napped — park-able.
        // An HTTP error body (a plain object) is the SERVER refusing, which no amount of waiting
        // fixes; that spends budget like any other failure.
        const neverAnswered = body instanceof Error;
        failTrackRef.current(
          neverAnswered ? "Playback stopped — the connection to the site dropped." : (body?.message || "This track can't be played."),
          { networkLevel: neverAnswered }
        );
      });
  }, [applyOutputChannels, fetchToDeck]);

  /// Mint the next track's URL now, so `ended` has one in hand. Idempotent per track and cheap to
  /// call from a timeupdate: a slot claimed for this id (in flight or filled) is left alone, and a
  /// slot pointing at some other track is simply replaced — the queue moved on under it.
  /// A failure clears the slot and says nothing: the hand-off just doesn't happen, and the ordinary
  /// load path runs on the track change exactly as it did before, error handling included.
  /**
   * Put the next track on the idle deck — as BYTES WE ALREADY HOLD, not a URL to be streamed.
   *
   * This is the whole answer to playback dying on a sleeping phone. It was never really about
   * format or file size: a 40 MB FLAC and an 11 MB mp3 both died mid-song. What they had in common
   * was needing the network DURING playback — and a phone whose screen is off naps its radio, so an
   * in-flight connection dies (MEDIA_ERR_NETWORK) or a fresh request never completes
   * (MEDIA_ERR_SRC_NOT_SUPPORTED). Neither leaves a server-side trace, because neither ever
   * reaches the server.
   *
   * Downloading the track while the page is awake and playing it from memory removes the
   * dependency entirely: once the bytes are here, there is no connection left to lose. Bit-perfect
   * — the file is byte-for-byte what is on disk, so FLAC stays FLAC and nothing is transcoded.
   */
  const installOnIdleDeck = useCallback((track, url, sizeBytes = 0) => {
    const idle = idleEl();
    const deck = idleDeck();
    if (!idle) return;
    const stream = (why) => {
      // Hand the URL to the ELEMENT and let the browser fetch it. Not a fallback any more: on a
      // hidden page this is the only one of the two that actually runs.
      try {
        revokeDeck(deck);
        idle.preload = "auto";
        idle.src = url;
        idle.volume = volumeOf();
        idle.load();
        deckLoadedRef.current = track.id;
        diagLog("preload:stream", { track: track.id, deck, why });
      } catch {
        deckLoadedRef.current = null;
      }
    };

    // Who fetches decides whether this works at all.
    //
    // Downloading to memory in JS still earns its keep for a file bigger than Chrome's media
    // buffer — such a file is evicted and re-requested part-way through the song, and a sleeping
    // phone cannot service that re-request. But it only works while the page is AWAKE. Hidden, the
    // fetch is exactly what gets throttled away, and the boundary then arrives with an empty deck.
    // So: script downloads only when it can (visible) and only when it must (over the buffer cap).
    // Otherwise the element does it, natively, which survives the screen going off.
    if (document.hidden || !sizeBytes || sizeBytes <= BUFFER_SAFE_BYTES) {
      stream(document.hidden ? "hidden" : "fits-buffer");
      return;
    }

    const controller = new AbortController();
    preloadAbortRef.current?.abort();
    preloadAbortRef.current = controller;
    diagLog("preload:fetch", { track: track.id, deck });
    fetch(url, { signal: controller.signal, credentials: "omit" })
      .then((res) => {
        if (!res.ok) throw new Error(`status ${res.status}`);
        const len = Number(res.headers.get("content-length") || 0);
        if (len > MAX_PRELOAD_BYTES) throw new Error("too big");
        return res.blob();
      })
      .then((blob) => {
        // A skip mid-download means these bytes are for a track nobody is about to play.
        if (nextTrackRef.current?.id !== track.id) return;
        revokeDeck(deck);
        const objectUrl = URL.createObjectURL(blob);
        deckBlobRef.current[deck] = objectUrl;
        idle.preload = "auto";
        idle.src = objectUrl;
        idle.volume = volumeOf();
        idle.load();
        deckLoadedRef.current = track.id;
        diagLog("preload:ready", { track: track.id, deck, mb: +(blob.size / 1048576).toFixed(1) });
      })
      .catch((e) => {
        if (controller.signal.aborted) return;
        stream(String(e?.message || e).slice(0, 40));
      });
  }, [idleEl, idleDeck, revokeDeck, volumeOf]);

  /// Start the idle deck playing, muted, so the browser fetches it natively and the page never
  /// reaches an instant with no audio in flight. Idempotent per track.
  const prerollIdleDeck = useCallback((upcomingId) => {
    const idle = idleEl();
    if (!idle || !idle.src) return;
    if (prerollRef.current === upcomingId) return;
    if (deckLoadedRef.current !== upcomingId) return;
    prerollRef.current = upcomingId;
    idle.muted = true;
    const p = idle.play();
    if (p && p.catch) {
      p.catch(() => {
        // Refused (no gesture yet, or the browser won't start a second stream). Nothing is lost:
        // the boundary falls back to playing it in the handler exactly as before.
        prerollRef.current = null;
        diagLog("preroll:refused", { track: upcomingId });
      });
    }
    diagLog("preroll", { track: upcomingId, hidden: document.hidden });
  }, [idleEl]);

  /// Undo a pre-roll whose boundary is no longer coming (a skip, a seek, a new pick).
  const cancelPreroll = useCallback(() => {
    if (prerollRef.current === null) return;
    const idle = idleEl();
    prerollRef.current = null;
    if (!idle) return;
    try {
      idle.pause();
      idle.currentTime = 0;
      idle.muted = false;
    } catch { /* the deck is being replaced anyway */ }
  }, [idleEl]);

  const prefetchNext = useCallback(() => {
    const track = nextTrackRef.current;
    if (!track) return;
    if (prefetchRef.current?.trackId === track.id) return;
    prefetchRef.current = { trackId: track.id, url: null, channels: 0 };
    MovieAPI.startMusicTrack(track.id)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
      .then((data) => {
        // Still the track we claimed the slot for? A skip mid-mint means this URL is for something
        // nobody is about to play, and writing it would hand `ended` the wrong song.
        if (prefetchRef.current?.trackId !== track.id) return;
        prefetchRef.current = { trackId: track.id, url: data.url, channels: Number(data.channels) || 0 };
        // …and start BUFFERING it on the idle deck, which is the whole point: at the boundary the
        // bytes must already be here. This is why a sleeping phone can cross a track boundary now —
        // there is nothing left to fetch at the moment it has the least licence to fetch anything.
        installOnIdleDeck(track, data.url, Number(data.sizeBytes) || 0);
      })
      .catch(() => {
        if (prefetchRef.current?.trackId === track.id) prefetchRef.current = null;
      });
  }, [installOnIdleDeck]);

  // ── Pending recovery (one in flight, ever) ─────────────────────────────────
  const clearPendingRecovery = useCallback(() => {
    const pending = pendingRecoveryRef.current;
    if (pending?.timerId) clearTimeout(pending.timerId);
    pendingRecoveryRef.current = null;
  }, []);

  // Run the recovery that was waiting. Resume position is read NOW, not at schedule time — the
  // playhead can't move while the stream is dead, and if it somehow did (the element healed itself),
  // the fresher number is the right one.
  const firePendingRecovery = useCallback(() => {
    const pending = pendingRecoveryRef.current;
    if (!pending) return;
    clearPendingRecovery();
    const track = currentRef.current;
    if (!track || track.id !== pending.trackId) return; // the user moved on; nothing to revive
    loadTrack(track, {
      resumeAt: Math.max(0, progressRef.current.sec - RESUME_REWIND_SEC),
      autoplay: true,
    });
  }, [clearPendingRecovery, loadTrack]);

  // delayMs null = no heartbeat: the parked cap is spent, and only a wake or `online` event fires it.
  const schedulePendingRecovery = useCallback((trackId, delayMs) => {
    clearPendingRecovery();
    const timerId = delayMs == null ? null : setTimeout(() => firePendingRecovery(), delayMs);
    pendingRecoveryRef.current = { trackId, timerId };
  }, [clearPendingRecovery, firePendingRecovery]);

  // ── The MSE engine (music-mse-plan.md §Phase 2) ────────────────────────────────────────────────
  // OFF by default. The flag is read ONCE per session — not per track — because switching engines
  // mid-queue is the one thing that would put a script event back at a boundary, which is the bug
  // this whole design exists to remove.
  const engineModeRef = useRef(null);
  if (engineModeRef.current === null) {
    let supported = false;
    try {
      const matrix = buildCapabilityMatrix({
        isTypeSupported: (mime) => window.MediaSource.isTypeSupported(mime),
        hasMediaSource: typeof window !== "undefined" && typeof window.MediaSource !== "undefined",
        hasManagedMediaSource: typeof window !== "undefined" && typeof window.ManagedMediaSource !== "undefined",
      });
      supported = matrix.anyTreatment;
    } catch { supported = false; }
    engineModeRef.current = chooseEngineMode({
      search: typeof window !== "undefined" ? window.location.search : "",
      storage: window.localStorage,
      supported,
    });
  }
  const engineRef = useRef(null);
  // Set once the engine has given up (a rung that exhausts MSE). From then on this session is a deck
  // session — the floor, with all four shipped fixes intact. Cleared only by an explicit new pick,
  // because that is a user gesture starting a fresh session rather than a boundary being crossed.
  const mseFallbackRef = useRef(false);
  // A MediaError kills the WHOLE MediaSource, not one track. Rung 6: rebuild once from where we are;
  // a second death means the decks.
  const mseErrorsRef = useRef(0);
  // The unbuffered-hidden-boundary tripwire has already fired this session. See onEnded.
  const reportedBoundaryRef = useRef(false);
  // The track currently being played off a DECK because the listener seeked somewhere the engine's
  // buffer could not reach. Distinct from mseFallbackRef, which is the engine having GIVEN UP: this
  // is a one-track detour and the engine comes back at the next boundary. See seekDetour.
  const seekDetourRef = useRef(null);
  // The queue played out. Latched so that WAKING UP does not restart the last track: on the phone
  // (field run, 00:58) coming back to a finished queue silently re-started its last song at 0:00 and
  // left it paused, which reads as a player that lost its place rather than one that finished.
  const queueFinishedRef = useRef(false);
  // Indirection for the engine's end-of-stream callback: onEnded is defined further down the file
  // and the engine is created above it.
  const endedRef = useRef(() => {});
  const mseActive = useCallback(
    () => engineModeRef.current === "mse" && !mseFallbackRef.current && !!engineRef.current,
    [],
  );

  const destroyEngine = useCallback(() => {
    const engine = engineRef.current;
    engineRef.current = null;
    if (engine) engine.destroy();
  }, []);

  /// Give up on MSE for the rest of this session and let the deck floor take it. Every caller has
  /// already logged its rung; this is the one place that decides the session is over for the engine.
  const fallBackToDecks = useCallback((why) => {
    if (mseFallbackRef.current) return;
    mseFallbackRef.current = true;
    diagLog("mse:fallback", { why });
    // The engine giving up entirely is worth a row. `force` is gone: this fires at most once per
    // session anyway (the ref above guarantees it), so jumping the rate limit only ever let it
    // compete with a genuine burst of other reports for the session budget.
    reportIncident("mse", { summary: `fell back to decks: ${why}`.slice(0, 400), trackId: currentRef.current?.id ?? null });
    destroyEngine();
    if (deckRef.current === "mse") {
      deckRef.current = "a";
      setActiveDeck("a");
      syncActive();
    }
  }, [destroyEngine, syncActive]);

  /**
   * Play THIS track off a deck so a seek the buffer can't serve can be honoured exactly — then give
   * the queue back to the engine at the next track.
   *
   * Deliberately not `fallBackToDecks`: that is the engine having failed, is one-way for the whole
   * session, and files an incident. Nothing has failed here. The engine's buffer is small because
   * the quota is small (11.5 MB measured), and on a fat bit-perfect track that is simply less of the
   * song than the seek bar covers — a limit, not a fault.
   *
   * ⚠ The cost, stated plainly: the deck downloads the whole file (55 MB for a 5-minute FLAC), and
   * the boundary at the end of THIS track is an ordinary load rather than a pre-rolled flip, because
   * the detour suppresses the prefetch so the index change can hand control back. That is one
   * un-gapless boundary bought with a deliberate user gesture on a page that is demonstrably awake.
   * `mseFallbackRef` stays FALSE throughout, which is what makes the track-change effect restart the
   * engine — the sleep-survival guarantee is intact for the rest of the queue.
   */
  const seekDetour = useCallback((track, offsetSec, why) => {
    diagLog("mse:seek-detour", { track: track.id, to: Math.round(offsetSec), why });
    destroyEngine();
    seekDetourRef.current = track.id;
    // Off the engine's element and onto a real deck, exactly as the fallback does — but without the
    // latch that would keep us here.
    if (deckRef.current === "mse") {
      deckRef.current = "a";
      setActiveDeck("a");
      syncActive();
    }
    // loadTrack already knows how to land at an offset: it waits for `loadedmetadata` before setting
    // currentTime, on both the streamed and the downloaded-to-a-blob path.
    loadTrackRef.current(track, { resumeAt: offsetSec, autoplay: true });
  }, [destroyEngine, syncActive]);

  /// Start (or restart) the engine at the queue's current position. Restarting on a manual skip is
  /// deliberate: the buffer holds the wrong part of the queue after a jump, and re-appending from
  /// the new position is both simpler and cheaper than trying to splice.
  const startEngine = useCallback((track, autoplay) => {
    const el = audioMseRef.current;
    if (!el) return false;
    destroyEngine();
    const engine = createMseEngine({
      audio: el,
      quotaBytes: undefined,
      onAdvance: (trackId) => {
        // The boundary as bookkeeping: audio is already playing the new track from the same buffer,
        // and React is being told about it afterwards. handedOffRef is the existing signal that says
        // "the source is already this track" — the deck path invented it for exactly this shape.
        handedOffRef.current = trackId;
        loadedTrackIdRef.current = trackId;
        progressRef.current = { sec: el.currentTime || 0, at: Date.now() };
        setIndex((i) => {
          const at = queueRef.current.findIndex((t) => t.id === trackId);
          return at >= 0 ? at : i;
        });
      },
      onRung: (n, detail) => diagLog("mse:rung", { rung: n, ...(typeof detail === "object" ? detail : { detail }) }),
      // The queue-end guard (Phase 4): a stall on an ended stream at the end of the buffer IS the
      // end. Driven into the SAME handler the real `ended` event drives, because the cross-engine
      // deck flip lives there and must not have a second, subtly different spelling.
      onStreamEnded: () => {
        diagLog("mse:ended-by-guard", { track: currentRef.current?.id ?? null, hidden: document.hidden });
        endedRef.current({ currentTarget: audioMseRef.current });
      },
      onDeckNeeded: (nextTrack, payload) => {
        // The engine cannot carry the next track. Prepare the deck NOW — well before the buffer runs
        // out — so the join is a pre-rolled flip and not a load at the boundary (§the invariant).
        diagLog("mse:deck-needed", { track: nextTrack?.id ?? null });
        if (nextTrack && payload?.url) installOnIdleDeck(nextTrack, payload.url, Number(payload.sizeBytes) || 0);
        mseHandoffRef.current = nextTrack?.id ?? null;
      },
    });
    engineRef.current = engine;
    deckRef.current = "mse";
    setActiveDeck("mse");
    syncActive();
    mseErrorsRef.current = 0;
    queueFinishedRef.current = false;
    // ⚠ Claim the element SYNCHRONOUSLY, before the engine's async start resolves. Measured in a
    // real browser: a tap on Play in that window found `loadedTrackIdRef` still empty, decided the
    // element was holding nothing, and ran the DECK load — which assigned a signed URL over the
    // blob: src and detached the MediaSource out from under the engine. The engine then appended
    // into a buffer nobody was listening to and re-fetched the same track ~2000 times.
    loadedTrackIdRef.current = track.id;
    lastUrlRef.current = null;
    engine.start({ queue: queueRef.current, index: queueRef.current.findIndex((t) => t.id === track.id) })
      .then(() => {
        loadedTrackIdRef.current = track.id;
        lastUrlRef.current = null;
        if (!autoplay) {
          // ⚠ Only if it is still true. `autoplay` is an intent from when this start was ORDERED,
          // and the engine takes a moment to open its MediaSource and append — long enough for the
          // listener to press Play, which they do, because the bar appears the instant the queue is
          // restored. Asserting the stale intent here left the element playing with the button
          // still showing ▶ for the rest of the session (measured in a browser: `play` and
          // `playing` both fired, and React's state said paused).
          if (el.paused) setPlaying(false);
          return;
        }
        el.volume = volumeOf();
        el.muted = false;
        el.play().catch(() => {
          if (document.hidden) resumeOnWakeRef.current = true;
          setPlaying(false);
        });
      })
      .catch((e) => {
        // The engine could not even start (no treatment, no MediaSource, the first mint failed).
        // That is rung 7 territory: hand the whole session to the decks, which is where it would
        // have been without the flag.
        fallBackToDecks(String(e && e.message ? e.message : e).slice(0, 80));
        loadTrackRef.current(currentRef.current, { autoplay });
      });
    return true;
  }, [destroyEngine, fallBackToDecks, installOnIdleDeck, syncActive, volumeOf]);

  // Load + play whenever the current track changes. The signed URL comes from Stream/Start;
  // the <audio> element then streams straight off the gateway (Range requests, native decode).
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    // Whatever recovery the previous track had in flight dies with it.
    clearPendingRecovery();
    if (!current) {
      audio.pause();
      audio.removeAttribute("src");
      loadedTrackIdRef.current = null;
      resumeOnWakeRef.current = false;
      return;
    }
    // A new pick is a clean slate: this track has spent none of its recovery budget.
    recoveryRef.current = { trackId: current.id, attempts: 0, attemptedAtSec: 0, parkBeats: 0 };
    // A seek detour lasts exactly one track, and we have left that track — played out, skipped, or
    // replaced by a different pick. Cleared HERE, above every early return below, because a detour
    // flag that outlives its track would go on suppressing the prefetch for the rest of the session.
    seekDetourRef.current = null;
    const autoplay = !suppressAutoplayRef.current;
    suppressAutoplayRef.current = false;
    // `ended` already put this track's source on the element and started it. Loading it again would
    // undo the whole point of the hand-off — a fresh Stream/Start, a fresh src, and the silent gap
    // back — so this effect only does the bookkeeping above and lets the audio run.
    if (handedOffRef.current === current.id) {
      handedOffRef.current = null;
      return;
    }
    handedOffRef.current = null; // a pick that jumped elsewhere: the stale claim can't outlive it
    // …and a deck pre-rolled for a boundary that is no longer coming must be stopped, or it keeps
    // playing (silently) underneath the track the listener actually picked.
    cancelPreroll();
    // The engine, when this session has one and hasn't given up on it. A jump lands here (the
    // engine restarts at the new position); an advance the ENGINE made never does, because it set
    // handedOffRef above and returned already.
    if (engineModeRef.current === "mse" && !mseFallbackRef.current) {
      if (startEngine(current, autoplay)) return;
    }
    // The resume flag is NOT cleared here. It used to be, and that was the bug that stranded the
    // album at a track boundary with no error and a dead play button: this line ran, then the load
    // below never landed on a backgrounded phone, and the wake that would have rescued it found the
    // flag already false. loadTrack sets the flag from the state it is actually loading in, and
    // onPlay clears it once the element is genuinely playing — so it is owned by the two moments
    // that know the truth, not by a pick that only knows the intent.
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
      // The playhead is moving: whatever recovery was queued is for a stream that no longer needs
      // one (an element that healed itself, or the retry that just took). Cancel it before it
      // re-loads a working stream out from under the listener.
      if (pendingRecoveryRef.current) clearPendingRecovery();
      // A track that has genuinely got going clears the "everything is failing" count…
      if (sec > 3) consecutiveFailuresRef.current = 0;
      // …and one that has run cleanly for a while earns its recovery budget back, so a long track
      // on a flaky link isn't written off for troubles it already recovered from. This can't spin:
      // refunding requires progress, and a failure loop makes none.
      const state = recoveryRef.current;
      if ((state.attempts > 0 || state.parkBeats > 0) && sec - state.attemptedAtSec > RECOVERY_RESET_SEC) {
        state.attempts = 0;
        state.parkBeats = 0;
      }
      // Fetch the NEXT track as early as this one is properly under way. The old trigger was a 30s
      // lead, which is plenty to mint a URL but nowhere near enough to download a track — and the
      // download is the point: it has to finish while the page is still awake and allowed to use
      // the network. Driven off timeupdate rather than a timer because it's the one clock that
      // can't run while the audio isn't, so it never fires on a paused or seeking element.
      // With the engine live, the next track is already IN the buffer (or being appended into it):
      // there is no boundary to prepare a deck for, and downloading the track twice would be a
      // second copy of every album on a phone's radio. The one exception is a cross-engine flip,
      // which the engine announces through onDeckNeeded and which prepares the deck itself.
      if (mseActive()) {
        engineRef.current.pump();
        return;
      }
      // NB: a seek detour does NOT skip this. The preparation is what makes a boundary survivable
      // on a sleeping phone, and it must happen while the page is still awake enough to fetch —
      // deciding whether to USE it belongs at the boundary, where the answer is known. See onEnded.
      const duration = audio.duration;
      const nearEnd = Number.isFinite(duration) && duration > 0 && duration - sec <= PREFETCH_LEAD_SEC;
      if (sec >= PRELOAD_START_SEC || nearEnd) prefetchNext();
      // Close to the boundary, hand the fetching to the media stack: start the next deck muted so
      // there is never a moment with no audio playing (see PREROLL_LEAD_SEC).
      const upcomingId = nextTrackRef.current?.id;
      if (upcomingId && Number.isFinite(duration) && duration > 0
          && duration - sec <= PREROLL_LEAD_SEC) {
        prerollIdleDeck(upcomingId);
      }
    };
    audio.addEventListener("timeupdate", onTime);
    return () => audio.removeEventListener("timeupdate", onTime);
  }, [clearPendingRecovery, prefetchNext, prerollIdleDeck, activeDeck, mseActive]);

  // ── The engine's execution opportunities (§"The clock rule") ───────────────────────────────────
  // `timeupdate` above is the awake accelerator; these are the triggers the plan says survive a
  // hidden page — the completion of our own append, the element noticing it needs data, the page
  // being looked at again — plus an interval that is admitted to be useless while asleep. Every one
  // of them does ALL currently-possible work, because the next one is not schedulable.
  useEffect(() => {
    if (engineModeRef.current !== "mse") return undefined;
    const el = audioMseRef.current;
    if (!el) return undefined;
    const pump = () => { if (mseActive()) engineRef.current.pump(); };
    const events = ["progress", "waiting", "playing", "canplay", "stalled"];
    events.forEach((name) => el.addEventListener(name, pump));
    document.addEventListener("visibilitychange", pump);
    window.addEventListener("online", pump);
    const timer = setInterval(pump, 5000);
    return () => {
      events.forEach((name) => el.removeEventListener(name, pump));
      document.removeEventListener("visibilitychange", pump);
      window.removeEventListener("online", pump);
      clearInterval(timer);
    };
  }, [mseActive]);

  // Every raw media event, recorded with the element's state at that instant (musicDiag). This is
  // the only witness to a failure that happens with the screen off — `error` in particular carries
  // the MediaError code, which is what separates "the file won't decode" from "the request for it
  // never succeeded". Off unless ?diag=1, and it never touches playback.
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !diagEnabled()) return undefined;
    const handlers = MEDIA_EVENTS.map((name) => {
      const fn = () => diagLog(name, snapshotAudio(audio));
      audio.addEventListener(name, fn);
      return [name, fn];
    });
    const onVis = () => diagLog("visibility", { state: document.visibilityState, ...snapshotAudio(audio) });
    const onOnline = () => diagLog("online", snapshotAudio(audio));
    const onOffline = () => diagLog("offline", snapshotAudio(audio));
    document.addEventListener("visibilitychange", onVis);
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);
    return () => {
      handlers.forEach(([name, fn]) => audio.removeEventListener(name, fn));
      document.removeEventListener("visibilitychange", onVis);
      window.removeEventListener("online", onOnline);
      window.removeEventListener("offline", onOffline);
    };
  }, [activeDeck]);

  // Nothing may outlive the player: an object URL kept past unmount pins its buffer forever.
  useEffect(() => () => {
    preloadAbortRef.current?.abort();
    revokeDeck("a");
    revokeDeck("b");
    destroyEngine();
  }, [revokeDeck, destroyEngine]);

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
      // Hydrated either way: "there was nothing stored" is just as final an answer as a restore,
      // and both release the persist effect below.
      hydratedRef.current = true;
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
    // NOT before the restore above has had its turn. `enabled` is `!!userData?.hasPassword` and
    // App.js starts userData at null, so the provider's first render is ALWAYS disabled — the
    // restore bails, and an unguarded persist then read the still-empty queue as "the listener has
    // no queue" and deleted the stored one. The queue could never survive a reload, for anybody.
    // An empty queue only means "cleared" once we know it isn't just "not loaded yet".
    if (!hydratedRef.current) return;
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
    queueFinishedRef.current = false;
    // A deliberate new pick is a new session, so an engine that gave up on the LAST queue gets
    // another turn. Mid-queue it stays given-up (see onEnded) — the difference is that this one is a
    // user gesture, not a boundary.
    mseFallbackRef.current = false;
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
  const failTrack = useCallback((message, { firstHand = false, networkLevel = false } = {}) => {
    const track = currentRef.current;
    if (!track) return; // nothing loaded (e.g. the element erroring as stop() clears its src)
    if (pendingRecoveryRef.current) return; // a recovery is already queued for this failure
    if (recoveryRef.current.trackId !== track.id) {
      recoveryRef.current = { trackId: track.id, attempts: 0, attemptedAtSec: 0, parkBeats: 0 };
    }
    const state = recoveryRef.current;
    const hasNext = index + 1 < queue.length;

    // The phone is napping, not the server failing. Hold position and wait for the world to
    // change: a slow heartbeat while it lasts, and an immediate retry on wake or `online`. No
    // budget is spent — the budget exists for a server that keeps refusing, and burning it on a
    // Wi-Fi doze is how one bad second used to end the whole album.
    if (shouldPark({ networkLevel, hidden: document.hidden, offline: navigator.onLine === false })) {
      state.parkBeats += 1;
      state.attemptedAtSec = progressRef.current.sec; // start the refund clock from here, not from 0:00
      setError("Playback interrupted — waiting for the connection to come back.");
      // Report the FIRST beat only. Parking is a heartbeat: reporting every one would turn a
      // two-minute Wi-Fi doze into a hundred rows saying the same thing.
      if (state.parkBeats === 1) {
        diagLog("park", { track: track.id, sec: Math.round(progressRef.current.sec), networkLevel });
        reportIncident("park", {
          summary: `parked: ${message}`.slice(0, 400),
          trackId: track.id,
        });
      }
      schedulePendingRecovery(track.id, state.parkBeats <= PARKED_MAX_BEATS ? PARKED_RETRY_MS : null);
      return;
    }

    switch (recoveryDecision({
      attempts: state.attempts,
      consecutiveFailures: consecutiveFailuresRef.current,
      hasNext,
    })) {
      case "retry":
        // A fresh URL, resumed just behind where it died — a re-mint costs one round trip and is
        // the only thing that helps when the old connection is what broke. After a short wait: an
        // instant retry against a hiccup fails as instantly as the first attempt did, and two
        // instant failures spend the whole budget inside the same bad second.
        state.attempts += 1;
        state.attemptedAtSec = progressRef.current.sec;
        schedulePendingRecovery(track.id, retryDelayMs(state.attempts - 1));
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
          if (!why) return;
          // A healthy host has nothing to add to an account the element already gave first-hand —
          // and overwriting "the connection dropped" with "the host answered fine" is what made a
          // client-side bug read as a server outage. Only a probe that FOUND something speaks over it.
          if (firstHand && why === HOST_HEALTHY) return;
          setError(`Playback stopped — ${why}.`);
        });
      }
    }
  }, [index, queue.length, schedulePendingRecovery, next]);

  // Kept in a ref so loadTrack — defined earlier, and deliberately dependency-free — can reach it.
  useEffect(() => { failTrackRef.current = failTrack; }, [failTrack]);
  useEffect(() => { loadTrackRef.current = loadTrack; }, [loadTrack]);

  // The two moments the world changes back: the page becomes visible (the listener picked the phone
  // up) and the browser regains a network. Both fire whatever recovery was parked, so the session
  // heals the instant it CAN rather than on the next heartbeat — and a play() that was refused on a
  // hidden page gets its retry, which only a visible page can grant.
  useEffect(() => {
    const onWake = () => {
      if (document.hidden) return;
      diagLog("wake", { armed: resumeOnWakeRef.current, loaded: loadedTrackIdRef.current, current: currentRef.current?.id ?? null, finished: queueFinishedRef.current });
      // A queue that finished stays finished. Waking is not a reason to play anything: the listener
      // left it playing, it played to the end, and picking the phone up should show that — not the
      // last track back at 0:00, which is what it did before this guard.
      if (queueFinishedRef.current) {
        firePendingRecovery();
        return;
      }
      if (resumeOnWakeRef.current) {
        resumeOnWakeRef.current = false;
        const track = currentRef.current;
        // Only play() the element if what it holds IS the current track. If the load that was
        // supposed to fetch this track never landed while the page was frozen, the element is still
        // sitting on the PREVIOUS track's spent URL — play()ing that either replays a finished song
        // or rejects into a silent catch, which is how a wedged player looked healthy and did
        // nothing. Re-drive the load instead: it is the step that never happened.
        if (track && loadedTrackIdRef.current !== track.id) loadTrackRef.current(track, { autoplay: true });
        else audioRef.current?.play().catch(() => { /* still refused: the bar honestly shows Play */ });
      }
      firePendingRecovery();
    };
    // `online` fires on hidden pages too, and that's wanted: the music should come back while the
    // phone is still in a pocket, not when it's finally looked at.
    const onOnline = () => firePendingRecovery();
    document.addEventListener("visibilitychange", onWake);
    window.addEventListener("online", onOnline);
    return () => {
      document.removeEventListener("visibilitychange", onWake);
      window.removeEventListener("online", onOnline);
    };
  }, [firePendingRecovery]);

  // Stall watchdog. There is no event for "the bytes stopped arriving and nobody said anything":
  // `stalled`/`waiting` fire inconsistently across browsers, and not at all when a connection dies
  // quietly. The honest test is that the playhead has not moved while the element still claims to be
  // playing.
  //
  // ⚠ The watchdog's clock is the thing it must be most careful about. It measures WALL time since
  // the playhead last moved — but on a phone whose screen has gone off, the page stops running:
  // `timeupdate` stops being delivered and this very interval stops firing, while the audio plays on
  // perfectly. Wall time then measures how long the page was asleep, not how long the stream was
  // silent. Reading that as a stall is how leaving an album playing and walking away reliably ended
  // in "Playback stopped" a couple of minutes later: the watchdog tore `src` off a healthy stream,
  // the re-mint couldn't restart on a backgrounded page, the recovery budget burned out, and the
  // HEAD probe then truthfully reported that the host was fine — because nothing was ever wrong with
  // it. Three guards keep the watchdog from firing on its own blind spots:
  //
  //   1. `document.hidden` — while the page is hidden we cannot trust our clock at all, so we do not
  //      guess. A stream that really dies while hidden still fires the element's `error` event,
  //      which goes through the same recovery; the watchdog is only for silent death.
  //   2. A tick that came in late — the renderer was throttled or frozen (device sleep, heavy GC),
  //      even if it never reported itself hidden. Same reasoning, caught a different way.
  //   3. An element that is still LOADING is fetching, not stuck. Large files on slow links rebuffer
  //      routinely; that earns a much longer grace than a connection that has gone quiet.
  //
  // In every skipped case the progress mark is re-armed, so the grace period starts fresh from the
  // moment the page is running again rather than firing instantly on the first tick back.
  useEffect(() => {
    if (!current) return undefined;
    let lastTick = Date.now();
    const id = setInterval(() => {
      const now = Date.now();
      const sinceTick = now - lastTick;
      lastTick = now;
      const audio = audioRef.current;
      if (!audio || audio.paused || audio.ended || audio.seeking) return;
      // A recovery is already queued (a backoff waiting out, or a park waiting on the world).
      // Judging the same dead stream again would spend budget the pending retry may make moot.
      if (pendingRecoveryRef.current) return;
      const verdict = stallVerdict({
        hidden: document.hidden,
        sinceTickMs: sinceTick,
        sinceProgressMs: now - progressRef.current.at,
        loading: audio.networkState === HTMLMediaElement.NETWORK_LOADING,
      });
      if (verdict === "wait") return;
      // Re-arm before handing off, so a reload in flight isn't re-triggered on the next poll — and
      // so a page that has just woken judges the stream from now rather than from before it slept.
      progressRef.current = { ...progressRef.current, at: now };
      // ⚠ With the engine live, the deck recovery is the WRONG cure and an actively destructive one:
      // it would assign a signed URL over the element's blob: src, which throws the whole MediaSource
      // (and every buffered second of the queue) away. A stalled MSE element means the buffer ran
      // dry, and the answer to that is to append — so pump, and report it, because reaching here
      // means the window arithmetic was wrong somewhere and that must surface rather than vanish
      // into a recovered gap.
      if (verdict === "fail" && mseActive()) {
        diagLog("mse:dry", { sec: Math.round(progressRef.current.sec) });
        reportIncident("mse", {
          summary: `buffer ran dry while ${document.hidden ? "hidden" : "visible"}`,
          trackId: currentRef.current?.id ?? null,
        });
        engineRef.current.pump();
        return;
      }
      // networkLevel: the track was decoding fine until the bytes stopped — that's a connection's
      // account, not a file's, so an offline browser parks it instead of spending budget.
      if (verdict === "fail") failTrackRef.current("Playback stopped — the stream isn't answering.", { networkLevel: true });
    }, STALL_POLL_MS);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id]);

  /**
   * Where we are IN THIS TRACK, and how long it is — the one mapping (music-mse-plan.md §Phase 3).
   *
   * Under the engine the element's clock counts QUEUE-seconds, because the whole queue is one
   * SourceBuffer. Every consumer that reads `audio.currentTime` and means "how far into this song"
   * has to come through here instead: the play bar, the elapsed/total labels, the lyrics scroller
   * and the lock-screen scrubber. Without it the bar reads 43-minute positions and the lyrics scroll
   * to the wrong line — which is why this, not the engine, was the blocker to using the flag daily.
   *
   * On the deck path it is exactly what it always was: one element, one track, one clock.
   */
  const trackTime = useCallback(() => {
    const audio = audioRef.current;
    if (!audio) return { position: 0, duration: 0 };
    const elementTime = audio.currentTime || 0;
    const engine = engineRef.current;
    if (mseActive() && engine) {
      const mapped = trackTimeAt(engine.timeline().entries, elementTime);
      if (mapped) {
        return {
          position: mapped.offsetSec,
          // The buffer's own answer first, then the catalog's — a track whose append is still in
          // flight has no measured length yet, and showing 0:00 total for it would be worse than
          // showing what the queue says.
          duration: mapped.durationSec || Number(currentRef.current?.durationSec) || 0,
        };
      }
    }
    return {
      position: elementTime,
      duration: Number.isFinite(audio.duration) ? audio.duration : (Number(currentRef.current?.durationSec) || 0),
    };
  }, [mseActive]);

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
    queueFinishedRef.current = false;
    const audio = audioRef.current;
    if (!audio) return;
    // A recovery is waiting on a heartbeat or a wake — the tap IS the wake. Fire it now: what this
    // element holds is a dead source, and play() on a corpse either rejects or replays the error.
    if (pendingRecoveryRef.current) {
      firePendingRecovery();
      return;
    }
    // The tap must never be a no-op. If the element holds nothing, or holds a track the queue has
    // already moved past — a load that never landed on a backgrounded phone leaves exactly that —
    // then play() would silently reject on a spent URL and the button would look broken. Load what
    // the player actually says is current instead.
    const track = currentRef.current;
    if (track && (!audio.src || loadedTrackIdRef.current !== track.id)) {
      loadTrackRef.current(track, { autoplay: true });
      return;
    }
    if (!audio.src) return;
    if (audio.paused) audio.play().catch(() => {});
    else audio.pause();
  }, [firePendingRecovery]);

  const seek = useCallback((seconds) => {
    const audio = audioRef.current;
    if (!audio || !Number.isFinite(seconds)) return;
    const engine = engineRef.current;
    if (mseActive() && engine) {
      // `seconds` is TRACK-relative (it comes from a bar whose range is this track's duration), so
      // it has to be mapped onto the queue clock before it means anything to the element.
      const tl = engine.timeline();
      const plan = seekPlan({
        entries: tl.entries,
        bufferedStart: tl.bufferedStart,
        bufferedEnd: tl.bufferedEnd,
        trackId: currentRef.current?.id,
        offsetSec: seconds,
      });
      diagLog("mse:seek", { kind: plan.kind, to: Math.round(seconds), reason: plan.reason });
      if (plan.kind === "inBuffer") {
        // The case that must feel native, and it is: the bytes are already there, so this is a
        // local operation on a buffered range — no fetch, no src, nothing refusable.
        try { audio.currentTime = plan.elementTime; } catch { /* not seekable yet */ }
        return;
      }
      if (plan.kind === "restart" || plan.kind === "unavailable") {
        // Out of the buffer, and the engine cannot fetch "this track from 2:30" — the lanes are
        // piped ffmpeg with no Range, and a mid-file byte offset doesn't land on a frame boundary
        // anyway. This USED to restart the engine at the track, which begins it again at 0:00, and
        // that is the whole of "seeking goes back to the start of the song": on a 1568 kbps FLAC
        // the quota holds 61 s of a 297 s track, so ~77% of the seek bar was out of buffer and
        // every scrub into it silently restarted the song.
        //
        // A seek is proof the page is AWAKE, which is the one condition the engine's whole design
        // exists to survive being without. So hand this track to a deck, which downloads the file
        // and can seek anywhere natively, and let the engine have the queue back at the next track.
        const track = currentRef.current;
        if (track) seekDetour(track, seconds, plan.reason);
      }
      return;
    }
    audio.currentTime = seconds;
    // A seek moves the boundary. A deck already pre-rolled for the old one would keep playing
    // (silently) and drift away from the start it is supposed to be flipped to.
    cancelPreroll();
  }, [cancelPreroll, mseActive, seekDetour]);

  const playAt = useCallback((i) => {
    queueFinishedRef.current = false;
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

  /**
   * Throw the queue away — including the stored copy.
   *
   * Distinct from `stop()` (which the ✕ "Close player" button calls) even though the state it leaves
   * behind is the same, because the INTENT is different and one of them has to survive a reload.
   * Since §Phase 7 the queue comes back on the next visit, so a queue you are done with is no longer
   * something you can walk away from — it follows you to the next session, and the only control that
   * shrank it was ✕-per-row. This is the "and don't bring it back" button.
   *
   * The persist effect below already removes the key on an empty queue; the explicit removal is
   * belt-and-braces for the one case it can't cover — a clear issued before hydration has run, where
   * that effect is deliberately inert and the stored queue would otherwise outlive the clear.
   */
  const clearQueue = useCallback(() => {
    try { window.localStorage.removeItem(QUEUE_KEY); } catch { /* nothing stored to clear */ }
    queueFinishedRef.current = false;
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
        graphSourcesRef.current.set(audio, source);
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
    // Route the OTHER deck through the same graph. createMediaElementSource may be called only once
    // per element and permanently reroutes it, so a deck left unrouted would play silently the
    // moment it became live — the visualizer would have muted every other track.
    // ⚠ ALL THREE elements, and the engine's is not optional. createMediaElementSource may be called
    // only once per element and permanently reroutes it — so an element left unrouted plays SILENTLY
    // the moment it becomes live. With the engine that is worse than with a deck: the MSE element
    // carries the whole queue, so missing it here would mute the rest of the session at the first
    // boundary and look exactly like the bug this design exists to remove.
    [audioARef.current, audioBRef.current, audioMseRef.current].forEach((el) => {
      if (!el || graphSourcesRef.current.has(el)) return;
      try {
        const src = graph.audioContext.createMediaElementSource(el);
        src.connect(graph.analyser);
        src.connect(graph.audioContext.destination);
        graphSourcesRef.current.set(el, src);
      } catch { /* already bound, or no Web Audio for this element */ }
    });
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
    mseFallbackRef.current = false;
    const playable = shuffled((tracks || []).filter(isPlayable));
    if (playable.length === 0) return;
    setQueue(playable);
    setIndex(0);
    setPlaying(true);
  }, [enabled, isPlayable]);

  const setVolume = useCallback((v) => {
    // EVERY element, not just the live one: the idle deck is already buffering the next track and
    // the engine's element may be pre-rolled for a cross-engine flip. Any of them can become the
    // thing playing at a boundary, and a flip that changes loudness is a bug the listener hears.
    const clamped = Math.min(Math.max(v, 0), 1);
    [audioARef.current, audioBRef.current, audioMseRef.current].forEach((el) => {
      if (el) el.volume = clamped;
    });
    window.localStorage.setItem(VOLUME_KEY, String(v));
  }, []);

  // Keep `playing` truthful to the element (covers OS media keys, autoplay refusals, errors).
  // Audio is coming out: whatever wake retry was armed has been made moot, and leaving it armed
  // would fire a pointless play() on an already-playing element the next time the phone is picked up.
  const onPlay = useCallback((e) => {
    if (e && e.currentTarget && e.currentTarget !== elFor(deckRef.current)) return;
    resumeOnWakeRef.current = false;
    setBuffering(false);
    setPlaying(true);
  }, [elFor]);
  const onPause = useCallback((e) => {
    if (e && e.currentTarget && e.currentTarget !== elFor(deckRef.current)) return;
    setPlaying(false);
  }, [elFor]);
  /// The track boundary. If the next track's URL is already in hand, the swap happens HERE —
  /// synchronously, inside the event handler — and `next()` only moves the index to match. See
  /// PREFETCH_LEAD_SEC for why every await between `ended` and `play()` is a chance for a sleeping
  /// phone to stop the album.
  const onEnded = useCallback((e) => {
    // The idle deck reaching the end of something it was only buffering is not a track boundary.
    if (e && e.currentTarget && e.currentTarget !== elFor(deckRef.current)) return;
    const audio = audioRef.current;
    const upcoming = nextTrackRef.current;
    const pre = prefetchRef.current;
    const idle = idleEl();
    const deckReady = !!(idle && upcoming && deckLoadedRef.current === upcoming.id && idle.src);
    diagLog("boundary", {
      upcoming: upcoming?.id ?? null,
      deckReady,
      deckState: idle ? idle.readyState : null,
      prefetched: !!(pre?.url && pre.trackId === upcoming?.id),
      hidden: document.hidden,
    });
    // A boundary crossed on a hidden page WITHOUT the next track already buffered is the exact
    // shape of the bug that keeps coming back: every remaining path from here needs the network at
    // the one moment the page has least licence to use it. Report it whether or not it goes on to
    // fail, because the interesting question is why the deck wasn't ready — and by the time the
    // failure is visible, the run-up has already scrolled out of anyone's reach.
    //
    // ONCE per session. This is the tripwire for a bug that is now fixed, and a tripwire only has to
    // go off once to be answered: an hour of hidden playback in a state where the deck never gets
    // ready would otherwise file a row per track, all of them saying the same sentence.
    if (upcoming && !deckReady && document.hidden && !reportedBoundaryRef.current) {
      reportedBoundaryRef.current = true;
      reportIncident("boundary", {
        summary: `boundary while hidden with no buffered deck (upcoming ${upcoming.id}, `
          + `prefetchUrl=${!!(pre?.url && pre.trackId === upcoming.id)})`,
        trackId: upcoming.id,
      });
    }

    // ── Handing a seek detour back to the engine (see seekDetour) ─────────────────────────────────
    // The detour is meant to last one track, and this boundary is where the engine takes the queue
    // back. But restarting the engine costs a mint and an append — the exact round trip a sleeping
    // phone cannot make, and the whole reason the engine exists. So the answer depends on something
    // only knowable HERE:
    //
    //   • awake  → ignore whatever was prepared and fall through to next(), which changes the index,
    //              which restarts the engine. The fetch is safe because somebody is holding the phone.
    //   • asleep → take the pre-rolled flip. Audio continues with no round trip at all, and the
    //              engine comes back at the listener's next pick instead. That leaves the rest of the
    //              queue on the deck floor — the proven pre-engine player, all four fixes intact —
    //              which is a downgrade, not a failure. Silence at 2am would be the failure.
    //
    // This is why the prefetch/preroll above is NOT suppressed during a detour: the preparation has
    // to already exist by the time we get here, because a page that has just gone quiet cannot make
    // it. Preparing and discarding costs one download; not preparing costs the album.
    const detourHandBack = !!seekDetourRef.current && !document.hidden;
    if (detourHandBack) {
      // The idle deck may be pre-rolling MUTED right now. Left running it would play the next track
      // underneath the engine's copy of it — cancelPreroll does not clear deckLoadedRef, hence the
      // explicit guards below rather than relying on deckReady going false.
      cancelPreroll();
      diagLog("mse:seek-detour-end", { upcoming: upcoming?.id ?? null, deckReady });
    }

    // Best case: the next track is already buffered on the other deck. Flip to it — no src
    // assignment, no load, no round trip. Just a play() on an element that already has the bytes.
    if (deckReady && !detourHandBack) {
      const outgoing = elFor(deckRef.current);
      const nextDeck = idleDeck();
      // A cross-engine boundary (§the invariant). `ended` on the MSE element only ever arrives after
      // the engine called endOfStream(), i.e. it decided it could not carry the next track — so the
      // flip below IS the pre-rolled hand-off to the floor, and the rest of this session belongs to
      // the decks. Coming BACK to the engine mid-queue is deliberately not attempted: restarting it
      // at a boundary would put a load exactly where this design removed one.
      if (deckRef.current === "mse") {
        diagLog("mse:flip-to-deck", { track: upcoming?.id ?? null, deck: nextDeck, hidden: document.hidden });
        mseFallbackRef.current = true;
        destroyEngine();
        mseHandoffRef.current = null;
      }
      deckRef.current = nextDeck;
      setActiveDeck(nextDeck);
      const live = syncActive();
      handedOffRef.current = upcoming.id; // the track-change effect must not re-load this
      loadSeqRef.current += 1;            // any load still in flight for the old track is void
      lastUrlRef.current = prefetchRef.current?.url || lastUrlRef.current;
      trackChannelsRef.current = prefetchRef.current?.channels || 0;
      applyOutputChannels();
      progressRef.current = { sec: 0, at: Date.now() };
      loadedTrackIdRef.current = upcoming.id;
      deckLoadedRef.current = null;
      prefetchRef.current = null;
      live.volume = volumeOf();
      // Pre-rolled? Then it is ALREADY playing and the page never went quiet. Rewind it to the
      // start and unmute — both local operations on bytes the element already holds, with no
      // play() to be refused and no network to be asked for at the one moment it can't be.
      const wasPrerolled = prerollRef.current === upcoming.id;
      prerollRef.current = null;
      if (wasPrerolled) {
        try { live.currentTime = 0; } catch { /* not seekable yet: it plays from where it is */ }
        live.muted = false;
        diagLog("boundary:preroll-flip", { track: upcoming.id, paused: live.paused });
        setPlaying(true);
        // A pre-roll that was paused out from under us (the browser reclaiming a second stream)
        // still has to start; play() here is a no-op when it is already running.
        if (live.paused) {
          live.play().catch(() => {
            if (document.hidden) resumeOnWakeRef.current = true;
            setPlaying(false);
          });
        }
      } else {
        live.muted = false;
        live.play().catch(() => {
          if (document.hidden) resumeOnWakeRef.current = true;
          setPlaying(false);
        });
      }
      // Park the outgoing deck and release the track it was holding. An object URL that is never
      // revoked pins its whole ArrayBuffer for the life of the page — two 40 MB tracks per album
      // side would be a leak measured in hundreds of megabytes on a phone.
      try { outgoing.pause(); } catch { /* already gone */ }
      revokeDeck(nextDeck === "a" ? "b" : "a");
      next();
      return;
    }

    // Fallbacks, in order: a URL in hand (swap it onto the live deck synchronously), then an
    // ordinary load driven by the index change. Both are worse than a flip, and both are kept
    // because a boundary must never depend on the optimisation having worked.
    // Nothing to go to: the queue is done. Latch it, so a wake hours later comes back to a finished
    // player rather than silently restarting the last song at 0:00 (field run, 00:58). `next()`
    // below already stops playback; this is what stops the WAKE from undoing that.
    if (!upcoming) {
      queueFinishedRef.current = true;
      resumeOnWakeRef.current = false;
      diagLog("queue-finished", { track: currentRef.current?.id ?? null, engine: deckRef.current });
    }

    if (audio && upcoming && pre?.url && pre.trackId === upcoming.id && !detourHandBack) {
      prefetchRef.current = null;
      handedOffRef.current = upcoming.id; // the effect below must not re-load what's already playing
      loadSeqRef.current += 1;            // and any load still in flight for the old track is void
      lastUrlRef.current = pre.url;
      trackChannelsRef.current = pre.channels;
      applyOutputChannels();              // width set before the first buffer decodes, as in loadTrack
      progressRef.current = { sec: 0, at: Date.now() };
      audio.src = pre.url;
      loadedTrackIdRef.current = upcoming.id;
      audio.play().catch(() => {
        // Same reading as loadTrack's: refused with nobody looking means retry on the next wake,
        // not that the listener has gone.
        if (document.hidden) resumeOnWakeRef.current = true;
        setPlaying(false);
      });
    }
    next();
  }, [applyOutputChannels, next, elFor, idleEl, idleDeck, syncActive, volumeOf, revokeDeck, destroyEngine, cancelPreroll]);
  // The element gave up on this source. That used to end the listening session; now it goes through
  // the same bounded recovery as everything else. The message is only reached once retries and a
  // skip are exhausted — it says what actually happened rather than the bare "Playback failed."
  const onError = useCallback((e) => {
    // A preload that fails is not a playback failure. Drop the buffered claim so the boundary falls
    // back to a real load, and say nothing to the listener — their track is still playing fine.
    if (e && e.currentTarget && e.currentTarget !== elFor(deckRef.current)) {
      diagLog("preload:failed", { deck: e.currentTarget.dataset?.deck, track: deckLoadedRef.current });
      deckLoadedRef.current = null;
      return;
    }
    // Rung 6: a MediaError no longer condemns one track — there is no `src` to swap, so it ends the
    // whole MediaSource. Rebuild ONCE from the current track; a second death on the same session
    // means the file or the browser is beating us and the decks take over.
    if (mseActive()) {
      mseErrorsRef.current += 1;
      const track = currentRef.current;
      diagLog("mse:element-error", { code: audioRef.current?.error?.code ?? null, attempt: mseErrorsRef.current });
      if (mseErrorsRef.current <= 1 && track) {
        reportIncident("mse", { summary: `MediaError ${audioRef.current?.error?.code ?? "?"} — rebuilding once`, trackId: track.id });
        startEngine(track, true);
      } else {
        fallBackToDecks(`MediaError ${audioRef.current?.error?.code ?? "?"} twice`);
        if (track) loadTrackRef.current(track, { autoplay: true });
      }
      return;
    }
    const err = audioRef.current?.error;
    const reason = mediaErrorReason(err);
    // MEDIA_ERR_NETWORK (2) is the element saying the connection went, which a sleeping phone does
    // routinely — park-able. Decode (3) and format (4) failures are about the FILE and get no such
    // patience: they will fail identically when the network returns.
    const networkLevel = err?.code === 2;
    if (reason) failTrackRef.current(`Playback stopped — ${reason}.`, { firstHand: true, networkLevel });
    else failTrackRef.current("Playback failed — the stream isn't answering.", { networkLevel });
  }, [elFor, mseActive, startEngine, fallBackToDecks]);

  // The queue-end guard's landing point. The engine is created above `onEnded` is defined, so it
  // calls through this ref — exactly the indirection failTrack/loadTrack already use. Without the
  // assignment the guard fires, logs, and lands on a no-op: measured in a browser, where the drained
  // stream was correctly identified as ended and then nothing happened at all.
  useEffect(() => { endedRef.current = onEnded; }, [onEnded]);

  // OS lock-screen / media-key card. The shared hook only touches standard HTMLMediaElement
  // APIs, so the <audio> ref rides the videoRef parameter unchanged.
  useMediaSession({
    videoRef: audioRef,
    // Per-TRACK position on the lock screen, not per-queue (music-mse-plan.md §Phase 3).
    positionOverride: trackTime,
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
    () => ({ queue, index, current, playing, error, buffering, audioRef, trackTime, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, clearQueue, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks, favoriteIds, isFavorite, toggleFavorite, lyricsSettings, setLyricsSetting }),
    [queue, index, current, playing, error, buffering, trackTime, canTranscode, isPlayable, playTracks, enqueue, next, prev, toggle, seek, playAt, removeAt, stop, clearQueue, setVolume, ensureAudioGraph, visualizerOn, toggleVisualizer, closeVisualizer, lyricsOn, toggleLyrics, closeLyrics, shuffleTracks, favoriteIds, isFavorite, toggleFavorite, lyricsSettings, setLyricsSetting]
  );

  return (
    <MusicPlayerContext.Provider value={value}>
      {children}
      {/* crossOrigin: required for the future Web Audio graph (visualizer §2.8) — a
          MediaElementAudioSourceNode over a CORS-tainted source outputs silence. */}
      {/* Two decks. Only the live one is ever played; the other is buffering the next track.
          Every handler ignores events from the idle deck — its loads, stalls and errors are
          preparation, not playback, and must never be mistaken for the listener's stream. */}
      <audio
        ref={audioARef}
        data-deck="a"
        crossOrigin="anonymous"
        onPlay={onPlay}
        onPause={onPause}
        onEnded={onEnded}
        onError={onError}
      />
      <audio
        ref={audioBRef}
        data-deck="b"
        crossOrigin="anonymous"
        onPlay={onPlay}
        onPause={onPause}
        onEnded={onEnded}
        onError={onError}
      />
      {/* The engine's element (§Phase 2). Always rendered, never used unless the flag is on: it must
          exist before the engine starts (a MediaSource needs an element to attach to) and it must be
          the SAME element for the session's whole life, since createMediaElementSource can only ever
          bind it once. Its `ended` — which only fires after endOfStream() — is a cross-engine
          boundary, and the deck flip machinery handles it because this is registered as a deck. */}
      <audio
        ref={audioMseRef}
        data-deck="mse"
        crossOrigin="anonymous"
        onPlay={onPlay}
        onPause={onPause}
        onEnded={onEnded}
        onError={onError}
      />
      {enabled && <MusicMiniPlayer />}
      <MusicDiagPanel />
    </MusicPlayerContext.Provider>
  );
}
