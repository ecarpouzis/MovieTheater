import { describe, it, expect, afterEach } from "vitest";
import { surfaceSize } from "./MusicVisualizer";

// The visualizer was blurry in fullscreen because the canvas backing store stayed at the HTML
// default (300×150) while butterchurn's viewport was set to the CSS box — GL clipped the viewport
// to the buffer and CSS stretched the corner it drew. surfaceSize is the single source of truth for
// both numbers now, so these tests are what stop that from creeping back.

const realDpr = window.devicePixelRatio;

function setDpr(value) {
  Object.defineProperty(window, "devicePixelRatio", { value, configurable: true });
}

afterEach(() => setDpr(realDpr));

const box = (clientWidth, clientHeight) => ({ clientWidth, clientHeight });

describe("surfaceSize", () => {
  it("renders at CSS resolution on a 1x display", () => {
    setDpr(1);
    expect(surfaceSize(box(520, 520))).toEqual({ width: 520, height: 520 });
  });

  it("renders at device resolution on a HiDPI display", () => {
    setDpr(2);
    expect(surfaceSize(box(520, 520))).toEqual({ width: 1040, height: 1040 });
  });

  it("never returns the 300x150 canvas default for a real box", () => {
    setDpr(1);
    const size = surfaceSize(box(1920, 1080));
    expect(size).toEqual({ width: 1920, height: 1080 });
  });

  it("caps a 4K fullscreen below native so the warp mesh keeps up", () => {
    setDpr(1);
    const size = surfaceSize(box(3840, 2160));
    expect(size.width).toBeLessThan(3840);
    expect(size.width * size.height).toBeLessThanOrEqual(2560 * 1440 + 1);
    // Still 16:9 — a stretched visualizer would be worse than a soft one.
    expect(size.width / size.height).toBeCloseTo(3840 / 2160, 2);
  });

  it("caps by total pixels, not by axis, for an ultrawide box", () => {
    setDpr(2);
    const size = surfaceSize(box(3440, 1440));
    expect(size.width * size.height).toBeLessThanOrEqual(2560 * 1440 + 1);
    expect(size.width / size.height).toBeCloseTo(3440 / 1440, 2);
  });

  it("stays at least 1x1 for a collapsed box", () => {
    setDpr(1);
    const size = surfaceSize(box(0, 0));
    expect(size.width).toBeGreaterThanOrEqual(1);
    expect(size.height).toBeGreaterThanOrEqual(1);
  });

  it("treats a bogus devicePixelRatio as 1x rather than collapsing the surface", () => {
    setDpr(0);
    expect(surfaceSize(box(800, 600))).toEqual({ width: 800, height: 600 });
  });
});
