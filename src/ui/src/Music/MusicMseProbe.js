import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { MovieAPI } from "../MovieAPI";
import { diagEnabled, setDiagEnabled } from "./musicDiag";
import "./MusicMseProbe.css";

// ── Phase 1 of music-mse-plan.md: prove the bytes, ON THE PHONE ──────────────────────────────────
//
// This page is the plan's GATE. Everything past it (the MSE engine, the timeline module, eviction)
// is forbidden until the five probes below have been run on the phone that actually fails — Android
// Chrome — because every measurement taken so far was desktop Chromium and was API acceptance, not
// playback. If probe 4 says a fetch issued while hidden never lands, the design is falsified and no
// client engineering rescues it.
//
// Why a committed page rather than a lab rig: the test has to be "visit a URL on the phone". A
// scratchpad script can't be run on the device that has the bug, and the capability matrix at the
// top is worth keeping forever — "what does browser X on phone Y get?" should be answerable by
// visiting a URL and reading a table, which is how the general case stays measured instead of
// assumed.
//
// Two rules this page inherits from the diag work that came before it (musicDiag.js):
//   • EVIDENCE MUST SURVIVE. The interesting moments happen with the screen off, minutes before
//     anyone can look, and the page may be reloaded or discarded in between. So every observation is
//     written to localStorage AS IT HAPPENS, never accumulated in memory and rendered at the end.
//   • NOTHING LOAD-BEARING RUNS ON A TIMER. Intervals stop firing (or arrive minutes late) on a
//     hidden page; media and SourceBuffer events still fire. The pumps below are driven from both,
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
 * How a fetch is cut into appends. Chrome's audio SourceBuffer quota is on the order of 12 MB —
 * LESS THAN ONE LARGE FLAC — so "append the track" is never a single operation; the working unit is
 * a chunk. Probe 3 measures the real number and this is the arithmetic every append loop uses.
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

function formatMs(ms) {
  if (!(ms > 0)) return "0 ms";
  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;
}

// ── Persistence ──────────────────────────────────────────────────────────────────────────────────
// Namespaced like every other music key. Written on every observation, not at the end of a run: a
// screen-off session that ends in a reload must still leave its evidence behind.

