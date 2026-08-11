import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { MovieAPI } from "../MovieAPI";
import { setDiagEnabled } from "./musicDiag";
import "./MusicMseProbe.css";

// ── Phase 1 of music-mse-plan.md: prove the bytes, ON THE PHONE ──────────────────────────────────
//
// This page is the plan's GATE. Everything past it (the MSE engine, the timeline module, eviction)
// is forbidden until the probes below have run on the phone that actually fails — Android Chrome —
// because every measurement taken so far was desktop Chromium and was API acceptance, not playback.
// If the hidden-fetch probe says a fetch issued while the screen is off never lands, the design is
// falsified and no client engineering rescues it.
//
// ── What the first version got wrong, and why this one is shaped like this ───────────────────────
// It asked a person to pick tracks and then to run five probes in the right order, one tap at a
// time. On the actual phone that read as a page whose buttons did nothing: the run buttons refused
// to do anything until slots were filled, and the diagnostics gate in front of it all had a button
// that could not work (it flipped a module flag that this component never re-read, so the page never
// left the gate screen). The lesson is the same one the incident reporter already paid for: A TEST
// THAT NEEDS A PERSON TO OPERATE IT DOES NOT GET RUN. So:
//
//   • the server picks the tracks (/API/Music/Probe/Candidates) — nobody knows by heart which of
//     their files is the 96 kHz one;
//   • there is ONE button, which runs everything in order and then puts the phone into the
//     screen-off phase by itself;
//   • the answer is a VERDICT panel, not a log to interpret;
//   • and nothing is gated behind a flag that has to be discovered.
//
// ── Two rules inherited from the diag work that came before it (musicDiag.js) ────────────────────
//   • EVIDENCE MUST SURVIVE. The interesting moments happen with the screen off, minutes before
//     anyone can look, and the page may be reloaded or discarded in between. So every observation is
//     written to localStorage AS IT HAPPENS, timestamped, never accumulated in memory and rendered
//     at the end.
//   • NOTHING LOAD-BEARING RUNS ON A TIMER. Intervals stop firing (or arrive minutes late) on a
//     hidden page; media and SourceBuffer events still fire. The pump below is driven from both,
//     with the timer as an accelerator only — and the census exists precisely to measure how sparse
//     the survivors are.

// ── Pure logic (exported and unit-tested) ────────────────────────────────────────────────────────
// The same discipline as MusicPlayerContext's exports: the parts with an arithmetic or classification
// answer are pure functions, so they can be tested without a MediaSource — which the test environment
// does not have, and which no unit test should be pretending to have.

/**
 * The treatment matrix's rows, as MIME strings to probe. Order is the plan's routing order, and the
 * last row is a TRAP being watched rather than a candidate: MP3-in-MP4 (`mp4a.6B`) is measured
 * unsupported in Chrome, which is why the fMP4 lane must never be asked for an mp3. If a browser
 * ever says yes to it, that is a finding, not a green light.
 */
export const PROBE_TYPES = [
  { key: "mpeg", label: "raw MP3", mime: "audio/mpeg", note: "the File lane, bit-perfect, no ffmpeg" },
  { key: "flac", label: "FLAC in fMP4", mime: 'audio/mp4; codecs="flac"', note: "the fMP4 lane, -c:a copy, still lossless" },
  { key: "aac", label: "AAC in fMP4", mime: 'audio/mp4; codecs="mp4a.40.2"', note: "the universal lane — every MSE browser should take this" },
  { key: "mp3mp4", label: "MP3 in fMP4", mime: 'audio/mp4; codecs="mp4a.6B"', note: "THE TRAP — expected NO; a yes here is a finding", trap: true },
];

/** The probes, in the order the gate runs them, with the labels the verdict panel reads out. */
export const PROBE_STEPS = [
  { key: "caps", label: "Capability matrix" },
  { key: "join", label: "MP3 ↔ FLAC join" },
  { key: "joinHires", label: "96 kHz join" },
  { key: "joinMono", label: "Mono join" },
  { key: "quota", label: "SourceBuffer quota" },
  { key: "sleep", label: "Screen-off phase" },
];

/**
 * The capability matrix: what this browser says it will accept, plus whether it has MediaSource at
 * all and whether it has ManagedMediaSource (iPhone's restricted variant, the Phase 2 seam).
 *
 * Takes its probe function as an argument rather than reaching for `window.MediaSource` so the
 * assembly is testable; `isTypeSupported` is allowed to throw, because it does on some engines when
 * handed a codec string they don't parse, and a thrown probe means "no", never a broken page.
 */
export function buildCapabilityMatrix({ isTypeSupported, hasMediaSource, hasManagedMediaSource }) {
  const rows = PROBE_TYPES.map((type) => {
    let supported = false;
    if (hasMediaSource && typeof isTypeSupported === "function") {
      try {
        supported = !!isTypeSupported(type.mime);
      } catch {
        supported = false;
      }
    }
    return { ...type, supported };
  });
  return {
    rows,
    hasMediaSource: !!hasMediaSource,
    hasManagedMediaSource: !!hasManagedMediaSource,
    // A browser that takes neither bit-perfect row nor the universal row has no MSE route at all and
    // keeps today's deck player (ladder rung 7). Surfaced as a verdict so the table answers the
    // question it exists to answer.
    anyTreatment: rows.some((r) => !r.trap && r.supported),
  };
}

/**
 * Which lane a minted Stream/Start payload should be fetched from — the plan's routing table, with
 * rung 1 of the fallback ladder built in: a source format whose bit-perfect treatment this browser
 * refuses (Firefox's MSE has no MP3 decoder) takes the universal treatment instead of falling to the
 * decks. Routing is by capability and payload, never by user-agent.
 *
 * Pure and tested because this is where the mp4a.6B trap would be sprung: an mp3 must never be
 * routed to `fmp4Url`, and the server refuses to mint one for anything but flac precisely so this
 * function cannot get it wrong twice.
 */
export function treatmentFor(payload, isTypeSupported = () => false) {
  if (!payload) return null;
  const universal = payload.universalUrl
    ? { lane: "universal", url: payload.universalUrl, mime: 'audio/mp4; codecs="mp4a.40.2"' }
    : null;

  let candidate = null;
  if (payload.mimeType === "audio/mpeg") {
    candidate = { lane: "file", url: payload.url, mime: "audio/mpeg" };
  } else if (payload.mimeType === "audio/flac" && payload.fmp4Url) {
    candidate = { lane: "fmp4", url: payload.fmp4Url, mime: 'audio/mp4; codecs="flac"' };
  }

  if (candidate && candidate.url) {
    let ok = false;
    try {
      ok = !!isTypeSupported(candidate.mime);
    } catch {
      ok = false;
    }
    if (ok) return candidate;
  }
  return universal;
}

