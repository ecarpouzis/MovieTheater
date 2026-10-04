// ── Decoder-wedge watchdog (arcade estimate-collapse plan, Stage 4) ──────────────────────────────────────
// 2026-10-03, Eric's Android phone, Firefox, an H.264 room: the browser's video decoder stopped dead. Frames kept
// ARRIVING at 60 fps, framesDecoded stopped advancing, the browser sent no PLI and no NACK, and ~ten later keyframes
// (a safety IDR every 10 s, then a full pipeline rebuild) did not revive it. The picture stayed frozen for two
// minutes while audio (its own PeerConnection) played on. The server cannot fix that — every keyframe it can send
// went into the dead decoder — so the page has to notice and rebuild its own receiving end.
//
// The rule (createWedgeDetector), fed by the same once-a-second getStats sample as the viewer report:
//   - STALLED second = frames kept arriving (framesReceived advanced by >= MIN_ARRIVING_FPS per second; bytes
//     where the browser has no framesReceived) and framesDecoded did NOT advance.
//   - A second in which frames did not arrive at all (a static screen the worker dedups, a network gap) is NOT
//     evidence: it neither grows the stall nor clears it. Any decoded frame clears it.
//   - Hidden tab, or the room not in its playing state, clears it (a background tab may not decode at all).
//   - Nothing fires in the first WEDGE_GRACE_MS after the session went live, nor for a decoder that never decoded a
//     single frame (that is the no-video watchdog's case: a codec this browser cannot play, not a death mid-room).
//   - It FIRES once the stall reaches WEDGE_MS, then again every WEDGE_REFIRE_MS while it lasts. Why 7 s: the
//     server's adaptive controller (patch 0052) calls a viewer at decoded = 0 % WEDGED after 2 one-second ticks and
//     forces a keyframe every 3 s (up to 5). A legitimate keyframe wait after loss is ~0.5 s (PLI limiter). 7 s
//     lets the server's first two forced keyframes land and be decoded before the page tears anything down.
// The page decides what to do with a fire (createRecoveryBudget bounds it: at most MAX_VIDEO_RECOVERIES per room,
// RECOVERY_BACKOFF_MS apart; then the player is told plainly instead of the page spinning).

export const WEDGE_MS = 7000;
export const WEDGE_GRACE_MS = 10000;
export const WEDGE_REFIRE_MS = 10000;
export const MIN_ARRIVING_FPS = 2;
// Bytes-only browsers: a keyframe-less trickle of RTCP-sized packets must not read as "frames arriving".
export const MIN_ARRIVING_BYTES_PER_S = 4000;
export const MAX_VIDEO_RECOVERIES = 2;
export const RECOVERY_BACKOFF_MS = 30000;
// How long a recovery may take (fresh join descriptor → new connection → first presented frame) before it is
// counted as failed. A healthy rejoin presents in ~2-4 s (ttff), a JIT room's never re-stages its ROM.
export const RECOVERY_TIMEOUT_MS = 20000;

const num = (v) => (typeof v === "number" && Number.isFinite(v) ? v : null);

/**
 * One detector per session. observe() takes one sample per getStats read:
 *   { t (ms), framesReceived, framesDecoded, bytesReceived, hidden, live }
 * and returns { verdict, stalledMs, fire }. verdict is one of
 *   "not-live" | "hidden" | "first" | "grace" | "ok" | "idle" | "stalling" | "wedged".
 */
export function createWedgeDetector({ wedgeMs = WEDGE_MS, graceMs = WEDGE_GRACE_MS, refireMs = WEDGE_REFIRE_MS } = {}) {
  let prev = null;
  let liveSince = null;
  let stalledMs = 0;
  let everDecoded = false;
  let lastFireAt = null;
  const out = (verdict, fire = false) => ({ verdict, stalledMs, fire });

  return {
    observe(s) {
      const t = num(s && s.t);
      if (t == null) return out("first");
      const cur = { t, rec: num(s.framesReceived), dec: num(s.framesDecoded), bytes: num(s.bytesReceived) };
      if (cur.dec != null && cur.dec > 0) everDecoded = true;
      const p = prev;
      prev = cur;
      // Hidden / not playing: clear the stall, and make the next sample only re-baseline (the interval that
      // straddles the change says nothing about a visible, playing decoder).
      if (!s.live) { liveSince = null; stalledMs = 0; prev = null; return out("not-live"); }
      if (liveSince == null) liveSince = t;
      if (s.hidden) { stalledMs = 0; prev = null; return out("hidden"); }
      if (!p) return out("first");
      const dt = t - p.t;
      if (!(dt > 0)) return out("first");
      if (t - liveSince < graceMs) { stalledMs = 0; return out("grace"); }

      const decoded = cur.dec != null && p.dec != null && cur.dec > p.dec;
      if (decoded) { stalledMs = 0; return out("ok"); }
      let arriving;
      if (cur.rec != null && p.rec != null) arriving = cur.rec - p.rec >= (MIN_ARRIVING_FPS * dt) / 1000;
      else if (cur.bytes != null && p.bytes != null) arriving = cur.bytes - p.bytes >= (MIN_ARRIVING_BYTES_PER_S * dt) / 1000;
      else arriving = false;
      if (!arriving) return out("idle");

      stalledMs += dt;
      if (stalledMs < wedgeMs || !everDecoded) return out("stalling");
      const fire = lastFireAt == null || t - lastFireAt >= refireMs;
      if (fire) lastFireAt = t;
      return out("wedged", fire);
    },
    stalledMs: () => stalledMs,
  };
}

