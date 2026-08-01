import { describe, it, expect, afterEach } from "vitest";
import {
  displayAspect, rotatedVideoSize, videoTransform,
  stickFoldFor, setStickFoldOverride, resetStickFoldOverride,
  getRightStickSwapX, setRightStickSwapX,
  keyboardArrowsDriveDpad,
  encodePointer, systemUsesPointer,
  encodeMouseMove, encodeMouseButtons, systemUsesMouse,
} from "./cloudRetroClient";

// W10 touch pointer wire format. This is the CONTRACT with the worker (nanoarch PointerState.Set):
// an 8-byte packet [tag:1=0xF0][ver:1=1][x:i16 LE][y:i16 LE][pressed:1][flags:1], length+tag
// discriminated from the 10-byte pad Int16Array on the SAME data channel. If any of these change,
// the worker silently stops recognizing touches (it falls through to SetInput).
describe("encodePointer (W10 stylus wire format)", () => {
  const bytes = (buf) => Array.from(new Uint8Array(buf));
  it("is exactly 8 bytes with tag 0xF0 and version 1 (the worker's discriminator)", () => {
    const b = bytes(encodePointer(0, 0, 0));
    expect(b.length).toBe(8);        // NOT 10/14 (the pad frame) — length is half the discriminator
    expect(b[0]).toBe(0xf0);         // tag — the other half
    expect(b[1]).toBe(0x01);         // version
  });
  it("packs x/y as LITTLE-ENDIAN int16 (matches the pad channel, not the big-endian keyboard channel)", () => {
    const dv = new DataView(encodePointer(0x1234, -0x0002, 1));
    expect(dv.getInt16(2, true)).toBe(0x1234);
    expect(dv.getInt16(4, true)).toBe(-2);
    expect(dv.getUint8(6)).toBe(1);  // pressed
    expect(dv.getUint8(7)).toBe(0);  // flags reserved
  });
  it("normalizes pressed to 0/1 and carries full-frame extremes", () => {
    expect(new DataView(encodePointer(32767, -32767, 5)).getUint8(6)).toBe(1);
    expect(new DataView(encodePointer(0, 0, 0)).getUint8(6)).toBe(0);
    const dv = new DataView(encodePointer(32767, -32767, 1));
    expect(dv.getInt16(2, true)).toBe(32767);   // right/bottom edge of the frame
    expect(dv.getInt16(4, true)).toBe(-32767);  // left/top edge
  });
});

describe("systemUsesPointer (capability gate)", () => {
  it("is on for nds and case-insensitive", () => {
    expect(systemUsesPointer("nds")).toBe(true);
    expect(systemUsesPointer("NDS")).toBe(true);
  });
  it("is on for the touch/stylus consoles", () => {
    expect(systemUsesPointer("3ds")).toBe(true);
  });
  it("is off for scummvm — it's a MOUSE_SYSTEMS member instead, not POINTER_SYSTEMS", () => {
    // 2026-07-27, corrected same day: RETRO_DEVICE_POINTER's hover-with-pressed=0 works for the touch
    // consoles (citra/melonDS read x/y regardless of pressed) but NOT for ScummVM's own libretro port,
    // which only moves its cursor on a pressed transition/hold (verified against the core's own
    // source). A mouse game needs RETRO_DEVICE_MOUSE (see systemUsesMouse below), not POINTER.
    expect(systemUsesPointer("scummvm")).toBe(false);
  });
  it("is off for non-pointer systems and junk (no hover flood / no pointer on a pad-only room)", () => {
    for (const s of ["snes", "n64", "ps1", "gba", "gc", "scummvm", "", null, undefined])
      expect(systemUsesPointer(s)).toBe(false);
  });
});

describe("systemUsesMouse (capability gate)", () => {
  it("is on for scummvm — a mouse game, not a touch one", () => {
    // Point-and-click adventures were gamepad-only before this and a player's mouse was never sent.
    // Pairs with scummvm_pointer_device:"mouse" in config.worker-gl.yaml — without that core option
    // the packets arrive on the worker's "mouse" DataChannel and the core ignores them (it's polling
    // RETRO_DEVICE_POINTER instead).
    expect(systemUsesMouse("scummvm")).toBe(true);
    expect(systemUsesMouse("ScummVM")).toBe(true);
  });
  it("is off for touch/pad systems and junk", () => {
    for (const s of ["nds", "3ds", "snes", "n64", "", null, undefined])
      expect(systemUsesMouse(s)).toBe(false);
  });
});

