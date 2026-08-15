// Input tape — record exactly what a seat SENDS to the worker, then play it back.
//
// Why a tape and not a keystroke log: the wire frame (mask + 4 axes) is the whole truth of what the
// game received. Recording it means a replay drives the identical bytes down the identical path
// (pumpInput's dedupe / RESYNC / echo-guard all still apply), instead of re-deriving them from
// synthetic key events whose timing and folding rules could drift from the player's real session.
//
// The tape carries three things:
//   inputs   — every SENT pad frame, absolute state, stamped on BOTH clocks (see below)
//   controls — quickSave/quickLoad/reset/fastForward/rewind/swapDisc, so a replay reproduces the
//              save-state manipulation too, not just the buttons
//   trace    — a per-presented-frame perceptual thumbprint of the video. This is the instrument that
//              answers "did my replay actually reproduce it?": two runs are separately H.264-encoded
//              so they can never be pixel- or hash-equal, but their downscaled luma vectors track
//              each other closely while the emulation agrees and separate the moment it diverges.
//
// THE CLOCK. Every event is stamped with both a wall-clock offset and a video mediaTime offset.
// mediaTime is the STREAM's own timeline — it advances with the encoder, i.e. with the emulator's
// output pace — so replaying against it survives a slow tab, a GC pause, or a network hiccup on
// either run. Wall clock is kept only as a fallback for the (brief) window before the video is
// attached, and as a sanity readout. Prefer "media" unless you have a reason not to.
//
// What a tape can and cannot promise: input arrives at the worker some milliseconds after we send
// it, and the worker applies it on its next retro_run — so a replay is accurate to roughly ±1-2
// emulator frames, never bit-exact. For a title whose set pieces are recorded pad-input playback
// (Stuntman's chases) that is enough to reach the same section repeatedly, and NOT enough to assume
// an identical outcome. That is precisely why the trace exists: measure the divergence, don't
// assume it away.

export const TAPE_VERSION = 1;
export const TAPE_KIND = "arcade-input-tape";

// Thumbprint geometry. 16x12 greyscale = 192 bytes/frame (~256 chars base64), so ~4.6 MB of JSON
// for five minutes at 60 fps — small enough to download and diff, coarse enough that H.264 noise
// and chroma differences don't swamp the signal.
export const THUMB_W = 16;
export const THUMB_H = 12;
export const THUMB_LEN = THUMB_W * THUMB_H;

// Default trace budget: ~11 minutes at 60 fps. A tape that hits this stops TRACING (inputs and
// controls keep recording) and says so, rather than growing without bound in a tab the player has
// forgotten about.
export const DEFAULT_MAX_TRACE_FRAMES = 40000;

const B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** Pack a Uint8Array into base64 without touching the DOM (works in node and jsdom tests alike). */
export function bytesToB64(bytes) {
  let out = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const a = bytes[i], b = bytes[i + 1], c = bytes[i + 2];
    const has1 = i + 1 < bytes.length, has2 = i + 2 < bytes.length;
    out += B64[a >> 2];
    out += B64[((a & 3) << 4) | (has1 ? b >> 4 : 0)];
    out += has1 ? B64[((b & 15) << 2) | (has2 ? c >> 6 : 0)] : "=";
    out += has2 ? B64[c & 63] : "=";
  }
  return out;
}

/** Inverse of bytesToB64. */
export function b64ToBytes(s) {
  const clean = String(s || "").replace(/=+$/, "");
  const out = new Uint8Array(Math.floor((clean.length * 3) / 4));
  let acc = 0, bits = 0, o = 0;
  for (let i = 0; i < clean.length; i++) {
    const v = B64.indexOf(clean[i]);
    if (v < 0) continue;
    acc = (acc << 6) | v;
    bits += 6;
    if (bits >= 8) { bits -= 8; out[o++] = (acc >> bits) & 0xff; }
  }
  return out.subarray(0, o);
}

/**
 * Mean absolute difference between two thumbprints, in luma units (0-255). Two frames of the same
 * emulator output through two different encodes land in the low single digits; a genuinely
 * different picture is tens. Returns Infinity for mismatched/absent vectors so a caller can't
 * mistake "couldn't compare" for "identical".
 */
export function thumbDistance(a, b) {
  if (!a || !b || a.length !== b.length || a.length === 0) return Infinity;
  let sum = 0;
  for (let i = 0; i < a.length; i++) sum += Math.abs(a[i] - b[i]);
  return sum / a.length;
}

/**
 * The recorder. Holds the growing tape; knows nothing about the DOM or the transport — the client
 * feeds it, which is what makes it testable.
 *
 * meta is free-form provenance (game, system, room code, video size, build marker, what the tape was
 * anchored to). It is written verbatim into the tape header.
 */
