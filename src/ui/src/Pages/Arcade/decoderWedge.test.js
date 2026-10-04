import { describe, it, expect, vi } from "vitest";
import {
  createWedgeDetector, createRecoveryBudget, createFrameGapMeter, createWedgeRecovery,
  WEDGE_MS, WEDGE_GRACE_MS, WEDGE_REFIRE_MS, MAX_VIDEO_RECOVERIES, RECOVERY_BACKOFF_MS, RECOVERY_TIMEOUT_MS,
} from "./decoderWedge";
import { encodeViewerReport, encodeViewerReportV2 } from "./cloudRetroClient";

// One-second getStats samples. `rec`/`dec` are per-second increments; the detector sees cumulative counters.
function run(det, seconds, { rec = 60, dec = 60, hidden = false, live = true, bytes = null } = {}, state) {
  const out = [];
  for (let i = 0; i < seconds; i++) {
    state.t += 1000;
    state.rec += rec;
    state.dec += dec;
    state.bytes += bytes == null ? rec * 2000 : bytes;
    out.push(det.observe({
      t: state.t, framesReceived: state.recUnknown ? undefined : state.rec, framesDecoded: state.dec,
      bytesReceived: state.bytes, hidden, live,
    }));
  }
  return out;
}
const fresh = () => ({ t: 0, rec: 0, dec: 0, bytes: 0 });
const fires = (rows) => rows.filter((r) => r.fire).length;

describe("createWedgeDetector (frames arrive, none decode)", () => {
  it("fires once WEDGE_MS after decoding stops while frames keep arriving (the 2026-10-03 phone)", () => {
    const det = createWedgeDetector();
    const st = fresh();
    expect(fires(run(det, 20, {}, st))).toBe(0); // healthy, past the grace period
    const wedged = run(det, 10, { dec: 0 }, st);
    const first = wedged.findIndex((r) => r.fire);
    expect(first + 1).toBe(WEDGE_MS / 1000); // the 7th stalled second
    expect(wedged[first].stalledMs).toBe(WEDGE_MS);
    expect(fires(wedged)).toBe(1);
  });

  it("re-fires only every WEDGE_REFIRE_MS while the wedge lasts (the page's budget bounds the recoveries)", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 15, {}, st);
    const rows = run(det, WEDGE_MS / 1000 + 2 * (WEDGE_REFIRE_MS / 1000), { dec: 0 }, st);
    expect(fires(rows)).toBe(3);
  });

  it("does not fire for a static screen: frames NOT arriving is not a wedge", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 15, {}, st);
    const rows = run(det, 60, { rec: 0, dec: 0, bytes: 0 }, st);
    expect(fires(rows)).toBe(0);
    expect(rows.every((r) => r.verdict === "idle" && r.stalledMs === 0)).toBe(true);
  });

  it("a decoded frame clears the stall; a pause in arrival neither grows nor clears it", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 15, {}, st);
    run(det, 5, { dec: 0 }, st);                  // 5 s stalled
    run(det, 30, { rec: 0, dec: 0, bytes: 0 }, st); // dedup pause: still 5 s
    expect(det.stalledMs()).toBe(5000);
    const resumed = run(det, 2, { dec: 0 }, st);   // frames arrive again, still none decoded → 7 s
    expect(resumed[1].fire).toBe(true);
    const det2 = createWedgeDetector();
    const st2 = fresh();
    run(det2, 15, {}, st2);
    run(det2, 6, { dec: 0 }, st2);
    run(det2, 1, { dec: 1 }, st2);                 // ONE decoded frame
    expect(fires(run(det2, 6, { dec: 0 }, st2))).toBe(0);
  });

  it("never fires for a hidden tab (a background tab may not decode at all), and hiding clears the stall", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 15, {}, st);
    expect(fires(run(det, 60, { dec: 0, hidden: true }, st))).toBe(0);
    run(det, 5, { dec: 0 }, st);
    run(det, 1, { dec: 0, hidden: true }, st);
    expect(det.stalledMs()).toBe(0);
    const back = run(det, 8, { dec: 0 }, st);
    // The first visible sample after hiding only re-baselines; seven stalled seconds after it, it fires.
    expect(back.findIndex((r) => r.fire)).toBe(7);
  });

  it("never fires while the room is not playing", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 15, {}, st);
    expect(fires(run(det, 30, { dec: 0, live: false }, st))).toBe(0);
  });

  it("never fires during the grace period after the session went live", () => {
    const det = createWedgeDetector();
    const st = fresh();
    run(det, 2, {}, st);
    const rows = run(det, WEDGE_GRACE_MS / 1000 - 3, { dec: 0 }, st);
    expect(fires(rows)).toBe(0);
    expect(rows.every((r) => r.verdict === "grace" || r.verdict === "first")).toBe(true);
    // ...and the stall only starts counting once the grace period is over.
    const after = run(det, WEDGE_MS / 1000 + 2, { dec: 0 }, st);
    expect(after.findIndex((r) => r.fire)).toBeGreaterThanOrEqual(WEDGE_MS / 1000 - 1);
  });

  it("leaves a decoder that never decoded anything to the no-video watchdog (codec, not a mid-room death)", () => {
    const det = createWedgeDetector();
    const st = fresh();
    expect(fires(run(det, 60, { dec: 0 }, st))).toBe(0);
  });

  it("falls back to bytes on a browser without framesReceived, and a trickle is not 'arriving'", () => {
    const det = createWedgeDetector();
    const st = { ...fresh(), recUnknown: true };
    run(det, 15, {}, st);
    expect(fires(run(det, 20, { dec: 0, bytes: 500 }, st))).toBe(0);
    expect(fires(run(det, 8, { dec: 0, bytes: 200000 }, st))).toBe(1);
  });
});