// Relative-mouse wire format (stock CloudRetro's "mouse" DataChannel — nanoarch InputMouse). BIG-ENDIAN,
// unlike the pointer/pad channel's little-endian — mixing these up is a silent no-op on the worker side.
describe("encodeMouseMove / encodeMouseButtons (RETRO_DEVICE_MOUSE wire format)", () => {
  it("move packet is 5 bytes: [0x00][dx:i16 BE][dy:i16 BE]", () => {
    const dv = new DataView(encodeMouseMove(0x0102, -5));
    expect(dv.byteLength).toBe(5);
    expect(dv.getUint8(0)).toBe(0x00);
    expect(dv.getInt16(1, false)).toBe(0x0102);
    expect(dv.getInt16(3, false)).toBe(-5);
  });
  it("button packet is 2 bytes: [0x01][mask]", () => {
    const dv = new DataView(encodeMouseButtons(0x01));
    expect(dv.byteLength).toBe(2);
    expect(dv.getUint8(0)).toBe(0x01);
    expect(dv.getUint8(1)).toBe(0x01);
  });
});

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

// Right-stick left/right mirror. Keyed per GAME (mirrored camera is a property of the title, not the
// console), off unless explicitly turned on, and falling back to the system only when the game key
// isn't known yet — a room mid-resolve must not write under a blank key every other such room shares.
describe("getRightStickSwapX / setRightStickSwapX", () => {
  afterEach(() => {
    ["Mario Kart 64", "Bomberman 64", "GOLDENEYE", null].forEach((g) => setRightStickSwapX(false, g, "n64"));
  });

  it("is off until it is turned on, per game", () => {
    expect(getRightStickSwapX("Mario Kart 64", "n64")).toBe(false);
    setRightStickSwapX(true, "Mario Kart 64", "n64");
    expect(getRightStickSwapX("Mario Kart 64", "n64")).toBe(true);
    expect(getRightStickSwapX("Bomberman 64", "n64")).toBe(false); // same system, untouched
  });

  it("turns back off", () => {
    setRightStickSwapX(true, "Mario Kart 64", "n64");
    setRightStickSwapX(false, "Mario Kart 64", "n64");
    expect(getRightStickSwapX("Mario Kart 64", "n64")).toBe(false);
  });

  it("is case-insensitive on the game key", () => {
    setRightStickSwapX(true, "GOLDENEYE", "n64");
    expect(getRightStickSwapX("goldeneye", "n64")).toBe(true);
  });

  it("falls back to the system when the game key isn't known yet", () => {
    setRightStickSwapX(true, null, "n64");
    expect(getRightStickSwapX(null, "n64")).toBe(true);
    expect(getRightStickSwapX(null, "ps1")).toBe(false); // not a shared blank bucket
  });
});

// The keyboard has ONE directional input (arrows), which always drives the left stick. This decides
// whether it ALSO presses the d-pad. Unlike the gamepad, arrows can't simply drop the d-pad on every
// analog console: PS1's many digital-only games read the d-pad, so a keyboard player must keep it.
describe("keyboardArrowsDriveDpad", () => {
  it("keeps arrows on the d-pad for 2D + d-pad-movement consoles (fold off)", () => {
    // 2D cores read the d-pad; PS1/PS2/PSP/DC include digital-only games that need it — dropping it
    // would leave a keyboard player unable to move at all.
    for (const sys of ["snes", "nes", "arcade", "ps1", "ps2", "psp", "dc", "unknown-system"]) {
      expect(keyboardArrowsDriveDpad(sys, false)).toBe(true);
    }
  });

  it("makes arrows stick-ONLY on stick-primary consoles whose d-pad is a distinct function", () => {
    // n64/gc/wii: arrows = the control stick, never the d-pad — or the keyboard reproduces the
    // Goldeneye view-pan / Smash taunt double-bind.
    for (const sys of ["n64", "gc", "wii"]) {
      expect(keyboardArrowsDriveDpad(sys, false)).toBe(false);
    }
  });

  it("restores arrows→d-pad on those consoles when the live fold toggle is on", () => {
    // A keyboard player who actually wants the d-pad on N64/GC/Wii flips "left stick also acts as
    // D-pad" — the same toggle that governs the gamepad — and arrows drive the d-pad again.
    for (const sys of ["n64", "gc", "wii"]) {
      expect(keyboardArrowsDriveDpad(sys, true)).toBe(true);
    }
  });
});
