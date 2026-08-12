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
// ── THE SWITCH ──────────────────────────────────────────────────────────────
// `?diag=1` on any page turns EVERYTHING back on and remembers it for that browser: the raw media
// firehose, every routine lifecycle event, and the on-screen panel. `?diag=0` turns it back off.
// That one switch is the whole re-enable story — nothing else needs editing to investigate the next
// one of these. (The panel's "Off" button is the same switch.)
//
// With the switch off, this file is nearly silent BY DESIGN. The sleeping-phone bug it was built for
// is fixed (see music-mse-engine / music-track-boundary-gap-stops-playback), and the run-up it used
// to record — a boundary, three preload steps and four load steps PER TRACK, every wake — was a
// localStorage write and a React notify per event, forever, on every listener's phone, to catch a
// bug that no longer happens. What stays on is a TRIPWIRE, not a journal: the handful of genuinely
// failure-shaped events below, plus the self-reports at the bottom. If the tripwire starts firing
// again, `?diag=1` brings the journal back.

const MAX_ENTRIES = 500;
const KEY = "music.diag";
const RING_KEY = "music.diag.ring";
// The ring has to survive the thing it is recording. When playback fails on a sleeping phone the
// page is reloaded (by the browser, or by the listener) and an in-memory ring dies with it — which
// is why every previous attempt to catch this asked someone to be looking at the exact moment, and
// why that never once worked. Persisted, bounded, and small enough to write on every event.
const PERSIST_ENTRIES = 200;

// Events recorded even with diagnostics OFF: the tripwire. Every one of these means something WENT
// WRONG, so on a healthy session this set fires zero times and the ring costs nothing.
//
// Deliberately NOT here any more (they were, while the sleeping-phone bug was open): `boundary`,
// `wake`, `visibility`, `recover`, and the routine `preload:ready|stream|fetch` /
// `load:minted|download|downloaded` steps. Those fire once or more PER TRACK on a working player —
// they were the run-up that made an incident readable, and they are exactly the excess this trims.
// The cost of trimming them, stated plainly: an incident filed with the switch off now carries the
// FAILURE and any earlier failures, but not the healthy steps that led up to it. That is the right
// trade for a fixed bug — and `?diag=1` buys the run-up back the moment one recurs.
const ALWAYS = new Set([
  "error", "give-up", "park",
  "load:failed", "preload:failed",
  "mse:fallback", "mse:element-error", "mse:dry",
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

/**
 * Turn recording on or off.
 *
 * `persist: false` sets the flag for THIS page life only. That exists for the MSE probe route,
 * which wants full recording while it runs but must not leave every listener's browser logging
 * forever because someone once opened a diagnostics URL on it.
 */
export function setDiagEnabled(on, { persist = true } = {}) {
  enabled = !!on;
  if (persist) {
    try {
      if (enabled) window.localStorage.setItem(KEY, "1");
      else window.localStorage.removeItem(KEY);
    } catch { /* private mode: the flag just won't survive a reload */ }
  }
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

/** Empty the ring — and, with it, the session's report budget: "clear" means start fresh. */
export function clearDiag() {
  entries = [];
  lastReportAt = 0;
  reportsSent = 0;
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
//
// This stays on with the switch off, on purpose: a self-report is the ONLY way these ever get seen,
// and asking Eric to catch one never worked in twenty-odd tries. What changed now that the bug is
// fixed is the volume — see the two limits below and the once-per-session guards at the call sites.
const REPORT_URL = "/API/Music/Incident";
const REPORT_MIN_GAP_MS = 60000;   // one report a minute is plenty; a loop must not become a flood
// ...and a minute apart is still 60 rows an hour. `force: true` was added to let the MSE paths jump
// the gap, which meant a browser stuck in a fallback loop could write faster than the rate limit was
// there to prevent. A whole-session ceiling is the limit that actually holds: force skips the GAP,
// never this. Five reports is far more than enough to characterise one bad session.
const REPORT_MAX_PER_SESSION = 5;
let lastReportAt = 0;
let reportsSent = 0;

export function reportIncident(kind, { summary = "", trackId = null, force = false } = {}) {
  const now = Date.now();
  if (reportsSent >= REPORT_MAX_PER_SESSION) return false;
  if (!force && now - lastReportAt < REPORT_MIN_GAP_MS) return false;
  lastReportAt = now;
  reportsSent += 1;
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
