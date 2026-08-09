// A recorder for playback failures that happen when nobody is looking.
//
// The music bugs worth chasing all happen on a phone with the screen off: the album stops at a
// track boundary, or a stream dies mid-song. By the time anyone can look, the interesting moment is
// minutes gone — and mobile Chrome has no console to have been watching it. chrome://inspect isn't
// always reachable either.
//
// So this keeps a bounded in-memory ring of timestamped events, written from the player's own
// lifecycle AND from every raw media event, and readable (and copyable) after the fact. Wall-clock
// timestamps on purpose: the gap between two entries is how we see the renderer was frozen.
//
// OFF by default and a no-op when off — `?diag=1` turns it on and remembers, `?diag=0` turns it off.

const MAX_ENTRIES = 500;
const KEY = "music.diag";

let enabled = false;
let entries = [];
let nextId = 1;
const listeners = new Set();

// Resolve the flag once, at module load: ?diag= wins and is remembered, otherwise localStorage.
try {
  const q = new URLSearchParams(window.location.search).get("diag");
  if (q === "1" || q === "0") {
    enabled = q === "1";
    if (enabled) window.localStorage.setItem(KEY, "1");
    else window.localStorage.removeItem(KEY);
  } else {
    enabled = window.localStorage.getItem(KEY) === "1";
  }
} catch {
  enabled = false;
}

export function diagEnabled() {
  return enabled;
}

export function setDiagEnabled(on) {
  enabled = !!on;
  try {
    if (enabled) window.localStorage.setItem(KEY, "1");
    else window.localStorage.removeItem(KEY);
  } catch { /* private mode: the flag just won't survive a reload */ }
  emit();
}

/** MediaError codes, spelled out — `err: 4` means nothing at 3am on a phone. */
export const MEDIA_ERROR_NAMES = {
  1: "ABORTED",
  2: "NETWORK",
  3: "DECODE",
  4: "SRC_NOT_SUPPORTED",
};

const NETWORK_STATES = ["EMPTY", "IDLE", "LOADING", "NO_SOURCE"];
const READY_STATES = ["NOTHING", "METADATA", "CURRENT", "FUTURE", "ENOUGH"];

/** The element's state, in words. Cheap enough to take on every entry. */
export function snapshotAudio(audio) {
  if (!audio) return null;
  return {
    src: (audio.src || "").slice(-28),
    paused: audio.paused,
    ended: audio.ended,
    t: Math.round(audio.currentTime || 0),
    ready: READY_STATES[audio.readyState] ?? audio.readyState,
    net: NETWORK_STATES[audio.networkState] ?? audio.networkState,
    err: audio.error ? (MEDIA_ERROR_NAMES[audio.error.code] || audio.error.code) : null,
  };
}

function emit() {
  listeners.forEach((fn) => {
    try { fn(); } catch { /* a broken subscriber must not break logging */ }
  });
}

/**
 * Record one event. `data` is any small object worth keeping — keep it small, this runs on
 * timeupdate-adjacent paths.
 */
export function diagLog(event, data) {
  if (!enabled) return;
  entries.push({
    id: nextId++,
    at: Date.now(),
    hidden: typeof document !== "undefined" ? document.visibilityState === "hidden" : null,
    event,
    data: data || null,
  });
  if (entries.length > MAX_ENTRIES) entries = entries.slice(-MAX_ENTRIES);
  emit();
}

export function diagList() {
  return entries;
}

export function clearDiag() {
  entries = [];
  emit();
}

export function subscribeDiag(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

function stamp(ms) {
  const d = new Date(ms);
  const p = (n, w = 2) => String(n).padStart(w, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

/**
 * The whole log as pasteable text. The gap column is the point: a multi-second jump between two
 * entries is the renderer having been frozen, which is invisible in the events themselves.
 */
export function diagText() {
  if (entries.length === 0) return "(no events recorded)";
  const lines = [
    `music diag — ${entries.length} events — ${navigator.userAgent}`,
    "",
  ];
  entries.forEach((e, i) => {
    const gap = i === 0 ? 0 : e.at - entries[i - 1].at;
    const gapText = gap >= 1000 ? ` +${(gap / 1000).toFixed(1)}s` : "";
    const where = e.hidden ? " [hidden]" : "";
    const data = e.data ? ` ${JSON.stringify(e.data)}` : "";
    lines.push(`${stamp(e.at)}${gapText}${where} ${e.event}${data}`);
  });
  return lines.join("\n");
}

/** Raw media events worth recording. `timeupdate` is deliberately NOT here — it would drown the ring. */
export const MEDIA_EVENTS = [
  "loadstart", "loadedmetadata", "canplay", "canplaythrough", "play", "playing",
  "pause", "waiting", "stalled", "suspend", "abort", "emptied", "error", "ended",
];
