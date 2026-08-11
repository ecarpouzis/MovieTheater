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

// ── The sleep-viability rule (music-mse-plan.md §"MSE route, asleep, forever") ────────────────────
// Measured on the phone 2026-08-11, and it is the rule that was missing when the sleep phase died:
// a page whose audio stops loses its audible exemption and FREEZES, so the buffer must hold more
// audio than the longest stretch the page can go without being allowed to run. The buffer's size is
// capped by the SourceBuffer quota, so the runway a treatment buys is `quota ÷ bitrate` — and a
// 96 kHz FLAC at ~0.3 MB/s bought 40 s against an 84 s execution gap. It went silent and never got
// another instruction.
//
// So: bitrate is judged per TRACK from real bytes, never per format, and a track that breaks the
// ceiling is appended from the universal lane (AAC ~256 kbps) instead while hidden. The phone-proven
// rate-switch changeType is what makes that switch legal.

/** Chrome's audio SourceBuffer quota, used only until probe 3 has measured the real one. */
export const ASSUMED_QUOTA_BYTES = 12 * 1024 * 1024;
/** Gap floor before the census has anything to say. The phone measured 84 s; assuming less than
 *  this would license exactly the choice that killed the last run. */
export const DEFAULT_WORST_GAP_MS = 90000;
/** How many worst-gaps of runway a treatment must buy. One would be the bare edge of survival. */
export const SLEEP_MARGIN = 2;

export function formatBitrate(bytesPerSec) {
  if (!(bytesPerSec > 0)) return "unknown";
  return `${((bytesPerSec * 8) / 1e6).toFixed(2)} Mbps`;
}

/**
 * Which lane a track should be appended from WHILE HIDDEN, and why in words.
 *
 * The "why" is not decoration: the verdict panel prints it, so a run that demotes a track to the
 * universal lane can be seen doing so rather than inferred from a lane name. Pure and tested
 * because this is the rule the phone paid for.
 */
export function sleepLaneDecision({ sizeBytes, durationSec, bitPerfect, universal, quotaBytes, worstGapMs }) {
  const quota = quotaBytes > 0 ? quotaBytes : ASSUMED_QUOTA_BYTES;
  const gapMs = Math.max(DEFAULT_WORST_GAP_MS, worstGapMs || 0);
  const ceiling = quota / ((gapMs / 1000) * SLEEP_MARGIN);
  const bytesPerSec = sizeBytes > 0 && durationSec > 0 ? sizeBytes / durationSec : 0;
  const runwaySec = bytesPerSec > 0 ? quota / bytesPerSec : 0;
  const base = {
    bytesPerSec,
    runwaySec: Math.round(runwaySec),
    ceilingBytesPerSec: ceiling,
    quotaBytes: quota,
    worstGapMs: gapMs,
  };

  if (!bitPerfect && !universal) return { ...base, treatment: null, reason: "no lane at all" };
  if (!universal) {
    // Rung 4: the gateway has no universal lane (not redeployed, no ffmpeg). Keep the bit-perfect
    // one and say so — an honest "this may not survive" beats a silent substitution.
    return {
      ...base,
      treatment: bitPerfect,
      reason: `${formatBitrate(bytesPerSec)} · no universal lane offered, kept ${bitPerfect.lane}`,
    };
  }
  if (bytesPerSec > 0 && bytesPerSec < ceiling && bitPerfect) {
    return {
      ...base,
      treatment: bitPerfect,
      reason: `${formatBitrate(bytesPerSec)} ≤ ceiling ${formatBitrate(ceiling)} → ${bitPerfect.lane} (${Math.round(runwaySec)}s runway)`,
    };
  }
  return {
    ...base,
    treatment: universal,
    reason: bytesPerSec > 0
      ? `${formatBitrate(bytesPerSec)} > ceiling ${formatBitrate(ceiling)} → universal (${Math.round(runwaySec)}s runway was not enough)`
      : "bitrate unknown → universal",
  };
}

