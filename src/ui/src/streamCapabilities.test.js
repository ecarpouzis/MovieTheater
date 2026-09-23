import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import {
  displayWidthCap, learnedWidthCeiling, noteDecodeFailure, forgetLearnedCeiling, effectiveWidthCap, DECODE_WIDTH_KEY,
} from "./streamCapabilities";

// The frame-width cap the Auto modes send: a phone or tablet's own screen width rounded up to a
// standard encode width, nothing on a desktop or a 4K panel, and the 1080p tier a decode failure
// teaches a browser to stay under — for 30 days, and only for a frame that was wider than that.

function setScreen(width, height, dpr = 1, touchOnly = true) {
  vi.stubGlobal("screen", { width, height });
  vi.stubGlobal("devicePixelRatio", dpr);
  vi.stubGlobal("matchMedia", (q) => ({ matches: touchOnly && q === "(hover: none) and (pointer: coarse)" }));
}

describe("displayWidthCap", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("rounds a phone or tablet's physical width UP to a standard encode width", () => {
    setScreen(1280, 800, 2); // Galaxy Tab S4: 2560×1600 physical
    expect(displayWidthCap()).toBe(2560);
    setScreen(412, 915, 2.625); // a 1080×2400 phone
    expect(displayWidthCap()).toBe(2560);
    setScreen(800, 1280, 1.5); // a 1200×1920 tablet
    expect(displayWidthCap()).toBe(1920);
  });

  it("uses the larger dimension, because phones and tablets rotate", () => {
    setScreen(800, 1280, 2);
    expect(displayWidthCap()).toBe(2560);
  });

  it("sends no cap for a desktop or laptop — their decoders were never at risk and a 1080p monitor keeps its 4K copy", () => {
    setScreen(1920, 1080, 1, false);
    expect(displayWidthCap()).toBeNull();
    setScreen(1366, 768, 1, false);
    expect(displayWidthCap()).toBeNull();
  });

  it("sends no cap for a 4K-class panel or an unknown screen", () => {
    setScreen(3840, 2160, 1);
    expect(displayWidthCap()).toBeNull();
    setScreen(0, 0, 1);
    expect(displayWidthCap()).toBeNull();
  });
});

describe("the learned decode ceiling", () => {
  beforeEach(() => { window.localStorage.clear(); forgetLearnedCeiling(); });
  afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

  it("is absent until a decode failure on a wide frame, then remembered per browser at the 1080p tier", () => {
    setScreen(1280, 800, 2);
    expect(learnedWidthCeiling()).toBeNull();
    expect(effectiveWidthCap()).toBe(2560);
    expect(noteDecodeFailure({ width: 3840 })).toBe(true);
    expect(JSON.parse(window.localStorage.getItem(DECODE_WIDTH_KEY)).width).toBe(1920);
    expect(effectiveWidthCap()).toBe(1920);
  });

  it("learns nothing from a failure on a frame already at or under the safe tier", () => {
    setScreen(1920, 1080, 1, false);
    expect(noteDecodeFailure({ width: 1920 })).toBe(false);
    expect(noteDecodeFailure({ width: 1280 })).toBe(false);
    expect(learnedWidthCeiling()).toBeNull();
  });

  it("refuses to learn twice — a second failure under the safe tier is not a resolution problem", () => {
    setScreen(1280, 800, 2);
    expect(noteDecodeFailure({ width: 3840 })).toBe(true);
    expect(noteDecodeFailure({ width: null })).toBe(false);
  });

  it("caps a desktop too once that browser has failed a decode on a wide frame, and forgets it after 30 days", () => {
    setScreen(3840, 2160, 1, false);
    expect(effectiveWidthCap()).toBeNull();
    expect(noteDecodeFailure({ width: 3840 })).toBe(true);
    expect(effectiveWidthCap()).toBe(1920);
    // A fresh page a month later reads storage, not memory: an entry older than 30 days is dropped.
    forgetLearnedCeiling();
    window.localStorage.setItem(DECODE_WIDTH_KEY, JSON.stringify({ width: 1920, at: Date.now() - 31 * 24 * 3600 * 1000 }));
    expect(learnedWidthCeiling()).toBeNull();
    expect(window.localStorage.getItem(DECODE_WIDTH_KEY)).toBeNull();
    // A fresh entry is still honoured.
    window.localStorage.setItem(DECODE_WIDTH_KEY, JSON.stringify({ width: 1920, at: Date.now() - 24 * 3600 * 1000 }));
    expect(learnedWidthCeiling()).toBe(1920);
  });

  it("survives in memory when storage is blocked, so a private-mode browser cannot loop", () => {
    setScreen(1280, 800, 2);
    const setItem = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    expect(noteDecodeFailure({ width: 3840 })).toBe(true);
    expect(effectiveWidthCap()).toBe(1920);
    expect(noteDecodeFailure({ width: 3840 })).toBe(false);
    setItem.mockRestore();
  });
});