/**
 * The per-ROOM bound on recoveries (it outlives the sessions a recovery replaces).
 * check(t) → "ok" | "backoff" | "exhausted"; begin(t) records an attempt and returns its number (1-based).
 */
export function createRecoveryBudget({ max = MAX_VIDEO_RECOVERIES, backoffMs = RECOVERY_BACKOFF_MS } = {}) {
  const attempts = [];
  return {
    check(t) {
      if (attempts.length >= max) return "exhausted";
      if (attempts.length && t - attempts[attempts.length - 1] < backoffMs) return "backoff";
      return "ok";
    },
    begin(t) { attempts.push(t); return attempts.length; },
    count: () => attempts.length,
  };
}

/**
 * Largest gap between PRESENTED frames, per report window. onFrame(ms) per requestVideoFrameCallback; take(ms)
 * returns the window's largest gap — INCLUDING the still-open gap since the last presented frame, so a picture
 * frozen right now reads as frozen — plus the presented count, and opens the next window. Before any frame (or
 * on a browser without rVFC, where onFrame never runs) take() answers { gapMs: null, presented: null } = unknown.
 */
export function createFrameGapMeter() {
  let last = null;
  let maxGap = 0;
  let frames = 0;
  return {
    onFrame(ms) {
      if (last != null && ms > last) maxGap = Math.max(maxGap, ms - last);
      last = ms;
      frames++;
    },
    take(ms) {
      if (last == null) return { gapMs: null, presented: null };
      const gapMs = Math.max(maxGap, ms > last ? ms - last : 0);
      const presented = frames;
      maxGap = 0;
      frames = 0;
      return { gapMs, presented };
    },
    reset() { last = null; maxGap = 0; frames = 0; },
  };
}

/**
 * The per-room recovery controller: turns watchdog fires into at most `budget` recoveries, one at a time, and tells
 * the page what to show. It owns the bound and the timeout; the page owns the mechanism.
 *
 *   attempt({ n, done }) — start recovery number n; call done(ok, why) when it succeeds or fails (asynchronously),
 *                          and return an abort function the controller calls if it gives up on the attempt first
 *                          (timeout) or after a failure (so the page can tear down a half-built replacement).
 *   onUi(event)          — { kind: "recovering", n } | { kind: "recovered", n } | { kind: "failed", n, why }
 *                          | { kind: "gave-up", why, codec }. "gave-up" is emitted ONCE per room.
 *
 * onWedge(info) answers what it did: "started" | "busy" | "backoff" | "gave-up" | "done" (already gave up).
 */
export function createWedgeRecovery({ budget = createRecoveryBudget(), attempt, onUi, now = () => Date.now(),
  timeoutMs = RECOVERY_TIMEOUT_MS, setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  let busy = false;
  let gaveUp = false;
  let lastCodec = "";
  const ui = (e) => { try { onUi && onUi(e); } catch { /* observer */ } };
  const giveUp = (why) => {
    if (gaveUp) return;
    gaveUp = true;
    ui({ kind: "gave-up", why, codec: lastCodec });
  };
  return {
    onWedge(info) {
      if (info && info.codec) lastCodec = info.codec;
      if (gaveUp) return "done";
      if (busy) return "busy";
      const c = budget.check(now());
      if (c === "exhausted") { giveUp("still-wedged"); return "gave-up"; }
      if (c === "backoff") return "backoff";
      busy = true;
      const n = budget.begin(now());
      ui({ kind: "recovering", n });
      let settled = false;
      let abort = null;
      let timer = null;
      const finish = (ok, why) => {
        if (settled) return;
        settled = true;
        if (timer != null) clearTimer(timer);
        busy = false;
        if (!ok) { try { abort && abort(); } catch { /* page teardown */ } }
        if (ok) ui({ kind: "recovered", n });
        else if (budget.check(now()) === "exhausted") giveUp(why || "failed");
        else ui({ kind: "failed", n, why: why || "failed" });
      };
      timer = setTimer(() => finish(false, "timeout"), timeoutMs);
      try { abort = attempt({ n, done: finish }); } catch { finish(false, "threw"); }
      return "started";
    },
    busy: () => busy,
    gaveUp: () => gaveUp,
    count: () => budget.count(),
  };
}
