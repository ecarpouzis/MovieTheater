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
    expect(onAdapt).toHaveBeenCalledWith(12_000_000);
    expect(hook.result.current.autoBpsRef.current).toBe(12_000_000);
  });

  // The restored fall-back: the video being COPIED is not a reason to keep stalling on it. This is
  // the common case (any file the browser can decode direct-streams at the top rung), and it is what
  // the always-true isDirectStream flag used to freeze on the Watch page.
  it("still drops off a COPIED stream, skipping the rungs that would re-deliver the same copy", () => {
    const { hook, onAdapt } = setup({ copied: true, sourceVideoBps: 5_760_524 });
    stallTwice(hook);
    expect(onAdapt).toHaveBeenCalledWith(4_000_000);
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
});
