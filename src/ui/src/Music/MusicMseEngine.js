import { MovieAPI } from "../MovieAPI";
import { diagLog } from "./musicDiag";
import {
  ASSUMED_QUOTA_BYTES, appendTreatmentFor, bufferCeilingSec, keepBehindSec, switchReasonFor,
} from "./musicTreatments";
import { isQueueEndStall } from "./musicTimeline";

// ── Phase 2 of music-mse-plan.md: the engine, behind a flag ──────────────────────────────────────
//
// One persistent <audio> whose src is a blob: MediaSource URL, ONE SourceBuffer in "sequence" mode,
// and the whole queue appended into it back to back. The point of the whole design in one sentence:
// **a track boundary stops being a JavaScript event.** Nothing happens in the media pipeline when
// one track ends and the next begins — the bytes are already there, contiguous — so a renderer that
// is frozen because the screen is off cannot stop the album. Index updates are bookkeeping that
// catches up on wake.
//
// Everything here is driven from REFS and events, never from React renders. The engine is a plain
// object with methods; the provider hands it an element and calls pump() at every execution
// opportunity. That split is not stylistic: "no route may require a React render to keep audio
// flowing" is a rule the plan states and the four previous fixes each paid for.
//
// ── The clock rule (§"Route mechanics") ─────────────────────────────────────────────────────────
// `timeupdate` stops being delivered and intervals stop firing on a hidden page, while media and
// SourceBuffer events still fire. So pump() is called from `updateend`, `waiting`, `progress`,
// `playing`, `visibilitychange` AND a timer — and it always does ALL currently-possible work
// ("earliest event", never "latest safe moment"), because the next opportunity is not schedulable.
//
// ── What Phase 2 deliberately does NOT do ───────────────────────────────────────────────────────
// Seek and lyrics. Both need the elementTime ⇄ (trackId, offset) module that Phase 3 owns; wiring
// them to the raw element here would be a second, wrong mapping to unpick later.

/** How far ahead of the playhead to keep the buffer, when the quota allows it. Three minutes: the
 *  phone measured an 84 s execution gap in the run that died, and a window has to outlast the
 *  longest stretch in which the page is not allowed to run — twice over. */
export const TARGET_AHEAD_SEC = 180;
/** What to keep behind the playhead. Everything older is evicted, which is what makes an hours-long
 *  queue cost the same as a short one. */
export const KEEP_BEHIND_SEC = 20;
/** Bytes per append. Small enough that a QuotaExceeded costs one chunk, not a track. */
export const APPEND_CHUNK_BYTES = 512 * 1024;
/** How far the window must DRAIN before it is topped up again.
 *
 *  ⚠ Without hysteresis the buffer sits exactly at its ceiling and every second of playback frees a
 *  second, so every execution opportunity starts another fetch to add it back. Measured: 80 requests
 *  for one hi-res track, each one re-running ffmpeg on the gateway (the fMP4 lane is piped stdout, so
 *  a resumed append cannot Range — it re-fetches from the start and discards what it already has).
 *  Topping up in useful bursts instead costs the same bytes and a fraction of the requests. */
export const MIN_TOPUP_SEC = 30;
/** How much of the queue to hold signed URLs for. Tokens last 6 h and minting is free, so the window
 *  is sized by "how long might this phone be asleep" rather than by cost. */
export const MINT_WINDOW_SEC = 2 * 3600;
/** Mint at most this many at a time (the endpoint's own cap). */
export const MINT_BATCH_MAX = 200;
/** A token is good for 6 h; treat it as spent early enough that one never expires mid-append. */
export const MINT_LIFETIME_MS = 5.5 * 3600 * 1000;

/**
 * How many tracks from `startAt` are inside the pre-mint window.
 *
 * Pure and exported because the window is the thing that keeps a sleeping phone from ever NEEDING a
 * mint — a JS fetch is the least reliable operation on a hidden page — and "how far ahead" is the
 * only number that decides whether it works. Falls back to a track count when durations are unknown,
 * so a queue with no duration metadata still gets a sane window instead of the whole catalog.
 */
