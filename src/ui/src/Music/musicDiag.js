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
const RING_KEY = "music.diag.ring";
// The ring has to survive the thing it is recording. When playback fails on a sleeping phone the
// page is reloaded (by the browser, or by the listener) and an in-memory ring dies with it — which
// is why every previous attempt to catch this asked someone to be looking at the exact moment, and
// why that never once worked. Persisted, bounded, and small enough to write on every event.
const PERSIST_ENTRIES = 200;

// Events that are recorded even with diagnostics OFF. These are the ones that describe a failure
// and its run-up; the raw media firehose stays behind ?diag=1.
const ALWAYS = new Set([
  "boundary", "error", "park", "recover", "wake", "give-up",
  "preload:ready", "preload:stream", "preload:failed", "preload:fetch",
  "load:failed", "load:minted", "load:download", "load:downloaded", "visibility",
]);

let enabled = false;
let entries = [];
let nextId = 1;
const listeners = new Set();

function persist() {
  try {
    const keep = entries.slice(-PERSIST_ENTRIES);
    window.localStorage.setItem(RING_KEY, JSON.stringify(keep));
  } catch { /* quota or private mode: the in-memory ring still works for this session */ }
}

function restore() {
  try {
    const raw = window.localStorage.getItem(RING_KEY);
    if (!raw) return;
    const prev = JSON.parse(raw);
    if (Array.isArray(prev) && prev.length) {
      entries = prev;
      nextId = Math.max(...prev.map((e) => e.id || 0)) + 1;
    }
  } catch { /* unreadable ring is no ring */ }
}

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

// Bring back whatever the last page life recorded, so a reload doesn't erase the failure that
// caused it.
restore();

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
  // Failure-shaped events are always recorded. Everything else needs ?diag=1.
  if (!enabled && !ALWAYS.has(event)) return;
  entries.push({
    id: nextId++,
    at: Date.now(),
    hidden: typeof document !== "undefined" ? document.visibilityState === "hidden" : null,
    event,
    data: data || null,
  });
  if (entries.length > MAX_ENTRIES) entries = entries.slice(-MAX_ENTRIES);
  persist();
  emit();
}

export function diagList() {
  return entries;
}

export function clearDiag() {
  entries = [];
  try { window.localStorage.removeItem(RING_KEY); } catch { /* nothing to clear */ }
  emit();
}

// ── Self-reporting ──────────────────────────────────────────────────────────
//
// The player uploads its own log when playback fails. No flag, no panel, no asking anyone to be
// holding the phone at the right second: by the time a person can look, the track has resumed and
// the evidence is gone.
//
// sendBeacon because the interesting failures happen as the page is being frozen or unloaded — a
// fetch() at that moment is exactly as likely to be dropped as the audio request that just failed.
const REPORT_URL = "/API/Music/Incident";
const REPORT_MIN_GAP_MS = 60000;   // one report a minute is plenty; a loop must not become a flood
let lastReportAt = 0;

export function reportIncident(kind, { summary = "", trackId = null, force = false } = {}) {
  const now = Date.now();
  if (!force && now - lastReportAt < REPORT_MIN_GAP_MS) return false;
  lastReportAt = now;
  const body = JSON.stringify({
    kind,
    summary: String(summary).slice(0, 400),
    trackId,
    userAgent: (typeof navigator !== "undefined" ? navigator.userAgent : "").slice(0, 400),
    // Only the tail matters: the run-up to the failure, not the whole listening session.
    events: entries.slice(-120),
  });
  try {
    if (typeof navigator !== "undefined" && navigator.sendBeacon) {
      // Type text/plain keeps it a CORS-simple request, so a freezing page never has to wait for
      // a preflight it will not survive.
      return navigator.sendBeacon(REPORT_URL, new Blob([body], { type: "text/plain" }));
    }
    fetch(REPORT_URL, { method: "POST", body, headers: { "Content-Type": "text/plain" }, keepalive: true })
      .catch(() => { /* a failed report must never surface to the listener */ });
    return true;
  } catch {
    return false;
  }
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
