import { describe, it, expect } from "vitest";
import { displayAspect } from "./cloudRetroClient";

// The numbers below are MEASURED from real rooms (worker log `Libretro System A/V >>> … AR [x]`,
// 2026-07-08), not invented. They are the regression fence for the aspect-ratio fix: before it, the
// room page hardcoded 4:3 for every system except gb/gbc/gba and stretched to fit (objectFit:"fill").
describe("displayAspect", () => {
  it("uses the core-reported aspect (PSP is 16:9, not 4:3)", () => {
    // ppsspp, Daxter: `960x540 (960x544) … AR [1.7777778]`. This is the headline bug: 473 PSP games
    // were being squeezed into 4:3.
    expect(displayAspect({ a: 1.7777778 })).toBeCloseTo(16 / 9, 5);
  });

  it("passes a rotated vertical arcade board's aspect through VERBATIM", () => {
    // fbneo, 1942: `256x224 (256x256) … AR [0.75]` with rot=90. The core already reports the
    // POST-rotation display aspect, and CloudRetro transposes the encoded frame (it arrives 672x768).
    // Inverting on rotation would flip vertical shooters back to landscape. Stock CloudRetro does the
    // same (web/js/stream.js resize(): style.aspectRatio = a, rotate(-rot) applied separately).
    expect(displayAspect({ a: 0.75, rot: 90 })).toBeCloseTo(0.75, 5);
  });

  it("keeps 4:3 systems at 4:3", () => {
    // fbneo Metal Slug + dolphin F-Zero GX both report AR [1.3333334].
    expect(displayAspect({ a: 1.3333334 })).toBeCloseTo(4 / 3, 5);
  });

  it("returns null when the core does not specify one (libretro: aspect_ratio <= 0)", () => {
    // Caller then falls back to its per-system table.
    expect(displayAspect({ a: 0 })).toBeNull();
    expect(displayAspect({ a: -1 })).toBeNull();
    expect(displayAspect({})).toBeNull();
    expect(displayAspect(null)).toBeNull();
  });

  it("rejects nonsense rather than distorting the screen", () => {
    expect(displayAspect({ a: NaN })).toBeNull();
    expect(displayAspect({ a: Infinity })).toBeNull();
    expect(displayAspect({ a: 99 })).toBeNull();
    expect(displayAspect({ a: 0.01 })).toBeNull();
  });
});