describe("createRecoveryBudget", () => {
  it("allows MAX_VIDEO_RECOVERIES attempts, RECOVERY_BACKOFF_MS apart, then is exhausted", () => {
    const b = createRecoveryBudget();
    expect(b.check(0)).toBe("ok");
    expect(b.begin(0)).toBe(1);
    expect(b.check(RECOVERY_BACKOFF_MS - 1)).toBe("backoff");
    expect(b.check(RECOVERY_BACKOFF_MS)).toBe("ok");
    expect(b.begin(RECOVERY_BACKOFF_MS)).toBe(2);
    expect(MAX_VIDEO_RECOVERIES).toBe(2);
    expect(b.check(10 * RECOVERY_BACKOFF_MS)).toBe("exhausted");
    expect(b.count()).toBe(2);
  });
});

describe("createWedgeRecovery (the bound, one at a time, never a loop)", () => {
  function harness() {
    let t = 0;
    const timers = [];
    const ui = [];
    const attempts = [];
    const rec = createWedgeRecovery({
      now: () => t,
      setTimer: (fn, ms) => { const h = { fn, at: t + ms, live: true }; timers.push(h); return h; },
      clearTimer: (h) => { h.live = false; },
      onUi: (e) => ui.push(e),
      attempt: ({ n, done }) => { const a = { n, done, aborted: 0 }; attempts.push(a); return () => { a.aborted++; }; },
    });
    const advance = (ms) => { t += ms; for (const h of timers) if (h.live && h.at <= t) { h.live = false; h.fn(); } };
    return { rec, ui, attempts, advance, kinds: () => ui.map((e) => e.kind) };
  }

  it("a successful recovery: one attempt, 'recovering' then 'recovered'", () => {
    const h = harness();
    expect(h.rec.onWedge({ codec: "h264" })).toBe("started");
    expect(h.rec.onWedge({})).toBe("busy");
    h.attempts[0].done(true);
    expect(h.kinds()).toEqual(["recovering", "recovered"]);
    expect(h.attempts[0].aborted).toBe(0);
  });

  it("two failures then gives up ONCE, and never starts a third attempt", () => {
    const h = harness();
    h.rec.onWedge({ codec: "h264" });
    h.attempts[0].done(false, "join");
    expect(h.attempts[0].aborted).toBe(1); // the page tears down its half-built replacement
    expect(h.rec.onWedge({})).toBe("backoff");
    h.advance(RECOVERY_BACKOFF_MS);
    expect(h.rec.onWedge({})).toBe("started");
    h.attempts[1].done(false, "disconnected");
    expect(h.kinds()).toEqual(["recovering", "failed", "recovering", "gave-up"]);
    expect(h.ui[3].codec).toBe("h264");
    for (let i = 0; i < 20; i++) { h.advance(RECOVERY_BACKOFF_MS); h.rec.onWedge({}); }
    expect(h.attempts.length).toBe(2);
    expect(h.kinds().filter((k) => k === "gave-up").length).toBe(1);
  });

  it("an attempt that never answers times out and is aborted", () => {
    const h = harness();
    h.rec.onWedge({});
    h.advance(RECOVERY_TIMEOUT_MS);
    expect(h.attempts[0].aborted).toBe(1);
    expect(h.kinds()).toEqual(["recovering", "failed"]);
    h.attempts[0].done(true); // a late answer changes nothing
    expect(h.kinds()).toEqual(["recovering", "failed"]);
  });

  it("recovered twice, wedged a third time: tells the player instead of a third rejoin", () => {
    const h = harness();
    h.rec.onWedge({ codec: "h264" });
    h.attempts[0].done(true);
    h.advance(RECOVERY_BACKOFF_MS);
    h.rec.onWedge({});
    h.attempts[1].done(true);
    h.advance(RECOVERY_BACKOFF_MS);
    expect(h.rec.onWedge({})).toBe("gave-up");
    expect(h.rec.onWedge({})).toBe("done");
    expect(h.attempts.length).toBe(2);
    expect(h.kinds()).toEqual(["recovering", "recovered", "recovering", "recovered", "gave-up"]);
  });

  it("an attempt that throws is a failure, not a stuck 'busy'", () => {
    const rec = createWedgeRecovery({ attempt: () => { throw new Error("x"); }, setTimer: () => 1, clearTimer: vi.fn(), now: () => 0 });
    expect(rec.onWedge({})).toBe("started");
    expect(rec.busy()).toBe(false);
  });
});