export function mintWindowIds(queue, startAt, windowSec = MINT_WINDOW_SEC) {
  const ids = [];
  let seconds = 0;
  for (let i = Math.max(0, startAt); i < (queue || []).length && ids.length < MINT_BATCH_MAX; i++) {
    const track = queue[i];
    if (!track || track.id == null) continue;
    ids.push(track.id);
    seconds += Number(track.durationSec) || 240;
    if (seconds >= windowSec) break;
  }
  return ids;
}

/** Which appended track the playhead is inside, by its buffered-corrected start. Exported and pure:
 *  this is the whole of "queue advance" now, and off-by-one here shows up as the UI naming the wrong
 *  song rather than as anything audible — which is exactly the kind of bug that survives a demo. */
export function trackAtTime(appended, timeSec) {
  let found = null;
  for (const entry of appended || []) {
    if (entry.startSec <= timeSec + 0.05) found = entry;
    else break;
  }
  return found;
}

/** Is this token still worth using? An expired one 401s, and doing that at a boundary on a sleeping
 *  phone is the failure the window exists to prevent — so they are retired early, while awake. */
export function mintIsFresh(entry, nowMs) {
  return !!entry && nowMs - entry.mintedAt < MINT_LIFETIME_MS;
}

/**
 * Creates the engine. Every collaborator is injected so the whole thing can be exercised without a
 * browser that has MediaSource — which the test environment does not, and which is the reason the
 * probe page's rules were extracted into pure functions in the first place.
 */