/**
 * How long this page has spent hidden, accumulated one event at a time.
 *
 * ⚠ It must be ACCUMULATED, not derived from the census, and that is not a style preference: the
 * census is a bounded ring. Measured in the harness — a busy pump filled all 600 entries inside
 * ~20 s, so a total derived from the ring read "20 s" after three minutes hidden, and the ten-minute
 * criterion could never have been met however long the phone slept. The accumulator carries the
 * whole run in three numbers.
 *
 * Freeze-proof for the same reason the census is: the state is written to storage at every event, so
 * whatever the last event before a freeze saw is what survives — and the stretch after a hidden
 * entry counts as hidden, frozen or not, because that is exactly the time being measured.
 */
export function hiddenAdvance(state, nowAt, nowHidden) {
  const prev = state || HIDDEN_ZERO;
  // `lastAt == null` (not 0) means "no previous observation" — a sentinel that cannot collide with a
  // real timestamp. Math.max keeps a clock that jumps backwards from subtracting the run's history.
  const totalMs = prev.lastHidden && prev.lastAt != null
    ? prev.totalMs + Math.max(0, nowAt - prev.lastAt)
    : prev.totalMs;
  return { totalMs, lastAt: nowAt, lastHidden: !!nowHidden };
}

/** A clock that has observed nothing yet. */
export const HIDDEN_ZERO = { totalMs: 0, lastAt: null, lastHidden: false };

/** The same sum taken over a whole census — used to seed the accumulator from a restored ring, and
 *  correct whenever the ring has not overflowed. */
export function hiddenElapsedMs(census) {
  const list = (census || []).filter((e) => e && typeof e.at === "number");
  let total = 0;
  for (let i = 1; i < list.length; i++) {
    if (list[i - 1].hidden) total += list[i].at - list[i - 1].at;
  }
  return total;
}

/** Ten minutes of screen-off with no dry buffer. Less than this has already been survived by runs
 *  that then died at T+4min, so a shorter bar would call the known failure a pass. */