/**
 * Does moving from track A to track B need a `changeType`, and because of what?
 *
 * ⚠ The MIME string is not the only thing that can change, and assuming it was cost this probe a
 * false FAIL: appending a 96 kHz FLAC-fMP4 after a 44.1 kHz one — IDENTICAL MIME, so no changeType
 * was called — made a real Chrome's SourceBuffer raise an error after about 200 KB. With the
 * changeType it plays through. That is the plan's residual risk (the rate switch, not the codec
 * switch) and its stated mitigation, so the rule is: a switch is any change of container/codec,
 * sample rate, or channel count. Pure and tested because Phase 2 routes on it.
 */
export function switchReasonFor(a, b) {
  if (!a || !b) return null;
  if (a.mime !== b.mime) return "codec/container";
  if ((a.sampleRateHz ?? null) !== (b.sampleRateHz ?? null)) return "sample rate";
  if ((a.channels ?? null) !== (b.channels ?? null)) return "channel count";
  return null;
}

/**
 * How a fetch is cut into appends. Chrome's audio SourceBuffer quota is on the order of 12 MB —
 * LESS THAN ONE LARGE FLAC — so "append the track" is never a single operation; the working unit is
 * a chunk. The quota probe measures the real number and this is the arithmetic every append loop
 * uses.
 */
export function chunkRanges(totalBytes, chunkBytes) {
  const total = Math.max(0, Math.floor(totalBytes || 0));
  if (total === 0) return [];
  const size = chunkBytes > 0 ? Math.floor(chunkBytes) : total;
  const out = [];
  for (let start = 0; start < total; start += size) {
    out.push({ start, end: Math.min(start + size, total) });
  }
  return out;
}

/** Buckets for the census, coarse on purpose: the shape of the tail is the finding, not the median. */
const GAP_BUCKETS = [
  { label: "< 1s", max: 1000 },
  { label: "1–5s", max: 5000 },
  { label: "5–15s", max: 15000 },
  { label: "15–60s", max: 60000 },
  { label: "> 60s", max: Infinity },
];

/**
 * The census's headline: how long this page went WITHOUT any event arriving.
 *
 * The plan sizes every lookahead from this number ("the worst gap is the lookahead floor for every
 * route"), so it is computed rather than eyeballed, and `maxGapAfter` names the last event before
 * the silence — which is the difference between "the renderer froze" and "nothing was scheduled".
 * Entries are `{ at, event }` in arrival order.
 */
export function gapDistribution(entries) {
  const list = (entries || []).filter((e) => e && typeof e.at === "number");
  const empty = {
    count: list.length,
    spanMs: 0,
    maxGapMs: 0,
    maxGapAfter: null,
    medianGapMs: 0,
    p95GapMs: 0,
    buckets: GAP_BUCKETS.map((b) => ({ label: b.label, count: 0 })),
  };
  if (list.length < 2) return empty;

  const gaps = [];
  let maxGapMs = 0;
  let maxGapAfter = null;
  for (let i = 1; i < list.length; i++) {
    const gap = list[i].at - list[i - 1].at;
    gaps.push(gap);
    if (gap > maxGapMs) {
      maxGapMs = gap;
      maxGapAfter = list[i - 1].event;
    }
  }
  const sorted = gaps.slice().sort((a, b) => a - b);
  const at = (p) => sorted[Math.min(sorted.length - 1, Math.floor(p * sorted.length))];
  const buckets = GAP_BUCKETS.map((b) => ({ label: b.label, count: 0 }));
  gaps.forEach((gap) => {
    const idx = GAP_BUCKETS.findIndex((b) => gap < b.max);
    buckets[idx < 0 ? buckets.length - 1 : idx].count += 1;
  });

  return {
    count: list.length,
    spanMs: list[list.length - 1].at - list[0].at,
    maxGapMs,
    maxGapAfter,
    medianGapMs: at(0.5),
    p95GapMs: at(0.95),
    buckets,
  };
}

/**
 * THE GATE, as two numbers: of the fetches issued while the page was hidden, how many came back.
 *
 * This is the whole design's central bet — MSE moves fetching back into script, and the question is
 * whether a backgrounded renderer is still allowed to complete one. A returning listener must be
 * able to read the answer without interpreting rows, so the counting lives here and is tested.
 */
export function hiddenFetchSummary(fetchLog) {
  const rows = fetchLog || [];
  const issued = new Set(rows.filter((r) => r && r.hiddenAtIssue).map((r) => r.seq));
  const completed = new Set(
    rows.filter((r) => r && r.hiddenAtIssue && r.state === "completed").map((r) => r.seq),
  );
  const failed = new Set(
    rows.filter((r) => r && r.hiddenAtIssue && r.state === "failed").map((r) => r.seq),
  );
  let status = "skip";
  if (issued.size === 0) status = "skip";
  else if (completed.size === issued.size) status = "pass";
  else if (completed.size === 0) status = "fail";
  else status = "partial";
  return {
    issued: issued.size,
    completed: completed.size,
    failed: failed.size,
    // Issued, never answered, and not recorded as failed either: the exact shape of "the phone went
    // to sleep holding the request", which is the failure the plan says would falsify the design.
    unanswered: issued.size - completed.size - failed.size,
    status,
  };
}

/**
 * Everything a returning user should be able to read in five seconds: one row per probe with
 * PASS / FAIL / SKIPPED and why, plus the two headline numbers. Pure, so the panel that decides
 * whether the gate passed can be tested without a browser that can play anything.
 */
export function summarizeRun({ results, fetchLog, census }) {
  const map = results || {};
  const probes = PROBE_STEPS.map((step) => {
    const result = map[step.key];
    return {
      key: step.key,
      label: step.label,
      status: result ? result.status || "run" : "none",
      detail: result ? result.verdict || result.detail || "" : "not run yet",
      at: result ? result.at : null,
    };
  });
  const hidden = hiddenFetchSummary(fetchLog);
  const gap = gapDistribution(census);
  const graded = probes.filter((p) => p.status === "pass" || p.status === "fail");
  const overall = probes.some((p) => p.status === "fail") ? "FAIL"
    : graded.length === 0 ? "NO RESULT"
      : probes.some((p) => p.status === "none" || p.status === "run") ? "INCOMPLETE"
        : "PASS";
  return { probes, hidden, gap, overall };
}