const K_PICKS = "music.mse.picks";
const K_CENSUS = "music.mse.census";
const K_FETCH = "music.mse.fetchlog";
const K_RESULTS = "music.mse.results";
const CENSUS_MAX = 600;
const FETCH_MAX = 200;

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
  const sessionRef = useRef(null);   // { ms, sb, mime, stop() }
  const censusRef = useRef([]);
  const fetchLogRef = useRef([]);
  const pumpRef = useRef(null);      // the screen-off fetch pump's mutable state
  const lastPersistRef = useRef(0);  // see logCensus: the ring must not cost more than it measures

  const [picks, setPicks] = useState(() => loadJson(K_PICKS, {}));
  const [results, setResults] = useState(() => loadJson(K_RESULTS, {}));
  const [census, setCensus] = useState(() => loadJson(K_CENSUS, []));
  const [fetchLog, setFetchLog] = useState(() => loadJson(K_FETCH, []));
  const [busy, setBusy] = useState(null);
  const [query, setQuery] = useState("");
  const [searchHits, setSearchHits] = useState([]);
  const [slot, setSlot] = useState("a");
  const [caps, setCapsState] = useState(null);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    censusRef.current = census;
    fetchLogRef.current = fetchLog;
    // Only on mount: these refs shadow state so the event handlers can push without re-subscribing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const isTypeSupported = useCallback((mime) => {
    const MS = window.MediaSource || window.ManagedMediaSource;
    return !!(MS && MS.isTypeSupported && MS.isTypeSupported(mime));
  }, []);

  useEffect(() => {
    setCapsState(buildCapabilityMatrix({
      isTypeSupported: (mime) => window.MediaSource.isTypeSupported(mime),
      hasMediaSource: typeof window.MediaSource !== "undefined",
      hasManagedMediaSource: typeof window.ManagedMediaSource !== "undefined",
    }));
  }, []);

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

  // ── Track picking ──────────────────────────────────────────────────────────────────────────────
  // Search by title, tap a result to fill the selected slot. Deliberately manual: only a person
  // knows which of their tracks is the 96 kHz one, and the minted payload reports the rate back so a
  // pick can be confirmed or rejected rather than assumed.
  const runSearch = useCallback(async (q) => {
    if (!q || q.trim().length < 2) { setSearchHits([]); return; }
    try {
      const r = await MovieAPI.searchMusicTracks(q.trim());
      const body = await r.json();
      setSearchHits(body.tracks || []);
    } catch {
      setSearchHits([]);
    }
  }, []);

  const assign = useCallback((track) => {
    setPicks((prev) => {
      const next = { ...prev, [slot]: { id: track.id, title: track.title, artist: track.artistName } };
      saveJson(K_PICKS, next);
      return next;
    });
  }, [slot]);

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
    setRunning(false);
  }, []);

  useEffect(() => teardown, [teardown]);

  /** Opens a MediaSource on the page's one element and wires the census to it. */
  const startSession = useCallback(async (mime) => {
    teardown();
    const audio = audioRef.current;
    audio.crossOrigin = "anonymous";
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
    setRunning(true);
    return sessionRef.current;
  }, [logCensus, teardown]);

  // ── Probe 1 / 2: real bytes across a changeType ────────────────────────────────────────────────
  // One routine, two pickers. Probe 1 is the load-bearing pair (a real MP3 and a real FLAC); probe 2
  // is the same join with a 96 kHz track and a mono track, which is where the residual risk lives —
  // the plan's judgement is that the codec switch is fine and the SAMPLE RATE switch is the open
  // question.
  const runJoin = useCallback(async (slotA, slotB, key) => {
    const a = picks[slotA];
    const b = picks[slotB];
    if (!a || !b) { record(key, { verdict: "skipped", detail: "pick two tracks first" }); return; }
    setBusy(key);
    try {
      const [pa, pb] = await mint([a.id, b.id]);
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

      // The switch itself. Same MIME on both sides (two universal-lane tracks, say) needs no
      // changeType at all — calling it anyway would be testing a different thing than the queue does.
      let changeTypeUsed = false;
      let changeTypeError = null;
      if (tb.mime !== ta.mime) {
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
      audio.addEventListener("waiting", onWaiting);
      audio.addEventListener("stalled", onWaiting);
      audio.currentTime = Math.max(0, boundary - JOIN_LEAD_SEC);
      let played = false;
      try {
        await audio.play();
        played = true;
      } catch (e) {
        appendError = appendError || `play() refused: ${e && e.message}`;
      }

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

      record(key, {
        verdict: changeTypeError ? "changeType REFUSED"
          : crossed && waitingCount === 0 ? "continuous across the join"
            : crossed ? "crossed, but the buffer went dry"
              : "did NOT cross the join",
        a: `${a.title} — ${pa.mimeType} ${pa.sampleRateHz || "?"} Hz / ${pa.channels || "?"} ch → ${ta.lane}`,
        b: `${b.title} — ${pb.mimeType} ${pb.sampleRateHz || "?"} Hz / ${pb.channels || "?"} ch → ${tb.lane}`,
        appended: `${formatBytes(appendedA)} + ${formatBytes(appendedB)}`,
        boundarySec: Math.round(boundary * 100) / 100,
        bufferedSec: Math.round(total * 100) / 100,
        reachedSec: Math.round(reached * 100) / 100,
        changeTypeUsed,
        changeTypeError,
        waitingCount,
        appendError,
      });
    } catch (e) {
      record(key, { verdict: "failed", detail: String(e && e.message ? e.message : e) });
    } finally {
      setBusy(null);
      teardown();
    }
  }, [picks, mint, isTypeSupported, startSession, record, teardown]);

  // ── Probe 3: what the audio SourceBuffer quota actually is ─────────────────────────────────────
  // Chrome's default is understood to be ~12 MB — less than one large FLAC — and every append-window
  // number in the plan is sized from the real value. Appends run with the element PAUSED AT ZERO on
  // purpose: Chrome evicts what is behind the playhead, so a playing element would quietly make room
  // and the measurement would come back as "no limit found".
  const runQuota = useCallback(async () => {
    const pick = picks.quota || picks.b || picks.a;
    if (!pick) { record("quota", { verdict: "skipped", detail: "pick a track first" }); return; }
    setBusy("quota");
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
        verdict: quotaAt === "QuotaExceededError"
          ? `quota reached at ${formatBytes(appended)}`
          : quotaAt ? `append failed: ${quotaAt}` : `no limit hit after ${formatBytes(appended)}`,
        track: `${pick.title} → ${treatment.lane}`,
        appendedBytes: appended,
        bufferedSec: Math.round(bufferedEnd(sb) * 10) / 10,
        chunkBytes: chunks[0] ? chunks[0].byteLength : 0,
        chunkCount: chunkRanges(appended, QUOTA_CHUNK_BYTES).length,
      });
    } catch (e) {
      record("quota", { verdict: "failed", detail: String(e && e.message ? e.message : e) });
    } finally {
      setBusy(null);
      teardown();
    }
  }, [picks, mint, isTypeSupported, startSession, record, teardown]);

  // ── Probe 4 / 5: the screen-off session ────────────────────────────────────────────────────────
  // THE gate. Track A plays from the SourceBuffer; meanwhile the page keeps issuing the fetch it
  // would issue for track n+1, logging the moment each one is issued and the moment it completes. If
  // those fetches stop landing once the screen goes off, the design is falsified — MSE moves fetching
  // back into script, and this is the bet being called.
  //
  // The pump is driven from `updateend` and `timeupdate` as well as an interval, because the interval
  // is the one trigger known NOT to survive a hidden page. The first successful fetch is APPENDED, so
  // the boundary is crossed while hidden too; later ones are measurements and their bytes are
  // discarded.
  const startSleepSession = useCallback(async ({ pump }) => {
    const a = picks.a;
    const b = picks.b || picks.a;
    if (!a) { record(pump ? "sleepFetch" : "census", { verdict: "skipped", detail: "pick track A first" }); return; }
    setBusy(pump ? "sleepFetch" : "census");
    try {
      const [pa, pb] = await mint(b.id === a.id ? [a.id] : [a.id, b.id]).then((r) => (r.length === 1 ? [r[0], r[0]] : r));
      const ta = treatmentFor(pa, isTypeSupported);
      const tb = treatmentFor(pb, isTypeSupported);
      if (!ta) throw new Error("no supported treatment for track A");
      if (pump && !tb) throw new Error("no supported treatment for track B — nothing to fetch");

      const session = await startSession(ta.mime);
      const { sb } = session;
      const audio = audioRef.current;

      // Fill with as much of A as the buffer will take. A QuotaExceeded here is not a failure of the
      // session — it is the quota, which probe 3 measures properly; we simply stop filling and play
      // what we have.
      let stoppedBy = null;
      try {
        await fetchChunks(ta.url, {
          onChunk: async (chunk) => {
            if (stoppedBy) return;
            try { await appendChunk(sb, chunk); } catch (e) { stoppedBy = e && e.name ? e.name : String(e); }
          },
        });
      } catch (e) {
        stoppedBy = stoppedBy || String(e && e.message ? e.message : e);
      }
      logCensus("probe:filled", { sec: Math.round(bufferedEnd(sb)), stoppedBy });

      await audio.play();
      logCensus("probe:playing", null);

      if (!pump) {
        record("census", {
          verdict: "recording — turn the screen off, leave it, then come back",
          track: `${a.title} → ${ta.lane}`,
          bufferedSec: Math.round(bufferedEnd(sb)),
        });
        return;
      }

      const state = { stopped: false, inFlight: false, attempts: 0, appended: false, lastAt: 0 };
      pumpRef.current = state;

      const attempt = async () => {
        if (state.stopped || state.inFlight || state.attempts >= 40) return;
        if (Date.now() - state.lastAt < 5000) return;
        state.inFlight = true;
        state.lastAt = Date.now();
        const seq = (state.attempts += 1);
        const issuedAt = Date.now();
        const hiddenAtIssue = document.visibilityState === "hidden";
        // Written BEFORE the fetch resolves. A fetch that never completes is the finding, and it
        // leaves no trace at all unless its issue was recorded on its own.
        logFetch({ seq, issuedAt, hiddenAtIssue, state: "issued" });
        let bytes = 0;
        let error = null;
        try {
          bytes = await fetchChunks(tb.url, {
            maxBytes: state.appended ? 512 * 1024 : Infinity,
            onChunk: async (chunk) => {
              // Only the first success is appended — after that the buffer would just fill up.
              if (!state.appended) {
                if (tb.mime !== session.mime) {
                  try { sb.changeType(tb.mime); session.mime = tb.mime; } catch { /* recorded by the join probe */ }
                }
                try { await appendChunk(sb, chunk); } catch { /* quota: the measurement is the fetch */ }
              }
            },
          });
          if (!state.appended) state.appended = true;
        } catch (e) {
          error = String(e && e.message ? e.message : e);
        }
        const completedAt = Date.now();
        logFetch({
          seq,
          issuedAt,
          completedAt,
          elapsedMs: completedAt - issuedAt,
          hiddenAtIssue,
          hiddenAtCompletion: document.visibilityState === "hidden",
          bytes,
          error,
          state: error ? "failed" : "completed",
        });
        state.inFlight = false;
      };

      // Three triggers, on purpose. The interval is the accelerator that only works awake; the media
      // and SourceBuffer events are the ones the clock rule says survive.
      const onTick = () => { attempt(); };
      const timer = window.setInterval(onTick, 7000);
      audio.addEventListener("timeupdate", onTick);
      sb.addEventListener("updateend", onTick);
      document.addEventListener("visibilitychange", onTick);
      const prevDetach = session.detach;
      session.detach = () => {
        window.clearInterval(timer);
        audio.removeEventListener("timeupdate", onTick);
        try { sb.removeEventListener("updateend", onTick); } catch { /* gone with the source */ }
        document.removeEventListener("visibilitychange", onTick);
        prevDetach();
      };

      record("sleepFetch", {
        verdict: "running — turn the screen off, wait a few minutes, then come back and read the log",
        a: `${a.title} → ${ta.lane}`,
        b: `${b.title} → ${tb ? tb.lane : "?"}`,
        bufferedSec: Math.round(bufferedEnd(sb)),
      });
    } catch (e) {
      record(pump ? "sleepFetch" : "census", { verdict: "failed", detail: String(e && e.message ? e.message : e) });
      teardown();
    } finally {
      setBusy(null);
    }
  }, [picks, mint, isTypeSupported, startSession, record, logCensus, logFetch, teardown]);

  const gaps = useMemo(() => gapDistribution(census), [census]);
  const hiddenFetches = useMemo(
    () => fetchLog.filter((f) => f.hiddenAtIssue && f.state !== "issued"),
    [fetchLog],
  );
  const hiddenIssued = useMemo(
    () => new Set(fetchLog.filter((f) => f.hiddenAtIssue).map((f) => f.seq)).size,
    [fetchLog],
  );

  // Same gate as the diag panel (musicDiag.js): `?diag=1` turns it on and is remembered, so the test
  // really is "visit a URL on the phone" — and the page stays out of the way of ordinary listening.
  if (!diagEnabled()) {
    return (
      <div className="mse-probe">
        <h1>MSE probe</h1>
        <p>
          Diagnostics are off. Add <code>?diag=1</code> to the URL, or:
        </p>
        <button onClick={() => setDiagEnabled(true)}>Turn diagnostics on</button>
      </div>
    );
  }

  const slots = [
    ["a", "A — an MP3"],
    ["b", "B — a FLAC"],
    ["hires", "96 kHz"],
    ["mono", "mono"],
    ["quota", "big FLAC"],
  ];

  return (
    <div className="mse-probe">
      <h1>MSE probe — Phase 1 gate</h1>
      <p className="mse-probe-lede">
        The gate for <code>music-mse-plan.md</code>: real bytes, real appends, on the phone that
        actually fails. Everything is written to localStorage as it happens, so a reload does not
        erase the run. Nothing here touches the music player — which means the player must be STOPPED
        before a run, or two elements will be playing at once and the census will be measuring both.
      </p>

      {/* The permanent capability reporter. */}
      <section>
        <h2>Capability matrix</h2>
        {!caps ? <p>probing…</p> : (
          <>
            <table className="mse-probe-table">
              <tbody>
                {caps.rows.map((row) => (
                  <tr key={row.key} className={row.trap && row.supported ? "mse-probe-bad" : undefined}>
                    <td>{row.label}</td>
                    <td><code>{row.mime}</code></td>
                    <td className={row.supported ? "mse-probe-yes" : "mse-probe-no"}>
                      {row.supported ? "supported" : "NO"}
                    </td>
                    <td className="mse-probe-note">{row.note}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="mse-probe-note">
              MediaSource: <strong>{caps.hasMediaSource ? "yes" : "NO"}</strong> · ManagedMediaSource:{" "}
              <strong>{caps.hasManagedMediaSource ? "yes" : "no"}</strong> · any usable treatment:{" "}
              <strong>{caps.anyTreatment ? "yes" : "NO — this browser keeps the deck player"}</strong>
            </p>
            <p className="mse-probe-note">{navigator.userAgent}</p>
          </>
        )}
      </section>

      <section>
        <h2>Tracks</h2>
        <div className="mse-probe-slots">
          {slots.map(([key, label]) => (
            <button
              key={key}
              className={slot === key ? "mse-probe-slot mse-probe-slot--on" : "mse-probe-slot"}
              onClick={() => setSlot(key)}
            >
              {label}
              <span>{picks[key] ? picks[key].title : "— none —"}</span>
            </button>
          ))}
        </div>
        <div className="mse-probe-search">
          <input
            value={query}
            placeholder="search titles, then tap a result to fill the selected slot"
            onChange={(e) => { setQuery(e.target.value); runSearch(e.target.value); }}
          />
        </div>
        <ul className="mse-probe-hits">
          {searchHits.slice(0, 12).map((t) => (
            <li key={t.id}>
              <button onClick={() => assign(t)}>{t.title} — {t.artistName}</button>
            </li>
          ))}
        </ul>
      </section>

      <section>
        <h2>1 · Real bytes across a changeType</h2>
        <p className="mse-probe-note">
          Appends a prefix of A and a prefix of B into ONE SourceBuffer with a <code>changeType</code>{" "}
          between, then plays across the join and watches the playhead cross it.
        </p>
        <button disabled={!!busy} onClick={() => runJoin("a", "b", "join")}>
          {busy === "join" ? "running…" : "Run A → B join"}
        </button>
        <Result value={results.join} />
      </section>

      <section>
        <h2>2 · The same join at 96 kHz, and mono</h2>
        <p className="mse-probe-note">
          The codec switch is measured fine; the SAMPLE RATE switch is the open question. The minted
          payload reports each track&apos;s real rate and channel count, so a wrong pick shows up in the
          result rather than silently passing.
        </p>
        <button disabled={!!busy} onClick={() => runJoin("a", "hires", "joinHires")}>
          {busy === "joinHires" ? "running…" : "Run A → 96 kHz"}
        </button>
        <button disabled={!!busy} onClick={() => runJoin("a", "mono", "joinMono")}>
          {busy === "joinMono" ? "running…" : "Run A → mono"}
        </button>
        <Result value={results.joinHires} label="96 kHz" />
        <Result value={results.joinMono} label="mono" />
      </section>

      <section>
        <h2>3 · SourceBuffer quota</h2>
        <p className="mse-probe-note">
          Chunk-appends with the element paused at zero (so nothing can be evicted) until
          <code> QuotaExceededError</code>, and reports the bytes. Every append-window number in the
          plan is sized from this.
        </p>
        <button disabled={!!busy} onClick={runQuota}>
          {busy === "quota" ? "measuring…" : "Measure quota"}
        </button>
        <Result value={results.quota} />
      </section>

      <section>
        <h2>4 · Screen-off fetch — the design&apos;s central bet</h2>
        <p className="mse-probe-note">
          Starts MSE playback of A, then keeps issuing the fetch for B and logging when each one was
          issued and when it came back. Start it, turn the screen off, leave it a few minutes, then
          come back and read the table. If fetches issued while hidden never complete, the design is
          falsified and Phase 2 must not be built.
        </p>
        <button disabled={!!busy} onClick={() => startSleepSession({ pump: true })}>
          {busy === "sleepFetch" ? "starting…" : "Start screen-off fetch test"}
        </button>
        {running && <button onClick={teardown}>Stop</button>}
        <Result value={results.sleepFetch} />
        <p className="mse-probe-note">
          {fetchLog.length} attempts logged · {hiddenIssued} issued while hidden ·{" "}
          <strong>{hiddenFetches.filter((f) => f.state === "completed").length} of those completed</strong>
        </p>
        <div className="mse-probe-log">
          {fetchLog.slice(-40).map((f, i) => (
            <div key={`${f.seq}-${f.state}-${i}`}>
              <code>#{f.seq}</code> {f.state}
              {f.hiddenAtIssue ? " [issued hidden]" : ""}
              {f.elapsedMs != null ? ` ${formatMs(f.elapsedMs)}` : ""}
              {f.bytes ? ` ${formatBytes(f.bytes)}` : ""}
              {f.error ? ` — ${f.error}` : ""}
            </div>
          ))}
        </div>
      </section>

      <section>
        <h2>5 · Event census</h2>
        <p className="mse-probe-note">
          Every media event, <code>updateend</code>, interval tick and visibility change, timestamped.
          The worst gap is the headline: it is the lookahead floor for every route in the plan.
        </p>
        <button disabled={!!busy} onClick={() => startSleepSession({ pump: false })}>
          {busy === "census" ? "starting…" : "Play + record census"}
        </button>
        <button onClick={() => { censusRef.current = []; saveJson(K_CENSUS, []); setCensus([]); }}>
          Clear census
        </button>
        <Result value={results.census} />
        <p className="mse-probe-headline">
          worst gap <strong>{formatMs(gaps.maxGapMs)}</strong>
          {gaps.maxGapAfter ? ` (after ${gaps.maxGapAfter})` : ""} · median {formatMs(gaps.medianGapMs)} ·
          p95 {formatMs(gaps.p95GapMs)} · {gaps.count} events over {formatMs(gaps.spanMs)}
        </p>
        <table className="mse-probe-table">
          <tbody>
            {gaps.buckets.map((b) => (
              <tr key={b.label}><td>{b.label}</td><td>{b.count}</td></tr>
            ))}
          </tbody>
        </table>
      </section>

      {/* One persistent element for every probe — the same shape the engine will have. */}
      <audio ref={audioRef} playsInline />
    </div>
  );
}

function Result({ value, label }) {
  if (!value) return null;
  return (
    <div className="mse-probe-result">
      <strong>{label ? `${label}: ` : ""}{value.verdict || value.detail}</strong>
      <pre>{JSON.stringify(value, null, 1)}</pre>
    </div>
  );
}