describe("createFrameGapMeter (largest gap between PRESENTED frames)", () => {
  it("reports the window's largest gap and count, then opens a new window", () => {
    const m = createFrameGapMeter();
    expect(m.take(0)).toEqual({ gapMs: null, presented: null }); // unknown before any frame / without rVFC
    [0, 16, 33, 150, 166].forEach((t) => m.onFrame(t));
    expect(m.take(170)).toEqual({ gapMs: 117, presented: 5 });
    m.onFrame(183);
    expect(m.take(190)).toEqual({ gapMs: 17, presented: 1 });
  });
  it("a picture frozen RIGHT NOW reads as frozen (the open gap since the last frame counts)", () => {
    const m = createFrameGapMeter();
    m.onFrame(0);
    m.onFrame(16);
    expect(m.take(2016)).toEqual({ gapMs: 2000, presented: 2 });
    expect(m.take(3016)).toEqual({ gapMs: 3000, presented: 0 });
  });
});

// Worker viewer.go "Viewer report v2": the same 13-byte 0xF1 shape under version 2, so a worker that only knows v1
// diverts it to its report path and drops it (a longer message would reach its INPUT dispatch).
describe("encodeViewerReportV2 (viewer report v2 wire format)", () => {
  it("is 13 bytes: tag 0xF1, version 2, hidden flag, four LE u16 and two u8 fields", () => {
    const dv = new DataView(encodeViewerReportV2({ hidden: true, maxGapMs: 117.4, jitterBufferMs: 12.34, presentedFps: 59.8,
      stalledMs: 4200, recoveries: 1, freezes: 3 }));
    expect(dv.byteLength).toBe(13);
    expect(dv.getUint8(0)).toBe(0xf1);
    expect(dv.getUint8(1)).toBe(2);
    expect(dv.getUint8(2)).toBe(1);
    expect(dv.getUint16(3, true)).toBe(117);
    expect(dv.getUint16(5, true)).toBe(123);
    expect(dv.getUint16(7, true)).toBe(598);
    expect(dv.getUint16(9, true)).toBe(4200);
    expect(dv.getUint8(11)).toBe(1);
    expect(dv.getUint8(12)).toBe(3);
  });
  it("absent values are 0xFFFF / 0xFF (no requestVideoFrameCallback, no freezeCount); big values clamp below them", () => {
    const dv = new DataView(encodeViewerReportV2({ hidden: false, maxGapMs: null, jitterBufferMs: undefined, presentedFps: NaN,
      stalledMs: 999999, recoveries: 0, freezes: null }));
    expect(dv.getUint8(2)).toBe(0);
    expect(dv.getUint16(3, true)).toBe(0xffff);
    expect(dv.getUint16(5, true)).toBe(0xffff);
    expect(dv.getUint16(7, true)).toBe(0xffff);
    expect(dv.getUint16(9, true)).toBe(0xfffe);
    expect(dv.getUint8(11)).toBe(0);
    expect(dv.getUint8(12)).toBe(0xff);
  });
  it("keeps v1 exactly as it was: v1 and v2 differ only in the version byte's meaning, never in shape", () => {
    const v1 = new DataView(encodeViewerReport({ hidden: false, displayHz: 60, recvFps: 60, decodedFps: 0, dropped: 0, decodeMs: null }));
    const v2 = new DataView(encodeViewerReportV2({ hidden: false, maxGapMs: 2000, stalledMs: 7000, recoveries: 0 }));
    expect(v1.byteLength).toBe(13);
    expect(v1.getUint8(1)).toBe(1);
    expect(v2.byteLength).toBe(v1.byteLength);
    expect(v2.getUint8(0)).toBe(v1.getUint8(0));
  });
});