export function createMseEngine({
  audio,
  api = MovieAPI,
  mediaSourceCtor,
  isTypeSupported,
  quotaBytes = ASSUMED_QUOTA_BYTES,
  onAdvance = () => {},
  onRung = () => {},
  onDeckNeeded = () => {},
  onStreamEnded = () => {},
  onStateChange = () => {},
  now = () => Date.now(),
  isHidden = () => (typeof document !== "undefined" && document.visibilityState === "hidden"),
} = {}) {
  const Ctor = mediaSourceCtor
    || (typeof window !== "undefined" && (window.MediaSource || window.ManagedMediaSource));
  const supports = isTypeSupported || ((mime) => {
    try { return !!(Ctor && Ctor.isTypeSupported && Ctor.isTypeSupported(mime)); } catch { return false; }
  });

  const state = {
    ms: null,
    sb: null,
    mime: null,            // what the SourceBuffer is currently configured for
    sampleRateHz: null,    // …and the shape of the last thing appended, for switchReasonFor
    channels: null,
    queue: [],
    // How far through the queue the APPENDS have got — distinct from which track is playing, which
    // is what the buffer and the playhead say between them.
    appendCursor: 0,
    appended: [],          // [{ trackId, startSec, treatment, bytesAppended, complete }]
    mints: new Map(),      // trackId -> { payload, mintedAt }
    minting: new Set(),
    busy: false,
    destroyed: false,
    endedStream: false,
    deckNeededFor: null,
    quotaRetried: new Set(),
    endedNotified: false,
    currentTrackId: null,
    lastError: null,
  };

  /** The element events that can mean "the buffer ran out at the end of an ended stream". Named
   *  because destroy() has to take the SAME set back off again. */
  const QUEUE_END_EVENTS = ["waiting", "stalled", "pause", "ended"];

  const log = (event, data) => diagLog(`mse:${event}`, data);

  /** A rung of the fallback ladder was used. Logged only — a rung is the ladder WORKING, and it no
   *  longer files an incident of its own. The report that matters is the one at the BOTTOM of the
   *  ladder (`mse:fallback`, when the engine gives up and hands the queue to the decks); a row per
   *  session saying "rung 2 was used and playback continued" is the noise, not the signal. Under
   *  `?diag=1` every rung is still in the ring with its detail. */
  const rung = (n, detail) => {
    log(`rung${n}`, detail);
    onRung(n, detail);
  };

  /** Is the MediaSource still attached to the element?
   *
   *  ⚠ This guard is not theoretical. If anything assigns a plain `src` over the element's blob: URL
   *  the MediaSource detaches, `buffered` empties, and every arithmetic answer here becomes nonsense
   *  — `aheadSec()` goes NEGATIVE, which no ceiling can ever satisfy, so the append loop re-fetches
   *  the same track forever. Measured: ~2000 requests for one 1 MB file before the run was stopped.
   *  Whatever detached it has already taken over playback; the engine's job is to notice and stop. */
  // "closed" is the discriminator, not "open": a stream we ENDED ourselves (endOfStream, because the
  // queue ran out) is still attached and still playing, and treating that as detachment would stop
  // the bookkeeping on the last track of every queue.
  const sourceIsOpen = () => !state.ms || state.ms.readyState !== "closed";

  const bufferedEnd = () => {
    try {
      const sb = state.sb;
      return sb && sb.buffered.length ? sb.buffered.end(sb.buffered.length - 1) : 0;
    } catch {
      return 0;
    }
  };
  const bufferedStart = () => {
    try {
      const sb = state.sb;
      return sb && sb.buffered.length ? sb.buffered.start(0) : 0;
    } catch {
      return 0;
    }
  };
  const aheadSec = () => bufferedEnd() - (audio ? audio.currentTime || 0 : 0);

  const sbOp = (fn) => new Promise((resolve, reject) => {
    const sb = state.sb;
    if (!sb) { resolve(); return; }
    const cleanup = () => {
      sb.removeEventListener("updateend", done);
      sb.removeEventListener("error", failed);
    };
    const done = () => { cleanup(); resolve(); };
    const failed = () => { cleanup(); reject(new Error("SourceBuffer raised error")); };
    sb.addEventListener("updateend", done);
    sb.addEventListener("error", failed);
    try {
      fn(sb);
    } catch (e) {
      cleanup();
      reject(e);
    }
  });

  /**
   * How far back to keep, for the track the playhead is currently inside.
   *
   * Derived per track rather than constant: the ahead window already spends what the quota allows
   * on sleep survival, and whatever is left over is free to spend on being able to scrub backwards.
   * See keepBehindSec — on a compressed lane this is hundreds of seconds, on a fat FLAC it is the
   * 20 s floor because there is genuinely nothing spare.
   */
  const behindWindowSec = () => {
    const t = audio ? audio.currentTime || 0 : 0;
    // The engine's own trackAtTime, which hands back the append record itself — the timeline
    // module's same-named mapping returns a position and is for consumers, not for this.
    const entry = trackAtTime(state.appended, t);
    if (!entry) return KEEP_BEHIND_SEC;
    return keepBehindSec({
      sizeBytes: entry.sizeBytes,
      durationSec: entry.durationSec,
      quotaBytes,
      aheadSec: entry.ceilingSec,
    });
  };

  /**
   * `keepBehindSec: null` means "use the per-track window". A NUMBER is the caller overriding it,
   * which only the QuotaExceeded path does — that one is freeing memory to survive the next append
   * and must be allowed to take back the seek window it is competing with.
   */
  const evictBehind = async (keepSec = null) => {
    const keep = keepSec == null ? behindWindowSec() : keepSec;
    const cutoff = (audio ? audio.currentTime || 0 : 0) - keep;
    if (!(cutoff > 0)) return;
    try {
      await sbOp((sb) => sb.remove(0, cutoff));
    } catch { /* a failed eviction is not fatal: the append that follows may still fit */ }
  };

  // ── The pre-mint window (§"URLs: minted while awake, never needed while asleep") ────────────────
  // Topped up only while VISIBLE. Minting is a JS fetch, which is the first thing a backgrounded
  // page stops being allowed to run — so no route may be allowed to NEED one while asleep. Tokens
  // are stateless and free to sign, which is what makes holding two hours of them reasonable.
  const topUpMints = async () => {
    if (state.destroyed || isHidden()) return;
    const wanted = mintWindowIds(state.queue, state.appendCursor);
    const missing = wanted.filter((id) => !state.minting.has(id) && !mintIsFresh(state.mints.get(id), now()));
    if (missing.length === 0) return;
    missing.forEach((id) => state.minting.add(id));
    try {
      const r = await api.startMusicTracks(missing);
      if (!r.ok) throw new Error(`StartBatch ${r.status}`);
      const body = await r.json();
      const at = now();
      (body.tracks || []).forEach((payload) => {
        state.mints.set(payload.trackId, { payload, mintedAt: at });
      });
      log("minted", { asked: missing.length, got: (body.tracks || []).length, skipped: (body.skipped || []).length });
    } catch (e) {
      // A failed top-up is not a failure of playback: whatever is already buffered keeps playing and
      // the next opportunity tries again. It only becomes fatal if the buffer runs out first.
      log("mint-failed", { why: String(e && e.message ? e.message : e).slice(0, 60) });
    } finally {
      missing.forEach((id) => state.minting.delete(id));
    }
  };

  const payloadFor = (trackId) => {
    const entry = state.mints.get(trackId);
    return mintIsFresh(entry, now()) ? entry.payload : null;
  };

  /**
   * Fetch from `offset` and hand each chunk to `onChunk`.
   *
   * A track bigger than the buffer window cannot be appended in one go, so appends carry a byte
   * cursor and resume later. Range is ASKED for (the File lane and a cached universal encode both
   * honour it) but never relied on: the fMP4 and live universal lanes are piped stdout with
   * `Accept-Ranges: none`, so a 200 answer is handled by discarding the bytes already held.
   */
  const fetchFrom = async (url, offset, onChunk, signal) => {
    const headers = offset > 0 ? { Range: `bytes=${offset}-` } : undefined;
    const res = await fetch(url, { credentials: "omit", headers, signal });
    if (!res.ok && res.status !== 206) throw new Error(`HTTP ${res.status}`);
    let skip = res.status === 206 ? 0 : offset;
    let got = 0;
    if (!res.body) {
      const whole = new Uint8Array(await res.arrayBuffer());
      const slice = skip > 0 ? whole.subarray(skip) : whole;
      if (slice.byteLength) await onChunk(slice);
      return slice.byteLength;
    }
    const reader = res.body.getReader();
    for (;;) {
      // eslint-disable-next-line no-await-in-loop
      const { done, value } = await reader.read();
      if (done) break;
      let chunk = value;
      if (skip > 0) {
        if (chunk.byteLength <= skip) { skip -= chunk.byteLength; continue; }
        chunk = chunk.subarray(skip);
        skip = 0;
      }
      got += chunk.byteLength;
      // eslint-disable-next-line no-await-in-loop
      const keepGoing = await onChunk(chunk);
      if (keepGoing === false) {
        try { await reader.cancel(); } catch { /* already closed */ }
        break;
      }
    }
    return got;
  };

  /** Append one chunk, with the plan's rung 5 on top: a QuotaExceeded evicts what is behind the
   *  playhead and retries ONCE. A second refusal means the buffer genuinely has no room for this
   *  chunk right now, which is not an error — it is the signal to stop appending and wait. */
  const appendChunk = async (bytes) => {
    try {
      await sbOp((sb) => sb.appendBuffer(bytes));
      return true;
    } catch (e) {
      if (e && e.name === "QuotaExceededError") {
        rung(5, { at: Math.round(bufferedEnd()), ahead: Math.round(aheadSec()) });
        await evictBehind(5);
        try {
          await sbOp((sb) => sb.appendBuffer(bytes));
          return true;
        } catch {
          return false;   // full even after eviction: come back at the next opportunity
        }
      }
      state.lastError = String(e && e.message ? e.message : e);
      return false;
    }
  };

  /** Append (or resume appending) the track at the append cursor. Returns true if it did work. */
  const appendNext = async () => {
    const track = state.queue[state.appendCursor];
    if (!track) return false;

    const already = state.appended.find((a) => a.trackId === track.id && !a.complete);
    let payload = payloadFor(track.id);
    if (!payload) {
      if (isHidden()) {
        // Asleep with no URL in hand: park. This is the bounded, logged case the plan describes —
        // heal on wake rather than spend a fetch the page probably cannot make.
        rung(4, { why: "no minted URL while hidden", track: track.id });
        return false;
      }
      await topUpMints();
      payload = payloadFor(track.id);
      if (!payload) return false;
    }

    // ⚠ A partial entry RESUMES on the treatment it started with — sticky, never recomputed. The
    // lane decision exists only for NEW entries, for two reasons the first phone run paid for:
    // (1) sequence-mode bytes must be contiguous, so a track begun on the fMP4 lane that went
    // hidden mid-append would have its universal encode resumed at a byte offset that only means
    // something in the fMP4 stream — corrupt audio or past-EOF either way; (2) re-evaluating on
    // every pump logged "demoted" four times a SECOND for as long as the page stayed hidden,
    // flooding the diag ring and evicting the evidence of whatever actually went wrong.
    let treatment = already ? already.treatment : null;
    let demotedWhy = null;
    if (!already) {
      const decision = appendTreatmentFor({
        payload,
        isTypeSupported: supports,
        hidden: isHidden(),
        quotaBytes,
      });
      if (!decision.treatment) {
        // Nothing in the matrix will carry this track: hand the boundary to the deck path, which is
        // the floor and must be prepared BEFORE the buffer runs out (§the invariant).
        rung(7, { why: "no treatment", track: track.id });
        state.deckNeededFor = track;
        onDeckNeeded(track, payload);
        finishStream();
        return false;
      }
      treatment = decision.treatment;
      if (decision.demoted) demotedWhy = decision.reason;
    }

    // ⚠ How far ahead it is worth buffering WITH this track in it, decided ONCE per append entry and
    // checked before anything is fetched or switched.
    //
    // The pump's target (180 s) is a wish; the quota is the physics, and for a fat track it holds
    // far less. Measured without this: a 38 MB hi-res FLAC whose bytes buy ~29 s was asked for 180 s
    // at every execution opportunity, so every `updateend` started another fetch, appended until
    // QuotaExceeded, evicted and came back — 191 requests for one track. Stable per entry rather
    // than recomputed from the live buffer, because a ceiling that moves with what it measures is
    // not a ceiling: it would clear itself on every pass and spin exactly as before.
    const runwaySec = bufferCeilingSec({
      sizeBytes: payload.sizeBytes,
      durationSec: payload.durationSec,
      quotaBytes,
      targetSec: TARGET_AHEAD_SEC,
    });
    const ceiling = already ? already.ceilingSec : Math.min(TARGET_AHEAD_SEC, aheadSec() + runwaySec);
    // Top up in bursts, not in dribbles: wait until the window has drained by a useful amount. A
    // small ceiling still gets topped up (half of it), so a fat track cannot starve.
    const lowWater = ceiling - Math.min(MIN_TOPUP_SEC, ceiling / 2);
    if (aheadSec() >= lowWater) return false;
    // Logged here — after the "any work to do?" gate — so a demotion is one line per track, at the
    // moment the entry is actually created, not once per execution opportunity.
    if (demotedWhy) log("demoted", { track: track.id, why: demotedWhy });

    // The switch. Container/codec, sample rate OR channel count — the rate switch is the one the
    // phone proved needs it even when the MIME string is identical.
    const reason = switchReasonFor(
      { mime: state.mime, sampleRateHz: state.sampleRateHz, channels: state.channels },
      { mime: treatment.mime, sampleRateHz: payload.sampleRateHz, channels: payload.channels },
    );
    if (state.mime && reason && !already) {
      try {
        state.sb.changeType(treatment.mime);
        log("changeType", { track: track.id, reason });
      } catch (e) {
        // Rung 2: the buffer refused the switch. The universal lane normalises the thing it refused,
        // so try that before giving the track to the decks.
        rung(2, { why: String(e && e.message ? e.message : e).slice(0, 60), track: track.id });
        state.deckNeededFor = track;
        onDeckNeeded(track, payload);
        finishStream();
        return false;
      }
    }
    state.mime = treatment.mime;
    state.sampleRateHz = payload.sampleRateHz;
    state.channels = payload.channels;

    const entry = already || {
      ceilingSec: ceiling,
      trackId: track.id,
      // Carried so the eviction window can be sized from this track's real bitrate (keepBehindSec).
      sizeBytes: Number(payload.sizeBytes) || 0,
      // Carried for the timeline module (Phase 3): the payload's duration is the best answer to
      // "how long is this track", and the mapping falls back to the next entry's start when it is
      // missing.
      durationSec: Number(payload.durationSec) || 0,
      // Buffered-corrected: where this track will actually begin, read off the SourceBuffer rather
      // than trusted from a sum of DB durations that drifts over an hours-long queue.
      startSec: bufferedEnd(),
      treatment,
      bytesAppended: 0,
      complete: false,
    };
    if (!already) state.appended.push(entry);

    let stoppedShort = false;
    const bytesBefore = entry.bytesAppended;
    try {
      const added = await fetchFrom(treatment.url, entry.bytesAppended, async (chunk) => {
        if (state.destroyed) return false;
        const ok = await appendChunk(chunk);
        if (!ok) { stoppedShort = true; return false; }
        entry.bytesAppended += chunk.byteLength;
        // Stop at the ceiling: past it the appends only evict each other, and every byte fetched is
        // a byte of somebody's battery.
        if (aheadSec() >= ceiling) { stoppedShort = true; return false; }
        return true;
      });
      log("appended", {
        track: track.id, lane: treatment.lane, bytes: added,
        ahead: Math.round(aheadSec()), partial: stoppedShort,
      });
    } catch (e) {
      // A failed lane fetch retires the treatment for this track, not the engine: rung 4 (the
      // gateway is behind the site) and CORS both look like this.
      rung(4, { why: String(e && e.message ? e.message : e).slice(0, 60), track: track.id, lane: treatment.lane });
      state.deckNeededFor = track;
      onDeckNeeded(track, payload);
      finishStream();
      return false;
    }

    if (!stoppedShort) {
      entry.complete = true;
      state.appendCursor += 1;
    } else if (entry.bytesAppended === bytesBefore) {
      // The append made no progress at all. Re-running the same fetch would make none either, and
      // the loop that called us would do it immediately — so stop, and let the next execution
      // opportunity (by which time the buffer may have drained) try again.
      log("no-progress", { track: track.id, ahead: Math.round(aheadSec()) });
      return false;
    }
    return true;
  };

  /**
   * What the timeline module needs, as a stable accessor.
   *
   * Deliberately NOT inspect(): that one is a diagnostic snapshot which may grow or change shape,
   * and the play bar reads this several times a second. Cheap, and the entries are copies so no
   * consumer can reach into the engine's own records.
   */
  const timeline = () => ({
    entries: state.appended.map((a) => ({
      trackId: a.trackId, startSec: a.startSec, durationSec: a.durationSec, complete: a.complete,
    })),
    currentTime: audio ? audio.currentTime || 0 : 0,
    bufferedStart: bufferedStart(),
    bufferedEnd: bufferedEnd(),
    endedStream: state.endedStream,
  });

  /**
   * A stall on a stream we already ended, at the very end of the buffer, IS the end.
   *
   * Measured on the phone: after endOfStream() the element drained its last 161 s and then fired
   * `waiting` rather than `ended`, and everything waiting on that `ended` waited forever. Fires once.
   */
  const checkQueueEndStall = () => {
    // A destroyed engine has no say in the session any more. It matters because destroy() PAUSES the
    // element (see below) and `pause` is one of this guard's own triggers — an engine that has just
    // been superseded would otherwise announce the end of a queue it no longer owns, and the
    // boundary handler would advance the track under whoever took over.
    if (state.destroyed) return false;
    if (state.endedNotified) return false;
    if (!isQueueEndStall({
      endedStream: state.endedStream,
      currentTime: audio ? audio.currentTime || 0 : 0,
      bufferedEnd: bufferedEnd(),
    })) return false;
    state.endedNotified = true;
    log("queue-end-stall", {
      at: Math.round(audio ? audio.currentTime || 0 : 0),
      bufferedEnd: Math.round(bufferedEnd()),
      readyState: audio ? audio.readyState : null,
    });
    onStreamEnded();
    return true;
  };

  /** End the stream so the element fires a REAL `ended` at the exact end of the audio — which is
   *  what lets the standard deck-flip machinery take over without the buffer ever running dry
   *  (§"Cross-engine joins, asleep"). */
  const finishStream = () => {
    if (state.endedStream || !state.ms) return;
    try {
      if (state.ms.readyState === "open") {
        state.ms.endOfStream();
        state.endedStream = true;
        log("endOfStream", { ahead: Math.round(aheadSec()) });
      }
    } catch { /* already ended or torn down */ }
  };

  /** Queue-advance bookkeeping: which track is the playhead inside? Reads the buffer, never React. */
  const syncIndex = () => {
    const entry = trackAtTime(state.appended, audio ? audio.currentTime || 0 : 0);
    if (!entry || entry.trackId === state.currentTrackId) return;
    state.currentTrackId = entry.trackId;
    log("advance", { track: entry.trackId, at: Math.round(audio.currentTime || 0) });
    onAdvance(entry.trackId);
  };

  /**
   * An execution opportunity. Does ALL currently-possible work, because the next one is not
   * schedulable — and never throws, because every caller is an event handler on a page that may be
   * frozen a millisecond later.
   */
  const pump = async () => {
    if (state.destroyed || state.busy || !state.sb) return;
    if (!sourceIsOpen()) {
      log("detached", { readyState: state.ms && state.ms.readyState });
      state.destroyed = true;
      return;
    }
    state.busy = true;
    try {
      syncIndex();
      checkQueueEndStall();
      if (!isHidden()) await topUpMints();
      // Append until the window is full or there is nothing left to append. Bounded by the queue,
      // and each iteration either advances the cursor or stops.
      for (let guard = 0; guard < 8; guard++) {
        if (state.destroyed || state.deckNeededFor) break;
        if (aheadSec() >= TARGET_AHEAD_SEC && state.appended.some((a) => a.complete)) break;
        if (state.appendCursor >= state.queue.length) { finishStream(); break; }
        // eslint-disable-next-line no-await-in-loop
        const did = await appendNext();
        if (!did) break;
      }
      await evictBehind();
      syncIndex();
    } catch (e) {
      log("pump-failed", { why: String(e && e.message ? e.message : e).slice(0, 80) });
    } finally {
      state.busy = false;
      onStateChange();
    }
  };

  /** Build the MediaSource and append from `index`. Resolves once there is audio to play. */
  const start = async ({ queue, index = 0 }) => {
    state.queue = queue || [];
    state.appendCursor = Math.max(0, index);
    state.appended = [];
    state.endedStream = false;
    state.deckNeededFor = null;
    state.mime = null;
    if (!Ctor || !audio) throw new Error("no MediaSource");

    const ms = new Ctor();
    state.ms = ms;
    const url = URL.createObjectURL(ms);
    audio.src = url;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("MediaSource never opened")), 10000);
      ms.addEventListener("sourceopen", () => { clearTimeout(timer); URL.revokeObjectURL(url); resolve(); }, { once: true });
    });
    if (state.destroyed) return null;

    await topUpMints();
    // Re-check after EVERY await, not just sourceopen: a newer start() destroys this engine and
    // assigns its own src, which detaches this MediaSource — addSourceBuffer on it would throw and
    // read as an engine failure when it is only supersession (incident 5, 2026-08-12).
    if (state.destroyed) return null;
    const first = state.queue[state.appendCursor];
    const payload = first ? payloadFor(first.id) : null;
    const decision = payload
      ? appendTreatmentFor({ payload, isTypeSupported: supports, hidden: isHidden(), quotaBytes })
      : { treatment: null };
    if (!decision.treatment) {
      rung(7, { why: "no treatment for the first track" });
      throw new Error("no treatment for the first track");
    }
    const sb = ms.addSourceBuffer(decision.treatment.mime);
    // "sequence" so appended tracks land back-to-back without computing timestamps — the mechanism
    // the whole mixed-queue requirement rests on.
    sb.mode = "sequence";
    state.sb = sb;
    state.mime = decision.treatment.mime;
    state.sampleRateHz = payload.sampleRateHz;
    state.channels = payload.channels;

    sb.addEventListener("updateend", () => { pump(); });
    // The guard's triggers: the events a drained element actually fires, plus the pump, because a
    // hidden page may get no event at all and the queue end must still be noticed on the next
    // opportunity the page is given.
    QUEUE_END_EVENTS.forEach((name) => audio.addEventListener(name, checkQueueEndStall));
    await pump();
    if (state.destroyed) return null;
    log("started", { track: first?.id ?? null, lane: decision.treatment.lane, ahead: Math.round(aheadSec()) });
    return state.appended[0] || null;
  };

  const setQueue = (queue, index) => {
    state.queue = queue || [];
    if (Number.isInteger(index) && index >= 0) state.appendCursor = Math.max(state.appendCursor, index);
  };

  /**
   * Stop being the thing that is playing.
   *
   * ⚠ `endOfStream()` is NOT a stop — it is a promise that no more data is coming, and the element
   * then plays out everything already in the SourceBuffer. That is up to a whole quota of audio
   * (11.5 MB ≈ 95 s of a 950 kbps FLAC), and it kept playing after every caller that hands the
   * session to a deck: seekDetour, fallBackToDecks, the unmount. The listener heard the song on top
   * of itself, and could not stop the second copy — once `deckRef` says "a", NOTHING in the player
   * can reach this element (pause and Clear queue touch the ACTIVE deck, cancelPreroll the IDLE
   * one, and the deck handlers ignore an element that isn't live). Reported 2026-08-13: wake, scrub,
   * pause, and one of the two copies played on.
   *
   * This is the mirror of parkDecks(): the engine already silences the decks it takes over FROM, and
   * this is the same courtesy in the other direction. Pause only — the `src` is deliberately left
   * alone, because assigning over a MediaSource blob URL detaches it and is its own documented trap;
   * the next start() replaces it wholesale.
   *
   * The listeners come off FIRST: `pause` is one of checkQueueEndStall's triggers, so silencing the
   * element must not be mistaken for the queue running out.
   */
  const destroy = () => {
    state.destroyed = true;
    if (audio) QUEUE_END_EVENTS.forEach((name) => audio.removeEventListener(name, checkQueueEndStall));
    try { if (state.ms && state.ms.readyState === "open") state.ms.endOfStream(); } catch { /* fine */ }
    state.sb = null;
    state.ms = null;
    try { if (audio) audio.pause(); } catch { /* the element is being replaced anyway */ }
  };

  return {
    start,
    pump,
    setQueue,
    destroy,
    finishStream,
    evictBehind,
    timeline,
    element: audio,
    /** Diagnostics + tests. Never used to drive playback — the engine reads its own refs. */
    inspect: () => ({
      appendCursor: state.appendCursor,
      appended: state.appended.map((a) => ({ ...a })),
      mints: Array.from(state.mints.keys()),
      currentTrackId: state.currentTrackId,
      deckNeededFor: state.deckNeededFor,
      endedStream: state.endedStream,
      aheadSec: aheadSec(),
      mime: state.mime,
      lastError: state.lastError,
    }),
  };
}