export function createInputTape(meta, opts) {
  const o = opts || {};
  const maxTrace = o.maxTraceFrames || DEFAULT_MAX_TRACE_FRAMES;
  const t0Wall = o.now != null ? o.now : Date.now();
  // The media clock's origin is the FIRST stamped event, not the arm moment: mediaTime is the
  // stream's own timeline and starts wherever the stream happens to be. Captured lazily below.
  let t0Media = null;
  const inputs = [];
  const controls = [];
  const trace = [];
  let traceTruncated = false;

  const media = (mt) => {
    if (mt == null || !Number.isFinite(mt)) return null;
    if (t0Media == null) t0Media = mt;
    return Math.round((mt - t0Media) * 1000);
  };

  return {
    meta: { ...(meta || {}) },
    startedAtWall: t0Wall,
    /**
     * One SENT pad frame. mask + axes are exactly the wire values (post chord-strip, post fold) —
     * record what the game got, not what the hardware read.
     */
    input(mask, axes, clock) {
      const c = clock || {};
      inputs.push([
        media(c.mediaTime), Math.round((c.now != null ? c.now : Date.now()) - t0Wall),
        c.presentedFrames == null ? -1 : c.presentedFrames,
        mask | 0, axes[0] | 0, axes[1] | 0, axes[2] | 0, axes[3] | 0,
      ]);
    },
    /**
     * A non-pad action that changes emulator state: quickSave, quickLoad, reset, fastForward,
     * rewind, swapDisc, or a human "mark" the player dropped to say "here — this is the moment".
     * `arg` is whatever the action needs (engaged flag, disc index, mark label).
     */
    control(action, arg, clock) {
      const c = clock || {};
      controls.push([
        media(c.mediaTime), Math.round((c.now != null ? c.now : Date.now()) - t0Wall),
        String(action), arg === undefined ? null : arg,
      ]);
    },
    /** One presented video frame's thumbprint. Silently stops (flagged) at the budget. */
    frame(mt, pf, thumbBytes) {
      if (trace.length >= maxTrace) { traceTruncated = true; return; }
      trace.push([media(mt), pf == null ? -1 : pf, bytesToB64(thumbBytes)]);
    },
    counts: () => ({ inputs: inputs.length, controls: controls.length, trace: trace.length, traceTruncated }),
    /** Serializable tape. `endedAt*` are written here so a tape always knows its own length. */
    toJSON(extraMeta) {
      const lastWall = Math.max(
        inputs.length ? inputs[inputs.length - 1][1] : 0,
        controls.length ? controls[controls.length - 1][1] : 0,
        trace.length ? trace[trace.length - 1][1] || 0 : 0,
      );
      return {
        v: TAPE_VERSION,
        kind: TAPE_KIND,
        meta: { ...this.meta, ...(extraMeta || {}), durationMs: lastWall, traceTruncated },
        thumb: { w: THUMB_W, h: THUMB_H },
        // Column names are carried IN the tape so a reader (python, jq, a future me) never has to
        // guess what the seventh number in a row means.
        inputCols: ["mt", "wall", "pf", "mask", "ax", "ay", "rx", "ry"],
        controlCols: ["mt", "wall", "action", "arg"],
        traceCols: ["mt", "pf", "thumb"],
        inputs,
        controls,
        trace,
      };
    },
  };
}

/**
 * The player. Turns a tape into "what should the pad be at clock T" plus "which controls have come
 * due since I last asked".
 *
 * clockMode "media" (default) reads the video's mediaTime offset; "wall" reads elapsed real time.
 * The two are stamped independently in the tape, so switching modes needs no re-recording.
 *
 * frameAt is deliberately a SEARCH, not a cursor advance: pumpInput calls it every 16 ms and a
 * dropped tick (or a clock that jumps) must land on the right frame rather than fall behind. The
 * scan starts from the last index, so the common case is O(1).
 */
export function createTapePlayer(tape, opts) {
  const o = opts || {};
  const mode = o.clockMode === "wall" ? "wall" : "media";
  const col = mode === "wall" ? 1 : 0;
  const scale = Number(o.speed) > 0 ? Number(o.speed) : 1;
  // Rows whose chosen clock is null (stamped before the video attached) can't be scheduled on that
  // clock. Drop them rather than guess — and report how many, so a tape recorded before video
  // attach doesn't silently replay short.
  const rows = (tape.inputs || []).filter((r) => r[col] != null);
  const droppedInputs = (tape.inputs || []).length - rows.length;
  const ctrl = (tape.controls || []).filter((r) => r[col] != null);
  const droppedControls = (tape.controls || []).length - ctrl.length;
  const NEUTRAL = [0, 0, 0, 0, 0];
  let idx = -1;
  let ctrlIdx = 0;

  const at = (t) => {
    const tt = t / scale;
    // Forward from the current position while the NEXT row is already due.
    while (idx + 1 < rows.length && rows[idx + 1][col] <= tt) idx++;
    // Backward if the clock moved back (a re-anchored loop iteration reuses the same player).
    while (idx >= 0 && rows[idx][col] > tt) idx--;
    return idx;
  };

  return {
    mode,
    length: rows.length,
    droppedInputs,
    droppedControls,
    durationMs: rows.length ? rows[rows.length - 1][col] * scale : 0,
    /** Absolute pad state at clock T: [mask, ax, ay, rx, ry]. Neutral before the first row. */
    frameAt(t) {
      const i = at(t);
      if (i < 0) return NEUTRAL;
      const r = rows[i];
      return [r[3], r[4], r[5], r[6], r[7]];
    },
    /** Controls that came due at or before T and haven't been handed out yet. */
    dueControls(t) {
      const tt = t / scale;
      const out = [];
      while (ctrlIdx < ctrl.length && ctrl[ctrlIdx][col] <= tt) {
        out.push({ action: ctrl[ctrlIdx][2], arg: ctrl[ctrlIdx][3], at: ctrl[ctrlIdx][col] });
        ctrlIdx++;
      }
      return out;
    },
    /** 0..1 through the tape, for progress logging. */
    progress(t) {
      const d = rows.length ? rows[rows.length - 1][col] : 0;
      return d > 0 ? Math.min(1, t / scale / d) : 1;
    },
    finished(t) { return rows.length === 0 || t / scale > rows[rows.length - 1][col]; },
    /** Restart for another loop iteration against a fresh clock origin. */
    rewind() { idx = -1; ctrlIdx = 0; },
  };
}

/** Decode a tape's trace into { mt, pf, thumb:Uint8Array }[] for diffing. */
export function decodeTrace(tape) {
  return (tape.trace || []).map((r) => ({ mt: r[0], pf: r[1], thumb: b64ToBytes(r[2]) }));
}
