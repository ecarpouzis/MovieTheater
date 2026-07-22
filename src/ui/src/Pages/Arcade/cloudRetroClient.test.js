import { describe, it, expect, afterEach } from "vitest";
import {
  displayAspect, rotatedVideoSize, videoTransform,
  stickFoldFor, setStickFoldOverride, resetStickFoldOverride,
} from "./cloudRetroClient";

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

// Rotated boards: the element's axes swap before it is rotated, or the frame overflows its box.
// Measured on 1942 (fbneo vertical cab): stream 672x768, av = { a: 0.75, rot: 90 }.
describe("rotatedVideoSize", () => {
  it("fills the box normally when the core asks for no rotation", () => {
    expect(rotatedVideoSize(4 / 3, 0)).toEqual({ width: "100%", height: "100%" });
    expect(rotatedVideoSize(16 / 9, 180)).toEqual({ width: "100%", height: "100%" }); // half-turn keeps axes
  });

  it("swaps width/height on a quarter turn so the rotated frame fills a 3:4 box", () => {
    expect(rotatedVideoSize(0.75, 90)).toEqual({ width: "calc(100% / 0.75)", height: "calc(100% * 0.75)" });
    expect(rotatedVideoSize(0.75, 270)).toEqual({ width: "calc(100% / 0.75)", height: "calc(100% * 0.75)" });
  });

  it("degrades to a plain fill rather than emitting calc(100% / 0)", () => {
    expect(rotatedVideoSize(0, 90)).toEqual({ width: "100%", height: "100%" });
    expect(rotatedVideoSize(NaN, 90)).toEqual({ width: "100%", height: "100%" });
  });
});

// The <video> is absolutely centred at top/left 50%, so the translate is load-bearing, not cosmetic.
// REGRESSION: 21 of 29 cores (every 2D system + ps1) never send an `av` payload — CloudRetro only emits
// one for cores with coreAspectRatio — so anything that made the transform conditional on geometry left
// the picture in the bottom-right quadrant. Seen live on Castlevania: SotN.
describe("videoTransform", () => {
  it("always centres the element, even when the core reports NO geometry", () => {
    expect(videoTransform(0, false)).toBe("translate(-50%, -50%)");
    expect(videoTransform(undefined, undefined)).toBe("translate(-50%, -50%)");
    expect(videoTransform(NaN, false)).toBe("translate(-50%, -50%)");
  });

  it("flips GL cores about the centre (bottom-left origin)", () => {
    expect(videoTransform(0, true)).toBe("translate(-50%, -50%) scaleY(-1)");
  });

  it("rotates vertical cabs the opposite way the core reports", () => {
    expect(videoTransform(90, false)).toBe("translate(-50%, -50%) rotate(-90deg)");
    expect(videoTransform(270, false)).toBe("translate(-50%, -50%) rotate(-270deg)");
  });

  it("composes rotate and flip in the order the stock client uses", () => {
    expect(videoTransform(90, true)).toBe("translate(-50%, -50%) rotate(-90deg) scaleY(-1)");
  });
});

// The left-stick→d-pad fold. It must be OFF for analog-native consoles: there the console reads the
// analog stick and the d-pad as DISTINCT inputs, so folding double-binds them — the bug behind
// "N64 Goldeneye pans the view up as I walk forward" and "GC/Wii Smash taunts when I push the stick".
// It stays ON for 2D cores, where an analog-only pad has no other way to steer and stick==d-pad.
describe("stickFoldFor", () => {
  afterEach(() => {
    ["default", "n64", "gc", "wii", "ps1", "ps2", "psp", "dc", "snes"].forEach(resetStickFoldOverride);
  });

  it("folds for pure-dpad 2D cores (an analog-only pad can still steer)", () => {
    expect(stickFoldFor("snes")).toBe(true);   // default profile
    expect(stickFoldFor("nes")).toBe(true);
    expect(stickFoldFor("arcade")).toBe(true);
    expect(stickFoldFor("unknown-system")).toBe(true); // falls back to the default profile
  });

  it("does NOT fold for analog-native 3D consoles (no double-bind with the d-pad)", () => {
    for (const sys of ["n64", "gc", "wii", "ps1", "ps2", "psp", "dc"]) {
      expect(stickFoldFor(sys)).toBe(false);
    }
  });

  it("is case-insensitive on the system key", () => {
    expect(stickFoldFor("N64")).toBe(false);
    expect(stickFoldFor("SNES")).toBe(true);
  });

  it("lets a saved user override win over the profile default (either way)", () => {
    setStickFoldOverride(true, "n64");   // a d-pad-less pad opts the fold back on
    expect(stickFoldFor("n64")).toBe(true);
    setStickFoldOverride(false, "snes"); // and it can be turned off for a 2D core too
    expect(stickFoldFor("snes")).toBe(false);
  });

  it("returns to the profile default once the override is cleared", () => {
    setStickFoldOverride(true, "gc");
    expect(stickFoldFor("gc")).toBe(true);
    resetStickFoldOverride("gc");
    expect(stickFoldFor("gc")).toBe(false);
  });
});
