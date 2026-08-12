// The treatment matrix and the two rules that decide how a track's bytes reach a SourceBuffer
// (music-mse-plan.md §"Mixed FLAC/MP3 queues", §"MSE route, asleep, forever").
//
// This module exists because these rules are now read by TWO callers — the Phase 1 probe page that
// measured them and the Phase 2 engine that plays by them — and a rule that gets restated in a
// second place is a rule that will eventually disagree with itself. Every function here is pure and
// unit-tested; the probe page re-exports them so its own tests still address them by their old names.
//
// Nothing here imports React, an element, or a network client. That is deliberate: the routing
// decisions have to be testable without a MediaSource, which the test environment does not have.

/**
 * The treatment matrix's rows, as MIME strings to probe. Order is the plan's routing order, and the
 * last row is a TRAP being watched rather than a candidate: MP3-in-MP4 (`mp4a.6B`) is measured
 * unsupported in Chrome AND on the phone, which is why the fMP4 lane must never be asked for an mp3.
 * If a browser ever says yes to it, that is a finding, not a green light.
 */
export const PROBE_TYPES = [
  { key: "mpeg", label: "raw MP3", mime: "audio/mpeg", note: "the File lane, bit-perfect, no ffmpeg" },
  { key: "flac", label: "FLAC in fMP4", mime: 'audio/mp4; codecs="flac"', note: "the fMP4 lane, -c:a copy, still lossless" },
  { key: "aac", label: "AAC in fMP4", mime: 'audio/mp4; codecs="mp4a.40.2"', note: "the universal lane — every MSE browser should take this" },
  { key: "mp3mp4", label: "MP3 in fMP4", mime: 'audio/mp4; codecs="mp4a.6B"', note: "THE TRAP — expected NO; a yes here is a finding", trap: true },
];

export const MIME_MPEG = "audio/mpeg";
export const MIME_FLAC_FMP4 = 'audio/mp4; codecs="flac"';
export const MIME_AAC_FMP4 = 'audio/mp4; codecs="mp4a.40.2"';

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
    // keeps today's deck player (ladder rung 7).
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
    ? { lane: "universal", url: payload.universalUrl, mime: MIME_AAC_FMP4 }
    : null;

  let candidate = null;
  if (payload.mimeType === MIME_MPEG) {
    candidate = { lane: "file", url: payload.url, mime: MIME_MPEG };
  } else if (payload.mimeType === "audio/flac" && payload.fmp4Url) {
    candidate = { lane: "fmp4", url: payload.fmp4Url, mime: MIME_FLAC_FMP4 };
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
  if (universal) {
    let ok = false;
    try {
      ok = !!isTypeSupported(universal.mime);
    } catch {
      ok = false;
    }
    if (ok) return universal;
  }
  return null;
}

/**
 * Does moving from track A to track B need a `changeType`, and because of what?
 *
 * ⚠ The MIME string is not the only thing that can change, and assuming it was cost the probe a
 * false FAIL: appending a 96 kHz FLAC-fMP4 after a 44.1 kHz one — IDENTICAL MIME, so no changeType
 * was called — made a real Chrome's SourceBuffer raise an error after about 200 KB. With the
 * changeType it plays through, on desktop AND on the phone. That is the plan's residual risk (the
 * rate switch, not the codec switch) and its stated mitigation, so the rule is: a switch is any
 * change of container/codec, sample rate, or channel count.
 */
export function switchReasonFor(a, b) {
  if (!a || !b) return null;
  if (a.mime !== b.mime) return "codec/container";
  if ((a.sampleRateHz ?? null) !== (b.sampleRateHz ?? null)) return "sample rate";
  if ((a.channels ?? null) !== (b.channels ?? null)) return "channel count";
  return null;
}