/** Bounded append. Every ring on this page is persisted on every push, so it must not grow. */
export function pushRing(list, entry, max) {
  const next = (list || []).concat([entry]);
  return next.length > max ? next.slice(next.length - max) : next;
}

export function formatBytes(n) {
  if (!(n > 0)) return "0 B";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(2)} MB`;
}

export function formatMs(ms) {
  if (!(ms > 0)) return "0 ms";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
}

/** Wall-clock, seconds included: a ten-minute walk-away has to be auditable after the fact. */
export function stamp(ms) {
  if (!ms) return "";
  const d = new Date(ms);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

/** Describes a server-picked candidate the way probe 2 needs it labelled — the rate and channel
 *  count ARE the measurement's label, so a slot filled by the wrong kind of file is visible. */
export function describeCandidate(candidate) {
  if (!candidate) return "none in this library";
  const bits = [candidate.extension];
  if (candidate.sampleRateHz) bits.push(`${Math.round(candidate.sampleRateHz / 100) / 10} kHz`);
  if (candidate.channels) bits.push(candidate.channels === 1 ? "mono" : `${candidate.channels} ch`);
  bits.push(formatBytes(candidate.sizeBytes));
  return `${candidate.title} — ${bits.join(" · ")}`;
}

// ── Persistence ──────────────────────────────────────────────────────────────────────────────────
// Namespaced like every other music key. Written on every observation, not at the end of a run: a
// screen-off session that ends in a reload must still leave its evidence behind.

const K_CENSUS = "music.mse.census";
const K_FETCH = "music.mse.fetchlog";
const K_RESULTS = "music.mse.results";
const CENSUS_MAX = 600;
const FETCH_MAX = 300;

function loadJson(key, fallback) {
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? JSON.parse(raw) : fallback;
  } catch {
    return fallback;
  }
}

function saveJson(key, value) {
  try {
    window.localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* quota or private mode: the run still works, it just won't survive a reload */
  }
}

// ── Byte plumbing ────────────────────────────────────────────────────────────────────────────────
// credentials: "omit" is the established pattern (fetchToDeck) — the gateway's ACAO admits the site
// origin and the auth is in the URL, so sending credentials would only turn a simple request into a
// preflighted one.

/**
 * Streams a lane URL, handing each chunk to `onChunk` as it lands, and stops after `maxBytes`.
 * Reading it in chunks rather than as one arrayBuffer is not incidental: it is what Phase 2's append
 * loop does, and stopping early is how a join test stays inside the SourceBuffer quota.
 */
async function fetchChunks(url, { maxBytes = Infinity, onChunk, signal } = {}) {
  const response = await fetch(url, { credentials: "omit", signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  if (!response.body) {
    // No streaming reader (very old engines): fall back to the whole body.
    const buf = new Uint8Array(await response.arrayBuffer());
    if (onChunk) await onChunk(buf);
    return buf.byteLength;
  }
  const reader = response.body.getReader();
  let total = 0;
  for (;;) {
    // eslint-disable-next-line no-await-in-loop
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    // eslint-disable-next-line no-await-in-loop
    if (onChunk) await onChunk(value);
    if (total >= maxBytes) {
      try { await reader.cancel(); } catch { /* already closed */ }
      break;
    }
  }
  return total;
}

/** appendBuffer as a promise. QuotaExceededError arrives synchronously from appendBuffer itself. */
function appendChunk(sb, bytes) {
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      sb.removeEventListener("updateend", onDone);
      sb.removeEventListener("error", onErr);
    };
    const onDone = () => { cleanup(); resolve(); };
    const onErr = () => { cleanup(); reject(new Error("SourceBuffer raised error")); };
    sb.addEventListener("updateend", onDone);
    sb.addEventListener("error", onErr);
    try {
      sb.appendBuffer(bytes);
    } catch (e) {
      cleanup();
      reject(e);
    }
  });
}

/** remove() as a promise. Eviction is what makes an hours-long queue cost the same as a short one —
 *  and what lets the screen-off phase play for as long as it is left alone. */
function removeRange(sb, start, end) {
  return new Promise((resolve) => {
    if (!(end > start)) { resolve(); return; }
    const done = () => { sb.removeEventListener("updateend", done); resolve(); };
    sb.addEventListener("updateend", done);
    try {
      sb.remove(start, end);
    } catch {
      sb.removeEventListener("updateend", done);
      resolve();
    }
  });
}

/** Attaches a fresh MediaSource to the element and resolves once it is open. */
function openMediaSource(audio) {
  const ms = new window.MediaSource();
  const url = URL.createObjectURL(ms);
  audio.src = url;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("MediaSource never opened")), 10000);
    ms.addEventListener("sourceopen", () => {
      clearTimeout(timer);
      URL.revokeObjectURL(url);
      resolve(ms);
    }, { once: true });
  });
}

/**
 * play() that ALWAYS settles, and says which way.
 *
 * Measured in a real browser: `await audio.play()` on an element whose buffer is empty (because the
 * fetch that was meant to fill it failed) stays pending indefinitely — it is not rejected, it simply
 * never resolves. That hung the screen-off phase with no verdict recorded at all, which is the one
 * outcome a walk-away test may never have: a person comes back after ten minutes to a page that says
 * nothing. An awaited promise with no timeout is a silent failure mode in any probe.
 */
async function playOrReport(audio, timeoutMs = 8000) {
  return Promise.race([
    audio.play().then(() => "playing", (e) => `play() refused: ${e && e.message}`),
    new Promise((resolve) => setTimeout(() => resolve("play() never settled"), timeoutMs)),
  ]);
}

function bufferedEnd(sb) {
  try {
    return sb.buffered.length ? sb.buffered.end(sb.buffered.length - 1) : 0;
  } catch {
    return 0;
  }
}

const JOIN_PREFIX_BYTES = 3 * 1024 * 1024; // enough audio either side of the join, small enough to fit the quota
const QUOTA_CHUNK_BYTES = 512 * 1024;
const JOIN_LEAD_SEC = 3;                    // start this far before the boundary — the test is the JOIN, not the track
const JOIN_WATCH_MS = 25000;
const SLEEP_FILL_BYTES = 4 * 1024 * 1024;   // per append cycle in the screen-off phase
const SLEEP_TARGET_AHEAD_SEC = 60;          // top up whenever the buffer holds less than this ahead
const SLEEP_KEEP_BEHIND_SEC = 20;           // …and drop everything older, so 10 minutes costs what 1 does
const SLEEP_PUMP_MS = 5000;

// Media events worth a census entry. `timeupdate` IS included here (unlike musicDiag's list, which
// deliberately excludes it to keep its ring readable) because on this page its ABSENCE is the
// measurement: the plan's clock rule says timeupdate stops being delivered with the screen off, and
// this page is where that gets a number.
const CENSUS_EVENTS = [
  "timeupdate", "progress", "waiting", "stalled", "playing", "pause", "ended", "error", "suspend",
];
const CENSUS_TICK_MS = 5000;

export default function MusicMseProbe() {
  const audioRef = useRef(null);
  const sessionRef = useRef(null);
  const censusRef = useRef([]);
  const fetchLogRef = useRef([]);
  const pumpRef = useRef(null);
  const lastPersistRef = useRef(0);  // see logCensus: the ring must not cost more than it measures
  const seqRef = useRef(0);

  const [results, setResults] = useState(() => loadJson(K_RESULTS, {}));
  const [census, setCensus] = useState(() => loadJson(K_CENSUS, []));
  const [fetchLog, setFetchLog] = useState(() => loadJson(K_FETCH, []));
  const [candidates, setCandidates] = useState(null);
  const [candidateError, setCandidateError] = useState(null);
  const [caps, setCaps] = useState(null);
  const [phase, setPhase] = useState("idle");   // idle → running → sleeping (→ stopped)
  const [step, setStep] = useState(null);
  const [advanced, setAdvanced] = useState(false);

  // Turn the diag ring on for the duration: this page is diagnostics, and a run that produces no
  // musicDiag entries alongside its own is half a record. It is NOT a gate — the first version put a
  // "turn diagnostics on" screen in front of the page, whose button flipped a module-level flag that
  // this component never re-read, so the page never left that screen and every tap did nothing. A
  // diagnostics ROUTE is its own gate; nobody arrives at this URL by accident.
  useEffect(() => { setDiagEnabled(true); }, []);

  const isTypeSupported = useCallback((mime) => {
    const MS = window.MediaSource;
    try {
      return !!(MS && MS.isTypeSupported && MS.isTypeSupported(mime));
    } catch {
      return false;
    }
  }, []);

  useEffect(() => {
    setCaps(buildCapabilityMatrix({
      isTypeSupported,
      hasMediaSource: typeof window.MediaSource !== "undefined",
      hasManagedMediaSource: typeof window.ManagedMediaSource !== "undefined",
    }));
  }, [isTypeSupported]);

  // Candidates are fetched on mount, not on demand: by the time a thumb is on the button the page
  // must already know what it is going to play.
  //
  // ⚠ And the gate AWAITS them (see ensureCandidates) rather than reading whatever state happened to
  // have arrived. Measured in a real browser: a tap that lands before this fetch returns — which on
  // a phone is every tap, because the page is opened and pressed immediately — ran the whole gate
  // against an empty candidate set and reported "SKIPPED — the library has no track for this join"
  // for all five probes, in the same second, over a library that had every one of them. A race that
  // reports a confident wrong answer is worse than one that hangs.
  const candidatesRef = useRef(null);
  const ensureCandidates = useCallback(async () => {
    if (candidatesRef.current) return candidatesRef.current;
    const r = await MovieAPI.getMusicProbeCandidates();
    if (!r.ok) throw new Error(`candidates answered ${r.status}`);
    const body = await r.json();
    candidatesRef.current = body;
    setCandidates(body);
    return body;
  }, []);

  useEffect(() => {
    let live = true;
    ensureCandidates().catch((e) => {
      if (live) setCandidateError(String(e && e.message ? e.message : e));
    });
    return () => { live = false; };
  }, [ensureCandidates]);

  const record = useCallback((key, value) => {
    setResults((prev) => {
      const next = { ...prev, [key]: { ...value, at: Date.now() } };
      saveJson(K_RESULTS, next);
      return next;
    });
  }, []);

  const logCensus = useCallback((event, data) => {
    const now = Date.now();
    const hidden = document.visibilityState === "hidden";
    censusRef.current = pushRing(censusRef.current, { at: now, event, hidden, data: data || null }, CENSUS_MAX);

    // The ring is written to localStorage rather than only held in memory, because a census that
    // survives only to the end of a run is a census of the runs that ended well — which are not the
    // ones being investigated.
    //
    // But it is written at most once a second while the page is AWAKE. `timeupdate` arrives ~4×/s,
    // and stringifying a 600-entry ring that often (plus the re-render it would cause) is enough
    // main-thread work to perturb the very timing this page exists to measure. While HIDDEN every
    // entry is written immediately: events are rare there, each one is the evidence, and the page may
    // not get another opportunity.
    if (hidden || now - lastPersistRef.current >= 1000) {
      lastPersistRef.current = now;
      saveJson(K_CENSUS, censusRef.current);
      setCensus(censusRef.current);
    }
  }, []);

  const logFetch = useCallback((entry) => {
    fetchLogRef.current = pushRing(fetchLogRef.current, entry, FETCH_MAX);
    saveJson(K_FETCH, fetchLogRef.current);
    setFetchLog(fetchLogRef.current);
  }, []);

  /** Mints through the BATCH endpoint — which also gives Phase 0's new route a real exerciser on the
   *  gate run, on the phone, rather than only ever being called by code that doesn't exist yet. */
  const mint = useCallback(async (ids) => {
    const r = await MovieAPI.startMusicTracks(ids);
    if (!r.ok) throw new Error(`Stream/StartBatch answered ${r.status}`);
    const body = await r.json();
    const byId = new Map((body.tracks || []).map((t) => [t.trackId, t]));
    return ids.map((id) => {
      const payload = byId.get(id);
      if (!payload) throw new Error(`the server minted no URL for track ${id}`);
      return payload;
    });
  }, []);

  const teardown = useCallback(() => {
    const session = sessionRef.current;
    sessionRef.current = null;
    if (pumpRef.current) pumpRef.current.stopped = true;
    pumpRef.current = null;
    const audio = audioRef.current;
    if (audio) {
      try { audio.pause(); } catch { /* already stopped */ }
    }
    if (session) {
      try { if (session.ms.readyState === "open") session.ms.endOfStream(); } catch { /* fine */ }
      if (session.detach) session.detach();
    }
  }, []);

  useEffect(() => teardown, [teardown]);

  /** Opens a MediaSource on the page's one element and wires the census to it. */
  const startSession = useCallback(async (mime) => {
    teardown();
    const audio = audioRef.current;
    const ms = await openMediaSource(audio);
    const sb = ms.addSourceBuffer(mime);
    // "sequence" so appended tracks land back-to-back without computing timestamps — the mechanism
    // the mixed-queue requirement rests on.
    sb.mode = "sequence";

    const onMedia = (e) => logCensus(`media:${e.type}`, { t: Math.round(audio.currentTime * 10) / 10 });
    const onUpdateEnd = () => logCensus("sb:updateend", { end: Math.round(bufferedEnd(sb) * 10) / 10 });
    const onVisibility = () => logCensus("visibilitychange", { state: document.visibilityState });
    CENSUS_EVENTS.forEach((name) => audio.addEventListener(name, onMedia));
    sb.addEventListener("updateend", onUpdateEnd);
    document.addEventListener("visibilitychange", onVisibility);

    // The interval is in the census as a SUBJECT, not as a recorder: the project has already measured
    // that intervals stop firing (or arrive minutes late) on a hidden page, and `lateMs` is that
    // measurement taken again on this device. A tick that arrives 90 s after the last one has told
    // us more than the tick itself ever could.
    let lastTick = Date.now();
    const ticker = window.setInterval(() => {
      const now = Date.now();
      logCensus("tick", { lateMs: Math.max(0, now - lastTick - CENSUS_TICK_MS) });
      lastTick = now;
    }, CENSUS_TICK_MS);

    sessionRef.current = {
      ms,
      sb,
      mime,
      detach: () => {
        window.clearInterval(ticker);
        CENSUS_EVENTS.forEach((name) => audio.removeEventListener(name, onMedia));
        try { sb.removeEventListener("updateend", onUpdateEnd); } catch { /* gone with the source */ }
        document.removeEventListener("visibilitychange", onVisibility);
      },
    };
    return sessionRef.current;
  }, [logCensus, teardown]);

  // ── The join probe (used for MP3↔FLAC, 96 kHz and mono) ────────────────────────────────────────
  // Appends a PREFIX of each track rather than the whole thing: a whole FLAC-fMP4 exceeds the ~12 MB
  // quota, which would fail the probe for a reason that isn't the join. It is also exactly the shape
  // Phase 2's append loop has.
  const runJoin = useCallback(async (key, from, to) => {
    if (!from || !to) {
      record(key, {
        status: "skip",
        verdict: `SKIPPED — the library has no ${!from ? "first" : "second"} track for this join`,
      });
      return;
    }
    if (from.id === to.id) {
      // The same file on both sides is not a join. Saying so is a result; running it and reporting
      // PASS would be a measurement of nothing, dressed as the one that matters.
      record(key, { status: "skip", verdict: "SKIPPED — only one candidate, so there is no switch to test" });
      return;
    }
    setStep(`${PROBE_STEPS.find((s) => s.key === key).label}…`);
    try {
      const ids = from.id === to.id ? [from.id] : [from.id, to.id];
      const minted = await mint(ids);
      const pa = minted[0];
      const pb = minted.length === 1 ? minted[0] : minted[1];
      const ta = treatmentFor(pa, isTypeSupported);
      const tb = treatmentFor(pb, isTypeSupported);
      if (!ta || !tb) throw new Error("no supported treatment for one of these tracks");

      const session = await startSession(ta.mime);
      const { sb } = session;
      const audio = audioRef.current;

      let appendedA = 0;
      await fetchChunks(ta.url, {
        maxBytes: JOIN_PREFIX_BYTES,
        onChunk: async (chunk) => { appendedA += chunk.byteLength; await appendChunk(sb, chunk); },
      });
      const boundary = bufferedEnd(sb);

      // The switch itself.
      //
      // ⚠ The MIME string is NOT the only thing that can change. Measured here, on a real Chrome:
      // appending a 96 kHz FLAC-fMP4 after a 44.1 kHz one — identical MIME, so the first version of
      // this probe called no changeType at all — makes the SourceBuffer raise an error after ~200 KB.
      // That is precisely the plan's residual risk (the rate switch, not the codec switch), and
      // `changeType` is its stated mitigation, so the probe has to actually perform it: a switch is
      // any change of MIME, sample rate or channel count.
      const switchReason = switchReasonFor(
        { mime: ta.mime, sampleRateHz: pa.sampleRateHz, channels: pa.channels },
        { mime: tb.mime, sampleRateHz: pb.sampleRateHz, channels: pb.channels },
      );
      let changeTypeUsed = false;
      let changeTypeError = null;
      if (switchReason) {
        try {
          sb.changeType(tb.mime);
          changeTypeUsed = true;
        } catch (e) {
          changeTypeError = String(e && e.message ? e.message : e);
        }
      }

      let appendedB = 0;
      let appendError = null;
      if (!changeTypeError) {
        try {
          await fetchChunks(tb.url, {
            maxBytes: JOIN_PREFIX_BYTES,
            onChunk: async (chunk) => { appendedB += chunk.byteLength; await appendChunk(sb, chunk); },
          });
        } catch (e) {
          appendError = String(e && e.message ? e.message : e);
        }
      }
      const total = bufferedEnd(sb);

      // Now the part the API probes could not answer: does it PLAY across the join. Start a few
      // seconds before the boundary and watch the playhead cross it. A `waiting` in that window is
      // the buffer having gone dry at the join, which is the failure this whole design exists to
      // avoid — so it is counted, not just tolerated.
      let waitingCount = 0;
      const onWaiting = () => { waitingCount += 1; };
      audio.muted = false;
      audio.currentTime = Math.max(0, boundary - JOIN_LEAD_SEC);
      const playOutcome = await playOrReport(audio);
      const played = playOutcome === "playing";
      if (!played) appendError = appendError || playOutcome;
      // Counting starts only once playback is under way. A SEEK fires `waiting` on its own — Chrome
      // does it every time — and counting that reported "crossed, but the buffer went dry" on joins
      // that were in fact continuous. A stall detector that fires on its own setup is worse than no
      // stall detector: it condemns the healthy case.
      audio.addEventListener("waiting", onWaiting);
      audio.addEventListener("stalled", onWaiting);

      const target = Math.min(boundary + 1.5, total);
      const deadline = Date.now() + JOIN_WATCH_MS;
      let crossed = false;
      while (played && Date.now() < deadline) {
        // eslint-disable-next-line no-await-in-loop
        await new Promise((r) => setTimeout(r, 250));
        if (audio.currentTime >= target) { crossed = true; break; }
      }
      const reached = audio.currentTime;
      audio.removeEventListener("waiting", onWaiting);
      audio.removeEventListener("stalled", onWaiting);
      audio.pause();

      const passed = !changeTypeError && !appendError && crossed && waitingCount === 0;
      record(key, {
        status: passed ? "pass" : "fail",
        verdict: changeTypeError ? "FAIL — changeType refused"
          : appendError ? `FAIL — ${appendError}`
            : crossed && waitingCount === 0 ? "PASS — continuous across the join"
              : crossed ? "FAIL — crossed, but the buffer went dry"
                : "FAIL — did not cross the join",
        from: `${from.title} — ${pa.mimeType} ${pa.sampleRateHz || "?"} Hz / ${pa.channels || "?"} ch → ${ta.lane}`,
        to: `${to.title} — ${pb.mimeType} ${pb.sampleRateHz || "?"} Hz / ${pb.channels || "?"} ch → ${tb.lane}`,
        appended: `${formatBytes(appendedA)} + ${formatBytes(appendedB)}`,
        boundarySec: Math.round(boundary * 100) / 100,
        bufferedSec: Math.round(total * 100) / 100,
        reachedSec: Math.round(reached * 100) / 100,
        switchReason: switchReason || "none — nothing differed",
        changeTypeUsed,
        waitingCount,
      });
    } catch (e) {
      record(key, { status: "fail", verdict: `FAIL — ${String(e && e.message ? e.message : e)}` });
    } finally {
      teardown();
    }
  }, [mint, isTypeSupported, startSession, record, teardown]);

  // ── The quota probe ────────────────────────────────────────────────────────────────────────────
  // Chrome's default is understood to be ~12 MB — less than one large FLAC — and every append-window
  // number in the plan is sized from the real value. Appends run with the element PAUSED AT ZERO on
  // purpose: Chrome evicts what is behind the playhead, so a playing element would quietly make room
  // and the measurement would come back as "no limit found".
  const runQuota = useCallback(async (pick) => {
    if (!pick) {
      record("quota", { status: "skip", verdict: "SKIPPED — no candidate track" });
      return;
    }
    setStep("SourceBuffer quota…");
    try {
      const [payload] = await mint([pick.id]);
      const treatment = treatmentFor(payload, isTypeSupported);
      if (!treatment) throw new Error("no supported treatment for this track");
      const session = await startSession(treatment.mime);
      const { sb } = session;

      const chunks = [];
      await fetchChunks(treatment.url, {
        maxBytes: 24 * 1024 * 1024,
        onChunk: async (chunk) => { chunks.push(chunk); },
      });
      if (chunks.length === 0) throw new Error("the lane returned no bytes");

      let appended = 0;
      let quotaAt = null;
      let passes = 0;
      // Re-append the same bytes until the buffer refuses them. Looping the source is legitimate: an
      // fMP4 init segment may be re-appended, and in sequence mode each pass lands after the last —
      // it is the same shape as a queue, which is what we are sizing for.
      outer: while (passes < 40) {
        passes += 1;
        for (const chunk of chunks) {
          try {
            // eslint-disable-next-line no-await-in-loop
            await appendChunk(sb, chunk);
            appended += chunk.byteLength;
          } catch (e) {
            const name = e && e.name;
            quotaAt = name === "QuotaExceededError" ? "QuotaExceededError" : String(e && e.message ? e.message : e);
            break outer;
          }
        }
      }

      record("quota", {
        // A quota that is never reached is not a failure — it is a browser that took 24 MB × 40
        // without complaint, which is worth knowing and is not a reason to stop the gate.
        status: "pass",
        verdict: quotaAt === "QuotaExceededError"
          ? `PASS — quota is ${formatBytes(appended)}`
          : quotaAt ? `PASS (with a caveat) — append failed at ${formatBytes(appended)}: ${quotaAt}`
            : `PASS — no limit hit after ${formatBytes(appended)}`,
        track: `${pick.title} → ${treatment.lane}`,
        appendedBytes: appended,
        bufferedSec: Math.round(bufferedEnd(sb) * 10) / 10,
        chunkCount: chunkRanges(appended, QUOTA_CHUNK_BYTES).length,
      });
    } catch (e) {
      record("quota", { status: "fail", verdict: `FAIL — ${String(e && e.message ? e.message : e)}` });
    } finally {
      teardown();
    }
  }, [mint, isTypeSupported, startSession, record, teardown]);

  // ── The screen-off phase (the gate itself) ─────────────────────────────────────────────────────
  // Audible MSE playback that keeps itself alive by fetching, cycling the candidates forever. Every
  // top-up is a real "next track" fetch, logged at issue AND at completion with the page's visibility
  // at both moments — so the question the plan calls the design's central bet is answered by the
  // mechanism that has to survive rather than by a synthetic request alongside it.
  //
  // ⚠ AUDIBLE, NOT MUTED. A hidden page's licence to keep running is that it is playing audio the
  // user can hear; Chrome's background exemption keys on audibility, so a muted element would make
  // the whole measurement meaningless — it would be measuring a page that had already lost the
  // licence for a different reason.
  const startSleepPhase = useCallback(async (list) => {
    const playable = (list || []).filter(Boolean);
    if (playable.length === 0) {
      record("sleep", { status: "skip", verdict: "SKIPPED — no candidate tracks to play" });
      return;
    }
    setStep("screen-off phase");
    try {
      const minted = await mint(playable.map((t) => t.id));
      const lanes = minted
        .map((payload) => ({ payload, treatment: treatmentFor(payload, isTypeSupported) }))
        .filter((l) => l.treatment);
      if (lanes.length === 0) throw new Error("no supported treatment for any candidate");

      const session = await startSession(lanes[0].treatment.mime);
      const { sb } = session;
      const audio = audioRef.current;
      audio.muted = false;
      audio.volume = 1;

      const state = { stopped: false, busy: false, i: 0, cycles: 0 };
      pumpRef.current = state;

      // One cycle: evict what has been played, then fetch and append the next candidate. Both halves
      // matter — without eviction the quota ends the session in minutes, and the phase has to last
      // longer than a person's walk away.
      const cycle = async () => {
        if (state.stopped || state.busy || !sessionRef.current) return;
        const ahead = bufferedEnd(sb) - (audio.currentTime || 0);
        if (ahead > SLEEP_TARGET_AHEAD_SEC) return;
        state.busy = true;
        try {
          const keepFrom = (audio.currentTime || 0) - SLEEP_KEEP_BEHIND_SEC;
          if (keepFrom > 0) await removeRange(sb, 0, keepFrom);

          const lane = lanes[state.i % lanes.length];
          state.i += 1;
          if (state.i % lanes.length === 0) state.cycles += 1;

          if (lane.treatment.mime !== sessionRef.current.mime) {
            try {
              sb.changeType(lane.treatment.mime);
              sessionRef.current.mime = lane.treatment.mime;
            } catch (e) {
              logCensus("changeType:refused", { error: String(e && e.message ? e.message : e) });
            }
          }

          const seq = (seqRef.current += 1);
          const issuedAt = Date.now();
          const hiddenAtIssue = document.visibilityState === "hidden";
          // Written BEFORE the fetch resolves. A fetch that never completes is THE finding, and it
          // leaves no trace at all unless its issue was recorded on its own.
          logFetch({ seq, trackId: lane.payload.trackId, issuedAt, hiddenAtIssue, state: "issued" });

          let bytes = 0;
          let error = null;
          try {
            state.lastBytes = 0;
            bytes = await fetchChunks(lane.treatment.url, {
              maxBytes: SLEEP_FILL_BYTES,
              onChunk: async (chunk) => {
                try {
                  await appendChunk(sb, chunk);
                } catch (e) {
                  // Quota mid-cycle: drop more of what is behind and carry on. Stopping here would
                  // end the audio, which ends the measurement.
                  logCensus("append:refused", { name: e && e.name });
                  const back = (audio.currentTime || 0) - 5;
                  if (back > 0) await removeRange(sb, 0, back);
                }
              },
            });
          } catch (e) {
            error = String(e && e.message ? e.message : e);
          }
          state.lastError = error;
          state.lastBytes = bytes;
          const completedAt = Date.now();
          logFetch({
            seq,
            trackId: lane.payload.trackId,
            issuedAt,
            completedAt,
            elapsedMs: completedAt - issuedAt,
            hiddenAtIssue,
            hiddenAtCompletion: document.visibilityState === "hidden",
            bytes,
            error,
            state: error ? "failed" : "completed",
          });
        } finally {
          state.busy = false;
        }
      };

      // Prime the buffer before playing: the phase must start with audio, not with a wait. If the
      // priming fetch brought back nothing there is no point starting a ten-minute walk-away — say
      // why now, while someone is still holding the phone.
      await cycle();
      if (bufferedEnd(sb) <= 0) throw new Error(state.lastError || "no audio could be fetched to play");
      const playOutcome = await playOrReport(audio);
      logCensus("probe:playing", { bufferedSec: Math.round(bufferedEnd(sb)), playOutcome });
      if (playOutcome !== "playing") throw new Error(playOutcome);

      // Three triggers, on purpose. The interval is the accelerator that only works awake; the media
      // and SourceBuffer events are the ones the clock rule says survive a hidden page.
      const onTrigger = () => { cycle(); };
      const timer = window.setInterval(onTrigger, SLEEP_PUMP_MS);
      audio.addEventListener("timeupdate", onTrigger);
      audio.addEventListener("waiting", onTrigger);
      audio.addEventListener("progress", onTrigger);
      sb.addEventListener("updateend", onTrigger);
      document.addEventListener("visibilitychange", onTrigger);
      const prevDetach = session.detach;
      session.detach = () => {
        window.clearInterval(timer);
        audio.removeEventListener("timeupdate", onTrigger);
        audio.removeEventListener("waiting", onTrigger);
        audio.removeEventListener("progress", onTrigger);
        try { sb.removeEventListener("updateend", onTrigger); } catch { /* gone with the source */ }
        document.removeEventListener("visibilitychange", onTrigger);
        prevDetach();
      };

      setPhase("sleeping");
      record("sleep", {
        status: "run",
        verdict: "RUNNING — turn the screen off and come back in 10 minutes",
        playing: lanes.map((l) => l.treatment.lane).join(" → "),
        startedAt: Date.now(),
      });
    } catch (e) {
      record("sleep", { status: "fail", verdict: `FAIL — ${String(e && e.message ? e.message : e)}` });
      teardown();
      setPhase("idle");
    }
  }, [mint, isTypeSupported, startSession, record, logCensus, logFetch, teardown]);

  // ── The one button ─────────────────────────────────────────────────────────────────────────────
  const runGate = useCallback(async () => {
    setPhase("running");
    // A run starts from empty. Two runs' evidence in one ring is worse than none: the gap
    // distribution would span the time the phone spent between them, which is not a measurement of
    // anything.
    censusRef.current = [];
    fetchLogRef.current = [];
    seqRef.current = 0;
    saveJson(K_CENSUS, []);
    saveJson(K_FETCH, []);
    setCensus([]);
    setFetchLog([]);
    const fresh = { startedAt: Date.now() };
    saveJson(K_RESULTS, fresh);
    setResults(fresh);

    const matrix = buildCapabilityMatrix({
      isTypeSupported,
      hasMediaSource: typeof window.MediaSource !== "undefined",
      hasManagedMediaSource: typeof window.ManagedMediaSource !== "undefined",
    });
    setCaps(matrix);
    record("caps", {
      status: matrix.anyTreatment ? "pass" : "fail",
      verdict: matrix.anyTreatment
        ? `PASS — ${matrix.rows.filter((r) => !r.trap && r.supported).map((r) => r.label).join(", ")}`
        : "FAIL — this browser has no usable MSE treatment; it keeps the deck player (rung 7)",
      trapAccepted: matrix.rows.some((r) => r.trap && r.supported),
      managedMediaSource: matrix.hasManagedMediaSource,
      userAgent: navigator.userAgent,
    });

    if (!matrix.anyTreatment) {
      ["join", "joinHires", "joinMono", "quota", "sleep"].forEach((key) => record(key, {
        status: "skip",
        verdict: "SKIPPED — nothing to append into on this browser",
      }));
      setStep(null);
      setPhase("idle");
      return;
    }

    // Awaited, never read from state: see ensureCandidates for the race this closes.
    let picks = {};
    try {
      picks = await ensureCandidates();
    } catch (e) {
      const why = `SKIPPED — the server would not name any tracks (${String(e && e.message ? e.message : e)})`;
      ["join", "joinHires", "joinMono", "quota", "sleep"].forEach((key) => record(key, { status: "skip", verdict: why }));
      setStep(null);
      setPhase("idle");
      return;
    }
    const base = picks.mp3 || picks.flac;
    await runJoin("join", picks.mp3, picks.flac);
    await runJoin("joinHires", picks.flac || base, picks.hires);
    await runJoin("joinMono", base, picks.mono);
    await runQuota(picks.flac || base);
    await startSleepPhase([picks.mp3, picks.flac, picks.hires, picks.mono]);
    setStep(null);
  }, [ensureCandidates, isTypeSupported, record, runJoin, runQuota, startSleepPhase]);

  const stop = useCallback(() => {
    teardown();
    setPhase("idle");
    setStep(null);
    record("sleep", { status: "run", verdict: "stopped by hand" });
  }, [teardown, record]);

  const summary = useMemo(
    () => summarizeRun({ results, fetchLog, census }),
    [results, fetchLog, census],
  );

  // The button waits for the tracks as well as the capability probe. The gate awaits them anyway,
  // but a button that can be pressed before the page knows what it will play is a button that
  // reports an answer about a library it had not yet looked at.
  const waitingForCandidates = !candidates && !candidateError;
  const ready = phase === "idle" && !!caps && !waitingForCandidates;
  const slots = [
    ["mp3", "MP3"],
    ["flac", "FLAC 44.1 kHz"],
    ["hires", "FLAC > 48 kHz"],
    ["mono", "mono"],
  ];

  return (
    <div className="mse-probe">
      <h1>MSE gate — music-mse-plan.md Phase 1</h1>

      {/* ── The verdicts, first, always. A returning listener has to understand the outcome without
          reading a single row. ── */}
      <section className={`mse-probe-verdicts mse-probe-verdicts--${summary.overall.replace(/\s/g, "")}`}>
        <div className="mse-probe-overall">{summary.overall}</div>
        <div className="mse-probe-headline">
          Hidden fetches: <strong>{summary.hidden.completed} of {summary.hidden.issued}</strong> completed
          {summary.hidden.unanswered > 0 ? ` · ${summary.hidden.unanswered} never answered` : ""}
        </div>
        <div className="mse-probe-headline">
          Worst execution gap: <strong>{formatMs(summary.gap.maxGapMs)}</strong>
          {summary.gap.maxGapAfter ? ` (after ${summary.gap.maxGapAfter})` : ""}
        </div>
        <table className="mse-probe-table">
          <tbody>
            {summary.probes.map((p) => (
              <tr key={p.key}>
                <td className={`mse-probe-status mse-probe-status--${p.status}`}>
                  {p.status === "pass" ? "PASS" : p.status === "fail" ? "FAIL"
                    : p.status === "skip" ? "SKIP" : p.status === "run" ? "RUN" : "—"}
                </td>
                <td>{p.label}</td>
                <td className="mse-probe-note">{p.detail}</td>
                <td className="mse-probe-note">{stamp(p.at)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {phase === "sleeping" && (
        <section className="mse-probe-banner">
          <div className="mse-probe-banner-big">Now turn the screen off<br />and come back in 10 minutes.</div>
          <p>
            Audio will keep playing OUT LOUD — that is the test. A hidden page is only allowed to keep
            running while it is making audible sound, so a muted run would measure nothing.
          </p>
          <button onClick={stop}>Stop</button>
        </section>
      )}

      {phase !== "sleeping" && (
        <section>
          <button
            className="mse-probe-go"
            disabled={!ready}
            onClick={runGate}
          >
            {phase === "running" ? `Running… ${step || ""}`
              : waitingForCandidates ? "choosing tracks…"
                : "Run the whole gate"}
          </button>
          <p className="mse-probe-note">
            One press runs everything — capability matrix, the joins, the quota — and then starts the
            screen-off phase by itself. It plays audio out loud on purpose. Nothing else to do.
          </p>
          {candidateError && <p className="mse-probe-bad-text">Could not fetch candidates: {candidateError}</p>}
        </section>
      )}

      <section>
        <h2>What it will play</h2>
        {!candidates && !candidateError && <p className="mse-probe-note">choosing tracks…</p>}
        {candidates && (
          <table className="mse-probe-table">
            <tbody>
              {slots.map(([key, label]) => (
                <tr key={key}>
                  <td>{label}</td>
                  <td className="mse-probe-note">{describeCandidate(candidates[key])}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {caps && (
        <section>
          <h2>Capability matrix</h2>
          <table className="mse-probe-table">
            <tbody>
              {caps.rows.map((row) => (
                <tr key={row.key} className={row.trap && row.supported ? "mse-probe-bad" : undefined}>
                  <td>{row.label}</td>
                  <td><code>{row.mime}</code></td>
                  <td className={row.supported ? "mse-probe-yes" : "mse-probe-no"}>
                    {row.supported ? "yes" : "NO"}
                  </td>
                  <td className="mse-probe-note">{row.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="mse-probe-note">
            MediaSource: <strong>{caps.hasMediaSource ? "yes" : "NO"}</strong> · ManagedMediaSource:{" "}
            <strong>{caps.hasManagedMediaSource ? "yes" : "no"}</strong>
          </p>
          <p className="mse-probe-note">{typeof navigator !== "undefined" ? navigator.userAgent : ""}</p>
        </section>
      )}

      <section>
        <button className="mse-probe-fold" onClick={() => setAdvanced((v) => !v)}>
          {advanced ? "Hide" : "Show"} the raw record
        </button>
        {advanced && (
          <>
            <h2>Fetch log</h2>
            <div className="mse-probe-log">
              {fetchLog.slice(-60).map((f, i) => (
                <div key={`${f.seq}-${f.state}-${i}`}>
                  {stamp(f.issuedAt)} <code>#{f.seq}</code> {f.state}
                  {f.hiddenAtIssue ? " [issued hidden]" : ""}
                  {f.elapsedMs != null ? ` ${formatMs(f.elapsedMs)}` : ""}
                  {f.bytes ? ` ${formatBytes(f.bytes)}` : ""}
                  {f.error ? ` — ${f.error}` : ""}
                </div>
              ))}
            </div>
            <h2>Event census</h2>
            <p className="mse-probe-note">
              {summary.gap.count} events over {formatMs(summary.gap.spanMs)} · median{" "}
              {formatMs(summary.gap.medianGapMs)} · p95 {formatMs(summary.gap.p95GapMs)}
            </p>
            <table className="mse-probe-table">
              <tbody>
                {summary.gap.buckets.map((b) => (
                  <tr key={b.label}><td>{b.label}</td><td>{b.count}</td></tr>
                ))}
              </tbody>
            </table>
            <h2>Full results</h2>
            <pre className="mse-probe-raw">{JSON.stringify(results, null, 1)}</pre>
          </>
        )}
      </section>

      {/* One persistent element for every probe — the same shape the engine will have. */}
      <audio ref={audioRef} playsInline />
    </div>
  );
}
