import { renderHook, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { useAdaptiveBitrate } from "./useAdaptiveBitrate";
import { ABR_PROFILES, DIRECT_BPS } from "./streamAbr";

// The stall debounce needs a real clock to reason about: two DISTINCT episodes at least 6s apart
// inside a 30s window, and 20s since the last switch.
const EPISODE_GAP_MS = 6_000;

function setup({ quality = "auto", copied = false, sourceVideoBps = null } = {}) {
  const onAdapt = vi.fn();
  const qualityKeyRef = { current: quality };
  const videoCopiedRef = { current: copied };
  const sourceVideoBpsRef = { current: sourceVideoBps };
  const hook = renderHook(() =>
    useAdaptiveBitrate({ qualityKeyRef, profile: ABR_PROFILES.auto, onAdapt, videoCopiedRef, sourceVideoBpsRef })
  );
  return { hook, onAdapt, qualityKeyRef, videoCopiedRef, sourceVideoBpsRef };
}

// Two stall episodes spaced past the collapse window — the minimum that means "this rung is too high".
function stallTwice(hook) {
  act(() => hook.result.current.handleStall());
  vi.setSystemTime(Date.now() + EPISODE_GAP_MS + 1_000);
  act(() => hook.result.current.handleStall());
}

describe("useAdaptiveBitrate", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-08-12T12:00:00Z"));
  });
  afterEach(() => vi.useRealTimers());

  it("ignores a lone stall — one hiccup must not cost a reload", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.handleStall());
    expect(onAdapt).not.toHaveBeenCalled();
  });

  it("drops a rung after two stall episodes", () => {
    const { hook, onAdapt } = setup();
    stallTwice(hook);
    // No estimate, no source bitrate: the blind one-rung walk. The rung under DIRECT is 30 Mbps since
    // the 2026-09-02 ladder (was 12) — a denser ladder makes the blind walk finer, not different.
    expect(onAdapt).toHaveBeenCalledWith(30_000_000);
    expect(hook.result.current.autoBpsRef.current).toBe(30_000_000);
  });

  // The restored fall-back: the video being COPIED is not a reason to keep stalling on it. This is
  // the common case (any file the browser can decode direct-streams at the top rung), and it is what
  // the always-true isDirectStream flag used to freeze on the Watch page.
  it("still drops off a COPIED stream, skipping the rungs that would re-deliver the same copy", () => {
    const { hook, onAdapt } = setup({ copied: true, sourceVideoBps: 5_760_524 });
    stallTwice(hook);
    expect(onAdapt).toHaveBeenCalledWith(4_000_000);
  });

  // The other half of the copy rule: a stall on a copied stream whose link MEASURABLY carries the
  // source with headroom is not the wire's fault, and every rung below the source is a re-encode.
  // Ballerina 2026-09-03: 24.8 Mbps 4K HEVC copy, estimate 1.36 Gbps, dropped to a 20 Mbps encode.
  it("keeps a COPIED stream when the fresh estimate clears the source with headroom", () => {
    const { hook, onAdapt } = setup({ copied: true, sourceVideoBps: 24_787_016 });
    act(() => hook.result.current.handleBandwidth(1_362_770_115));
    stallTwice(hook);
    expect(onAdapt).not.toHaveBeenCalled();
    expect(hook.result.current.autoBpsRef.current).toBe(DIRECT_BPS);
  });

  it("still drops a COPIED stream when the fresh estimate is BELOW the source — a thin link is real", () => {
    const { hook, onAdapt } = setup({ copied: true, sourceVideoBps: 24_787_016 });
    act(() => hook.result.current.handleBandwidth(15_000_000));
    stallTwice(hook);
    expect(onAdapt).toHaveBeenCalledWith(8_000_000);
  });

  it("does not climb while the video is copied — every rung above it is the same bytes", () => {
    const { hook, onAdapt } = setup({ copied: true });
    act(() => hook.result.current.handleBandwidth(500_000_000));
    expect(onAdapt).not.toHaveBeenCalled();
  });

  it("never adapts on a fixed rung, stalls or not", () => {
    const { hook, onAdapt } = setup({ quality: "original" });
    stallTwice(hook);
    act(() => hook.result.current.handleBandwidth(500_000_000));
    expect(onAdapt).not.toHaveBeenCalled();
  });

  it("holds at the bottom rung", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.reseed(1_500_000));
    stallTwice(hook);
    expect(onAdapt).not.toHaveBeenCalled();
    expect(hook.result.current.autoBpsRef.current).toBe(1_500_000);
  });

  it("opens at the profile's cap", () => {
    const { hook } = setup();
    expect(hook.result.current.autoBpsRef.current).toBe(DIRECT_BPS);
  });

  // ── the informed drop ──────────────────────────────────────────────────────
  it("drops straight to the rung a fresh estimate supports — one restart, not a cascade", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.handleBandwidth(13_000_000)); // the link is delivering ~13 Mbps
    stallTwice(hook);
    expect(onAdapt).toHaveBeenCalledWith(8_000_000); // 12 Mbps would still sit above the link
  });

  it("ignores a stale estimate and falls back to the one-rung walk", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.handleBandwidth(13_000_000));
    vi.setSystemTime(Date.now() + 20_000); // estimate is 20s old by the first stall — says nothing now
    stallTwice(hook);
    expect(onAdapt).toHaveBeenCalledWith(30_000_000); // one rung under DIRECT on the 2026-09-02 ladder
  });

  // ── post-switch grace ──────────────────────────────────────────────────────
  it("does not count the restart's own rebuffer toward the next drop", () => {
    const { hook, onAdapt } = setup();
    stallTwice(hook); // first drop; the switch just restarted the session
    expect(onAdapt).toHaveBeenCalledTimes(1);
    vi.setSystemTime(Date.now() + 8_000); // 8s after the switch: the restart's own rebuffer
    act(() => hook.result.current.handleStall()); // grace-swallowed — must NOT become episode #1
    vi.setSystemTime(Date.now() + 13_000);
    act(() => hook.result.current.handleStall()); // a real stall — episode #1 only
    expect(onAdapt).toHaveBeenCalledTimes(1); // a second drop here would be the old cascade
    vi.setSystemTime(Date.now() + 7_000);
    act(() => hook.result.current.handleStall()); // episode #2 confirms the pattern
    expect(onAdapt).toHaveBeenCalledTimes(2);
  });

  // ── climb streak hysteresis ────────────────────────────────────────────────
  // Feed a 5s-cadence sample stream, as the players do.
  function feedFor(hook, seconds, bpsAt) {
    for (let t = 5; t <= seconds; t += 5) {
      vi.setSystemTime(Date.now() + 5_000);
      act(() => hook.result.current.handleBandwidth(bpsAt(t)));
    }
  }

  it("holds the climb streak through a dead-zone dip (between the hold bar and the climb bar)", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.reseed(8_000_000));
    // 20 Mbps with one 16 Mbps dip: 16 is short of the 18 climb bar but above the 13.8 hold bar —
    // under the old every-sample reset this dip restarted the 90s clock (the 29-minute starvation).
    feedFor(hook, 90, (t) => (t === 45 ? 16_000_000 : 20_000_000));
    expect(onAdapt).toHaveBeenCalledWith(DIRECT_BPS); // climbed on schedule despite the dip
  });

  it("resets the climb streak on a genuinely weak sample", () => {
    const { hook, onAdapt } = setup();
    act(() => hook.result.current.reseed(8_000_000));
    feedFor(hook, 90, (t) => (t === 45 ? 12_000_000 : 20_000_000)); // 12 < the 13.8 hold bar
    expect(onAdapt).not.toHaveBeenCalled(); // clock restarted at the dip…
    feedFor(hook, 45, () => 20_000_000);
    expect(onAdapt).toHaveBeenCalledWith(DIRECT_BPS); // …and completed 90s after it
  });

  // ── source-gated lossless tier ─────────────────────────────────────────────
  it("will not climb into a lossless tier the link can't carry", () => {
    const { hook, onAdapt } = setup({ sourceVideoBps: 21_600_000 }); // the Black Dynamite remux
    act(() => hook.result.current.reseed(8_000_000));
    feedFor(hook, 95, () => 25_000_000); // clears the 12 rung (18) but not the source ×1.5 (32.4)
    expect(onAdapt).toHaveBeenCalledWith(12_000_000);
    expect(onAdapt).not.toHaveBeenCalledWith(DIRECT_BPS);
  });

  // ── demotion memory ────────────────────────────────────────────────────────
  it("doubles the required clean streak after a stall-driven drop; reseed forgives it", () => {
    const { hook, onAdapt } = setup();
    stallTwice(hook); // DIRECT → 12 Mbps; the link has now knocked us down once
    onAdapt.mockClear();
    feedFor(hook, 90, () => 200_000_000);
    expect(onAdapt).not.toHaveBeenCalled(); // 90s used to be enough — demotion doubled it
    feedFor(hook, 90, () => 200_000_000);
    expect(onAdapt).toHaveBeenCalledWith(DIRECT_BPS); // 180s of clean link re-earns the climb
    // A manual re-select of Auto forgives the demotion: back to the base 90s.
    act(() => hook.result.current.reseed(12_000_000));
    onAdapt.mockClear();
    vi.setSystemTime(Date.now() + 95_000);
    act(() => hook.result.current.handleBandwidth(200_000_000));
    expect(onAdapt).toHaveBeenCalledWith(DIRECT_BPS);
  });
});