// ── The sleep-viability rule (music-mse-plan.md §"MSE route, asleep, forever") ────────────────────
// Measured on the phone 2026-08-11, and it is the rule that was missing when the probe's sleep phase
// died: a page whose audio stops loses its audible exemption and FREEZES, so the buffer must hold
// more audio than the longest stretch the page can go without being allowed to run. The buffer is
// capped by the SourceBuffer quota, so the runway a treatment buys is `quota ÷ bitrate` — and a
// 96 kHz FLAC at ~0.3 MB/s bought 40 s against an 84 s execution gap. It went silent and never got
// another instruction.
//
// So: bitrate is judged per TRACK from real bytes, never per format, and a track that breaks the
// ceiling is appended from the universal lane (AAC ~256 kbps) instead while hidden. The phone-proven
// rate-switch changeType is what makes that switch legal.

/** Chrome's audio SourceBuffer quota. Measured 11.85 MB on desktop and on the phone; the engine
 *  assumes slightly less rather than probing for it, because probing costs a whole track's fetch at
 *  session start and being 3% conservative costs nothing. */
export const ASSUMED_QUOTA_BYTES = 11.5 * 1024 * 1024;
/** The execution gap the arithmetic is sized against. The endurance run measured 270 ms on a HEALTHY
 *  page — but the 84 s gaps of the dying run were real too, and a design that assumes the healthy
 *  number is a design that only works while it is already working. */
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
 * The "why" is not decoration: the probe page prints it and the engine logs it, so a run that
 * demotes a track to the universal lane can be seen doing so rather than inferred from a lane name.
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
 * THE routing decision the engine makes for one track: the matrix first, then the sleep-viability
 * rule on top of it.
 *
 * Visibility is an input because the two rules answer different questions. Visible, the page is
 * running continuously and a dry buffer is a glitch it will recover from within a frame — so
 * fidelity wins and the bit-perfect lane is used. Hidden, a dry buffer is the end of the session —
 * so continuity wins and anything that cannot buy enough runway is demoted to the universal lane.
 * "Fidelity while watching, continuity while asleep", which is the plan's phrasing of it.
 */
export function appendTreatmentFor({ payload, isTypeSupported, hidden, quotaBytes, worstGapMs }) {
  const bitPerfect = treatmentFor(payload, isTypeSupported);
  if (!bitPerfect) return { treatment: null, reason: "no supported treatment", demoted: false };
  if (!hidden) {
    return { treatment: bitPerfect, reason: `visible → ${bitPerfect.lane}`, demoted: false };
  }
  let universal = null;
  if (payload && payload.universalUrl) {
    try {
      if (isTypeSupported(MIME_AAC_FMP4)) universal = { lane: "universal", url: payload.universalUrl, mime: MIME_AAC_FMP4 };
    } catch { /* no universal row on this browser */ }
  }
  const decision = sleepLaneDecision({
    sizeBytes: payload.sizeBytes,
    durationSec: payload.durationSec,
    bitPerfect,
    universal,
    quotaBytes,
    worstGapMs,
  });
  return {
    treatment: decision.treatment,
    reason: decision.reason,
    demoted: !!(decision.treatment && bitPerfect && decision.treatment.lane !== bitPerfect.lane),
    runwaySec: decision.runwaySec,
  };
}

/**
 * How many seconds of THIS treatment the buffer can actually hold — the ceiling on how far ahead it
 * is worth appending, and the smaller of what we WANT and what the quota PERMITS.
 *
 * ⚠ The min is the whole point, and taking the max instead is a spin. Measured in a real browser: a
 * 38 MB hi-res FLAC (2.5 Mbps) fits about 36 s in an 11.5 MB quota, so an append loop asked to reach
 * 180 s ahead of it never can — every opportunity appended until QuotaExceeded, evicted, retried,
 * and came back for more, turning one track into 61 requests. The quota is physics; the target is
 * only a wish.
 */
export function bufferCeilingSec({ sizeBytes, durationSec, quotaBytes, targetSec }) {
  const quota = quotaBytes > 0 ? quotaBytes : ASSUMED_QUOTA_BYTES;
  const bytesPerSec = sizeBytes > 0 && durationSec > 0 ? sizeBytes / durationSec : 0;
  if (!(bytesPerSec > 0)) return targetSec;
  return Math.min(targetSec, Math.round((quota / bytesPerSec) * 0.8));
}