export const SLEEP_ENDURANCE_MS = 10 * 60 * 1000;

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
const K_HIDDEN = "music.mse.hidden";   // { totalMs, lastAt, lastHidden } — see hiddenAdvance
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
// Three minutes of RUNWAY, not one. The phone measured an 84 s execution gap while hidden, so a
// 60 s target was under water before it started: the buffer has to outlast the longest stretch in
// which the page is not allowed to run, twice over. Seconds, computed from buffered ranges — bytes
// are the wrong unit the moment two tracks have different bitrates.
const SLEEP_TARGET_AHEAD_SEC = 180;
// A hard sanity cap on top of the quota-derived ceiling: no phone needs more than ten minutes of
// audio in flight, and past this the appends are only evicting each other.
const SLEEP_MAX_AHEAD_SEC = 600;
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
  const resultsRef = useRef(results);
  const quotaBytesRef = useRef(0);   // probe 3's measurement, fed into the sleep-viability rule
  const [census, setCensus] = useState(() => loadJson(K_CENSUS, []));
  const [hiddenMs, setHiddenMs] = useState(() => loadJson(K_HIDDEN, { totalMs: 0 }).totalMs || 0);
  const hiddenRef = useRef(loadJson(K_HIDDEN, HIDDEN_ZERO));
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

  // ⚠ Writes localStorage SYNCHRONOUSLY, from a ref, before touching React state.
  //
  // The death this page now has to record is a `waiting` on a hidden page — and the page usually
  // gets no further execution after one, because silent audio costs it the exemption that was
  // letting it run at all. A result queued behind a state updater is a result that never lands: the
  // last run died at T+4min and the panel still said RUNNING when it was picked up. So the ref is
  // the source of truth and setState only refreshes what is on screen.
  const record = useCallback((key, value) => {
    const next = { ...resultsRef.current, [key]: { ...value, at: Date.now() } };
    resultsRef.current = next;
    saveJson(K_RESULTS, next);
    setResults(next);
    return next;
  }, []);

  const logCensus = useCallback((event, data) => {
    const now = Date.now();
    const hidden = document.visibilityState === "hidden";
    censusRef.current = pushRing(censusRef.current, { at: now, event, hidden, data: data || null }, CENSUS_MAX);
    // The endurance clock, advanced on every event and never derived from the (bounded) ring.
    hiddenRef.current = hiddenAdvance(hiddenRef.current, now, hidden);

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
      saveJson(K_HIDDEN, hiddenRef.current);
      setCensus(censusRef.current);
      setHiddenMs(hiddenRef.current.totalMs);
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

      // The number the sleep-viability rule divides by. Kept in a ref because the sleep phase reads
      // it a few hundred milliseconds later, inside the same run.
      if (quotaAt === "QuotaExceededError") quotaBytesRef.current = appended;

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

      // ── Apply the sleep-viability rule to every candidate ────────────────────────────────────
      // The inputs are measurements from THIS run: probe 3's quota and the census's worst gap so
      // far (floored, because a run that has not been asleep yet has not seen a real gap).
      const quotaBytes = quotaBytesRef.current;
      const worstGapMs = gapDistribution(censusRef.current).maxGapMs;
      const lanes = minted.map((payload) => {
        const bitPerfect = treatmentFor(payload, isTypeSupported);
        const universal = payload.universalUrl && isTypeSupported('audio/mp4; codecs="mp4a.40.2"')
          ? { lane: "universal", url: payload.universalUrl, mime: 'audio/mp4; codecs="mp4a.40.2"' }
          : null;
        const decision = sleepLaneDecision({
          sizeBytes: payload.sizeBytes,
          durationSec: payload.durationSec,
          bitPerfect,
          universal,
          quotaBytes,
          worstGapMs,
        });
        return {
          payload,
          treatment: decision.treatment,
          // Carried so the verdict panel can show the rule working rather than assert that it did.
          note: `${payload.title}: ${decision.reason}`,
          decision,
          dead: false,
        };
      }).filter((l) => l.treatment);
      if (lanes.length === 0) throw new Error("no supported treatment for any candidate");
      lanes.forEach((l) => logCensus("sleep:lane", { note: l.note }));

      const session = await startSession(lanes[0].treatment.mime);
      const { sb } = session;
      const audio = audioRef.current;
      audio.muted = false;
      audio.volume = 1;

      const state = {
        stopped: false, busy: false, i: 0, startedAt: Date.now(),
        waitingWhileHidden: 0, lastAppendAt: Date.now(), lastAheadSec: 0, dead: false, passed: false, wantPlaying: false,
      };
      pumpRef.current = state;

      const aheadSec = () => bufferedEnd(sb) - (audio.currentTime || 0);

      /** The sleep row, rewritten in place. Every write is synchronous (see record).
       *  Once the endurance bar is cleared the row stays PASS — a later progress write must not
       *  quietly demote a result that has already been earned. */
      const writeSleep = (extra) => record("sleep", {
        status: state.passed ? "pass" : "run",
        verdict: state.passed
          ? `PASS — ${formatMs(hiddenRef.current.totalMs)} hidden with no dry buffer`
          : "RUNNING — turn the screen off and come back in 10 minutes",
        lanes: lanes.map((l) => l.note),
        quotaUsedBytes: quotaBytes || ASSUMED_QUOTA_BYTES,
        quotaMeasured: quotaBytes > 0,
        worstGapUsedMs: Math.max(DEFAULT_WORST_GAP_MS, worstGapMs),
        startedAt: state.startedAt,
        waitingWhileHidden: state.waitingWhileHidden,
        bufferedAheadSec: Math.round(aheadSec()),
        ...extra,
      });

      // ── Death, recorded as an outcome ────────────────────────────────────────────────────────
      // A `waiting` on a hidden page is not a glitch to recover from: the audio has stopped, so the
      // page has lost the audible exemption that was letting it run, and this handler is very likely
      // the LAST code that executes before the freeze. The last run died exactly here and the panel
      // still read RUNNING when the phone was picked up, because nothing wrote the death down.
      // So the write happens first, synchronously, inside the handler — before any await, before any
      // recovery attempt, before anything that could be scheduled and never run.
      const noteDeath = (how) => {
        const hidden = document.visibilityState === "hidden";
        // state.stopped covers the deliberate teardown: OUR pause() must not read as a death.
        if (!hidden || state.dead || state.stopped || !state.wantPlaying) return;
        state.dead = true;
        state.waitingWhileHidden += 1;
        const now = Date.now();
        record("sleep", {
          status: "fail",
          verdict: `FAIL — ${how} at T+${Math.round((now - state.startedAt) / 1000)}s while hidden`,
          how,
          diedAtMs: now,
          elapsedSec: Math.round((now - state.startedAt) / 1000),
          sinceLastAppendSec: Math.round((now - state.lastAppendAt) / 1000),
          bufferedAheadAtDeathSec: Math.round(aheadSec() * 10) / 10,
          playheadSec: Math.round((audio.currentTime || 0) * 10) / 10,
          hiddenElapsedMs: hiddenRef.current.totalMs,
          lanes: lanes.map((l) => l.note),
          quotaUsedBytes: quotaBytes || ASSUMED_QUOTA_BYTES,
          worstGapUsedMs: Math.max(DEFAULT_WORST_GAP_MS, worstGapMs),
        });
        logCensus("sleep:died", { how, aheadSec: Math.round(aheadSec() * 10) / 10 });
      };

      // Every way the audio can stop, not just the one that was expected. A dry buffer was how the
      // phone died, but the thing being measured is "did the sound keep coming out" — an element
      // that PAUSES with three minutes buffered has failed just as completely, and a probe that only
      // watched `waiting` would have called that a healthy run. Whatever stopped it, the page then
      // tries to start it again: recovery is worth attempting, and the FAIL is already written down
      // so a recovery cannot erase the finding.
      const onWaiting = () => noteDeath("buffer ran dry");
      const onPause = () => { noteDeath("playback paused"); playOrReport(audio, 5000).then((o) => logCensus("sleep:resume", { o })); };
      const onEnded = () => noteDeath("the stream ended");
      const onError = () => noteDeath(`the element errored (${audio.error ? audio.error.code : "?"})`);
      audio.addEventListener("waiting", onWaiting);
      audio.addEventListener("stalled", onWaiting);
      audio.addEventListener("pause", onPause);
      audio.addEventListener("ended", onEnded);
      audio.addEventListener("error", onError);

      /** Ten minutes hidden with a buffer that never ran dry. Checked at every opportunity, because
       *  the opportunity that crosses the line may be the last one the page gets. */
      const checkEndurance = () => {
        if (state.dead || state.stopped) return;
        const hiddenMs = hiddenRef.current.totalMs;
        if (hiddenMs >= SLEEP_ENDURANCE_MS && state.waitingWhileHidden === 0) {
          state.passed = true;
          record("sleep", {
            status: "pass",
            verdict: `PASS — ${formatMs(hiddenMs)} hidden with no dry buffer`,
            hiddenElapsedMs: hiddenMs,
            elapsedSec: Math.round((Date.now() - state.startedAt) / 1000),
            lanes: lanes.map((l) => l.note),
            quotaUsedBytes: quotaBytes || ASSUMED_QUOTA_BYTES,
            worstGapUsedMs: Math.max(DEFAULT_WORST_GAP_MS, worstGapMs),
            waitingWhileHidden: 0,
          });
        }
      };

      // One cycle: evict what has been played, then fetch and append the next candidate. Both halves
      // matter — without eviction the quota ends the session in minutes, and the phase has to last
      // longer than a person's walk away.
      const cycle = async () => {
        if (state.stopped || state.busy || !sessionRef.current) return;
        const hidden = document.visibilityState === "hidden";
        // Pick the lane FIRST, because how far ahead is worth buffering depends on what is about to
        // be appended: the quota is a byte budget, and only a bitrate turns it into seconds.
        //
        // Skip lanes that have proved unfetchable (a gateway without the universal lane 404s it).
        let lane = null;
        for (let n = 0; n < lanes.length; n++) {
          const candidate = lanes[(state.i + n) % lanes.length];
          if (!candidate.dead) { lane = candidate; break; }
        }
        if (!lane) {
          state.stopped = true;
          record("sleep", { status: "fail", verdict: "FAIL — every lane failed to fetch; nothing left to play" });
          return;
        }

        // ⚠ While HIDDEN there is no low-water mark: every execution opportunity tops up, because the
        // next one is not schedulable. But "top up" still has a ceiling, and the ceiling IS the
        // quota — expressed in seconds of THIS lane's audio, since appending past what the buffer can
        // hold only evicts what is already there. Measured without this: the pump issued ~6 fetches a
        // second for as long as it was left running, which on a phone is a battery and radio bill for
        // audio that could not be kept anyway, and against a cold gateway cache would be one ffmpeg
        // encode per request.
        const ceilingSec = Math.min(
          SLEEP_MAX_AHEAD_SEC,
          Math.max(SLEEP_TARGET_AHEAD_SEC, Math.round((lane.decision.runwaySec || 0) * 0.8)),
        );
        if (aheadSec() >= (hidden ? ceilingSec : SLEEP_TARGET_AHEAD_SEC)) { checkEndurance(); return; }

        state.busy = true;
        try {
          const keepFrom = (audio.currentTime || 0) - SLEEP_KEEP_BEHIND_SEC;
          if (keepFrom > 0) await removeRange(sb, 0, keepFrom);
          state.i = (lanes.indexOf(lane) + 1) % lanes.length;

          const switchReason = switchReasonFor(
            { mime: sessionRef.current.mime, sampleRateHz: sessionRef.current.sampleRateHz, channels: sessionRef.current.channels },
            { mime: lane.treatment.mime, sampleRateHz: lane.payload.sampleRateHz, channels: lane.payload.channels },
          );
          if (switchReason) {
            try {
              // The rate-switch changeType proven on the phone — it is what makes demoting a hi-res
              // track to the universal lane mid-buffer legal.
              sb.changeType(lane.treatment.mime);
              sessionRef.current.mime = lane.treatment.mime;
              sessionRef.current.sampleRateHz = lane.payload.sampleRateHz;
              sessionRef.current.channels = lane.payload.channels;
            } catch (e) {
              logCensus("changeType:refused", { switchReason, error: String(e && e.message ? e.message : e) });
            }
          }

          const seq = (seqRef.current += 1);
          const issuedAt = Date.now();
          const hiddenAtIssue = document.visibilityState === "hidden";
          // Written BEFORE the fetch resolves. A fetch that never completes is THE finding, and it
          // leaves no trace at all unless its issue was recorded on its own.
          logFetch({
            seq, trackId: lane.payload.trackId, lane: lane.treatment.lane, issuedAt, hiddenAtIssue,
            aheadSec: Math.round(aheadSec()), state: "issued",
          });

          let bytes = 0;
          let error = null;
          try {
            bytes = await fetchChunks(lane.treatment.url, {
              maxBytes: SLEEP_FILL_BYTES,
              onChunk: async (chunk) => {
                try {
                  await appendChunk(sb, chunk);
                  state.lastAppendAt = Date.now();
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
            // One failure is enough to retire a lane: the failures here are 404 (lane not deployed)
            // and CORS, neither of which gets better by being asked again — and asking again costs
            // an execution window the page may not get back.
            lane.dead = true;
          }
          state.lastError = error;
          const completedAt = Date.now();
          logFetch({
            seq,
            trackId: lane.payload.trackId,
            lane: lane.treatment.lane,
            issuedAt,
            completedAt,
            elapsedMs: completedAt - issuedAt,
            hiddenAtIssue,
            hiddenAtCompletion: document.visibilityState === "hidden",
            bytes,
            error,
            state: error ? "failed" : "completed",
          });
          state.lastAheadSec = aheadSec();
          if (!state.dead) writeSleep({ bufferedAheadSec: Math.round(state.lastAheadSec) });
          checkEndurance();
        } finally {
          state.busy = false;
        }
      };

      // Prime the buffer before playing: the phase must start with audio, not with a wait. If the
      // priming fetch brought back nothing there is no point starting a ten-minute walk-away — say
      // why now, while someone is still holding the phone. Every lane gets a turn, because the first
      // one may be a universal lane the gateway does not serve yet.
      for (let n = 0; n < lanes.length && bufferedEnd(sb) <= 0 && !state.stopped; n++) {
        // eslint-disable-next-line no-await-in-loop
        await cycle();
      }
      if (bufferedEnd(sb) <= 0) throw new Error(state.lastError || "no audio could be fetched to play");
      const playOutcome = await playOrReport(audio);
      logCensus("probe:playing", { bufferedSec: Math.round(bufferedEnd(sb)), playOutcome });
      if (playOutcome !== "playing") throw new Error(playOutcome);
      // From here on, any stop is a death rather than us not having started yet.
      state.wantPlaying = true;

      // Fill to the target before anyone walks away: the first hidden stretch is the one with no
      // measured gap behind it, so it gets the biggest runway we can give it.
      for (let n = 0; n < 8 && aheadSec() < SLEEP_TARGET_AHEAD_SEC && !state.stopped; n++) {
        // eslint-disable-next-line no-await-in-loop
        await cycle();
      }

      // Three kinds of trigger, on purpose. The interval is the accelerator that only works awake;
      // the media and SourceBuffer events are the ones the clock rule says survive a hidden page.
      const onTrigger = () => { cycle(); };
      const timer = window.setInterval(onTrigger, SLEEP_PUMP_MS);
      audio.addEventListener("timeupdate", onTrigger);
      audio.addEventListener("waiting", onTrigger);
      audio.addEventListener("progress", onTrigger);
      audio.addEventListener("playing", onTrigger);
      sb.addEventListener("updateend", onTrigger);
      document.addEventListener("visibilitychange", onTrigger);
      const prevDetach = session.detach;
      session.detach = () => {
        window.clearInterval(timer);
        audio.removeEventListener("timeupdate", onTrigger);
        audio.removeEventListener("waiting", onTrigger);
        audio.removeEventListener("progress", onTrigger);
        audio.removeEventListener("playing", onTrigger);
        audio.removeEventListener("waiting", onWaiting);
        audio.removeEventListener("stalled", onWaiting);
        audio.removeEventListener("pause", onPause);
        audio.removeEventListener("ended", onEnded);
        audio.removeEventListener("error", onError);
        try { sb.removeEventListener("updateend", onTrigger); } catch { /* gone with the source */ }
        document.removeEventListener("visibilitychange", onTrigger);
        prevDetach();
      };

      setPhase("sleeping");
      writeSleep({});
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
    hiddenRef.current = HIDDEN_ZERO;
    saveJson(K_CENSUS, []);
    saveJson(K_FETCH, []);
    saveJson(K_HIDDEN, hiddenRef.current);
    setCensus([]);
    setFetchLog([]);
    setHiddenMs(0);
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
    // Annotate, never downgrade. A run that had already earned its PASS (or recorded its death) must
    // not have that erased by the person who pressed Stop afterwards — which is exactly what would
    // happen if this wrote an unconditional "run" row over the top.
    const prev = resultsRef.current.sleep;
    record("sleep", {
      ...(prev || {}),
      status: prev && (prev.status === "pass" || prev.status === "fail") ? prev.status : "skip",
      verdict: prev && prev.verdict && prev.status !== "run"
        ? `${prev.verdict} (stopped by hand afterwards)`
        : "STOPPED by hand before the endurance bar was reached",
      stoppedAt: Date.now(),
    });
  }, [teardown, record]);

  const summary = useMemo(
    () => summarizeRun({ results, fetchLog, census }),
    [results, fetchLog, census],
  );
  const sleepLanes = (results.sleep && results.sleep.lanes) || [];

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
        {/* The endurance clock. A returning user's first question is "did it run long enough?", and
            the last run's answer to that was buried in a fetch log. */}
        <div className="mse-probe-headline">
          Screen-off so far: <strong>{formatMs(hiddenMs)}</strong> of {formatMs(SLEEP_ENDURANCE_MS)} needed
          {hiddenMs >= SLEEP_ENDURANCE_MS ? " ✓" : ""}
        </div>
        {sleepLanes.length > 0 && (
          <div className="mse-probe-lanes">
            {sleepLanes.map((note) => <div key={note}>{note}</div>)}
          </div>
        )}
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
            running while it is making audible sound, so a muted run would measure nothing. If the
            music stops before you come back, that IS the failure, and the page will have written it
            down.
          </p>
          <div className="mse-probe-headline">
            Screen-off so far: <strong>{formatMs(hiddenMs)}</strong> of {formatMs(SLEEP_ENDURANCE_MS)}
          </div>
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