/** Never keep less than this behind the playhead — below it the buffer is useless for any scrub. */
export const KEEP_BEHIND_MIN_SEC = 20;
/** …and never more. Past ten minutes we would be hoarding memory nobody is going to scrub back into,
 *  and on the compressed lanes the quota would happily let us. */
export const KEEP_BEHIND_MAX_SEC = 600;

/**
 * How much ALREADY-PLAYED audio to keep in the buffer — the seek-backwards window.
 *
 * This used to be a flat 20 s, which is where "seeking goes back to the start of the song" came
 * from: `seekPlan` can only honour a target that is still buffered, so a scrub further back than
 * 20 s fell out of the buffer and restarted the track. Twenty seconds was never a considered
 * number for the compressed lanes — a 128 kbps track buys ~700 s of runway from the same quota and
 * we were throwing away all but 20 of it, for nothing.
 *
 * So: spend what the ahead window is not using. The ahead window is sized first and is not
 * negotiable — it is the sleep-survival guarantee (`bufferCeilingSec`) and a seek is a comfort.
 * The 0.9 leaves a margin so an append never has to race an eviction for the last few hundred KB.
 *
 * ⚠ On a fat bit-perfect track this correctly returns the floor and seeking still cannot be served
 * from the buffer: 11.5 MB of quota holds 61 s of a 1568 kbps FLAC, and no arithmetic here makes
 * that 297 s. That case is the seek detour's (MusicPlayerContext), not this function's.
 */
export function keepBehindSec({ sizeBytes, durationSec, quotaBytes, aheadSec }) {
  const quota = quotaBytes > 0 ? quotaBytes : ASSUMED_QUOTA_BYTES;
  const bytesPerSec = sizeBytes > 0 && durationSec > 0 ? sizeBytes / durationSec : 0;
  // No idea how fat this track is: the old constant is the safe answer, not an optimistic one.
  if (!(bytesPerSec > 0)) return KEEP_BEHIND_MIN_SEC;
  const spentAhead = Math.max(0, aheadSec || 0) * bytesPerSec;
  const affordableSec = (quota * 0.9 - spentAhead) / bytesPerSec;
  return Math.max(KEEP_BEHIND_MIN_SEC, Math.min(KEEP_BEHIND_MAX_SEC, Math.floor(affordableSec)));
}

// ── The engine flag (music-mse-plan.md §Phase 2) ──────────────────────────────────────────────────

export const ENGINE_KEY = "music.engine";

/**
 * Which player this session gets. THE ENGINE, by default (Phase 5: the gate ran on the phone, the
 * timeline made it fit for daily use, and continuity is the product) — decided ONCE per session,
 * never mid-queue, because switching engines at a boundary is the one thing that would put a script
 * event back where this design removed one.
 *
 * `?mse=0` opts out and is REMEMBERED (the escape hatch has to survive a reload to be one);
 * `?mse=1` forces the engine on and clears an opt-out. Both flippable from a phone with no
 * devtools, same shape as `?diag=1`.
 *
 * `supported` is the caller's capability verdict and it outranks everything: a browser that proves
 * no treatment (no MediaSource at all, or an iPhone whose ManagedMediaSource seam is unbuilt) gets
 * the decks however the default reads — the default is a request, not an assertion (ladder rung 7).
 */
export function chooseEngineMode({ search, storage, supported }) {
  if (!supported) return "decks";
  let choice = null; // null = no explicit word from the listener → the default, which is the engine
  try {
    const q = new URLSearchParams(search || "").get("mse");
    // The choice lands BEFORE the persist, so a private-mode storage that throws still honours the
    // query for this session — it just cannot remember it.
    if (q === "1") { choice = "mse"; storage.setItem(ENGINE_KEY, "mse"); }
    else if (q === "0") { choice = "decks"; storage.setItem(ENGINE_KEY, "decks"); }
    else {
      const saved = storage.getItem(ENGINE_KEY);
      if (saved === "mse" || saved === "decks") choice = saved;
    }
  } catch { /* nothing persists; an unreadable opt-out cannot be honoured */ }
  return choice === "decks" ? "decks" : "mse";
}
