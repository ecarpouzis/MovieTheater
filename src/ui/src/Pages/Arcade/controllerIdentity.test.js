import { describe, it, expect, beforeEach } from "vitest";
import {
  controllerFamilyFor,
  controllerLabelFor,
  getFaceSwapMode,
  setFaceSwapMode,
  getPadFaceSwapOverride,
  setPadFaceSwapOverride,
  effectiveFaceSwap,
} from "./controllerIdentity";

const gp = (id, index = 0) => ({ id, index });

const XBOX = "Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)";
const DUALSENSE = "DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)";
const SWITCHPRO = "Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)";

describe("controllerFamilyFor / controllerLabelFor", () => {
  it("classifies a DualSense pad from its Vendor/Product hex", () => {
    const family = controllerFamilyFor(gp("DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)"));
    expect(family.key).toBe("dualsense");
    expect(family.swapFaceButtons).toBe(false);
  });

  it("classifies a DualShock 4 pad", () => {
    const family = controllerFamilyFor(gp("Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 05c4)"));
    expect(family.key).toBe("dualshock4");
    expect(family.swapFaceButtons).toBe(false);
  });

  it("classifies an Xbox pad and flags it for the face-button swap", () => {
    const family = controllerFamilyFor(gp("Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)"));
    expect(family.key).toBe("xbox");
    expect(family.swapFaceButtons).toBe(true);
  });

  it("classifies a Switch Pro Controller", () => {
    const family = controllerFamilyFor(gp("Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)"));
    expect(family.key).toBe("switchpro");
    expect(family.swapFaceButtons).toBe(false);
  });

  it("falls back to name-substring matching when Vendor/Product is missing (Firefox-style ids)", () => {
    expect(controllerFamilyFor(gp("045e-0b13-Xbox Wireless Controller")).key).toBe("xbox");
    expect(controllerFamilyFor(gp("DualSense Wireless Controller")).key).toBe("dualsense");
  });

  it("falls back to generic for an unrecognized or missing id", () => {
    expect(controllerFamilyFor(gp("Some Unbranded Pad")).key).toBe("generic");
    expect(controllerFamilyFor({ index: 2 }).key).toBe("generic"); // no id at all — must not throw
  });

  it("controllerLabelFor shows the family label, or an indexed fallback for generic pads with no id", () => {
    expect(controllerLabelFor(gp("DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)"))).toBe("DualSense");
    expect(controllerLabelFor({ index: 2 })).toBe("Controller 3");
  });
});

describe("face-swap mode: tri-state + legacy boolean migration", () => {
  beforeEach(() => localStorage.clear());

  it("defaults to auto when nothing has ever been set", () => {
    expect(getFaceSwapMode()).toBe("auto");
  });

  it("migrates the old boolean key once: \"1\" -> xbox, \"0\" -> nintendo", () => {
    localStorage.setItem("arcade.faceSwap", "1");
    expect(getFaceSwapMode()).toBe("xbox");

    localStorage.clear();
    localStorage.setItem("arcade.faceSwap", "0");
    expect(getFaceSwapMode()).toBe("nintendo");
  });

  it("does not re-derive from the legacy key once a mode has been explicitly set", () => {
    setFaceSwapMode("nintendo");
    localStorage.setItem("arcade.faceSwap", "1"); // stale legacy value, should be ignored henceforth
    expect(getFaceSwapMode()).toBe("nintendo");
  });

  it("effectiveFaceSwap: auto resolves from the pad's family; overrides ignore the pad entirely", () => {
    const xbox = gp(XBOX);
    const dualsense = gp(DUALSENSE);

    setFaceSwapMode("auto");
    expect(effectiveFaceSwap(xbox)).toBe(true);
    expect(effectiveFaceSwap(dualsense)).toBe(false);

    setFaceSwapMode("xbox");
    expect(effectiveFaceSwap(dualsense)).toBe(true); // override wins even for a non-Xbox pad

    setFaceSwapMode("nintendo");
    expect(effectiveFaceSwap(xbox)).toBe(false); // override wins even for an Xbox pad
  });
});

describe("per-controller face-swap override (the Controllers panel's per-player checkbox)", () => {
  const xbox = gp(XBOX, 0);
  const dualsense = gp(DUALSENSE, 1);
  const switchpro = gp(SWITCHPRO, 2);

  beforeEach(() => {
    localStorage.clear();
    setFaceSwapMode("auto");
    // The override map is module-cached (the 60Hz input poll reads it), so localStorage.clear()
    // alone doesn't reset it — clear each pad through the real API.
    for (const pad of [xbox, dualsense, switchpro]) setPadFaceSwapOverride(pad, null);
  });

  it("unset by default, and then reports exactly what was set", () => {
    expect(getPadFaceSwapOverride(dualsense)).toBeUndefined();
    setPadFaceSwapOverride(dualsense, true);
    expect(getPadFaceSwapOverride(dualsense)).toBe(true);
    setPadFaceSwapOverride(dualsense, false);
    expect(getPadFaceSwapOverride(dualsense)).toBe(false);
    setPadFaceSwapOverride(dualsense, null); // "back to auto"
    expect(getPadFaceSwapOverride(dualsense)).toBeUndefined();
  });

  it("beats BOTH the machine-wide mode and the pad's detected family", () => {
    setFaceSwapMode("nintendo");
    setPadFaceSwapOverride(dualsense, true);
    expect(effectiveFaceSwap(dualsense)).toBe(true); // pad choice > machine mode

    setFaceSwapMode("auto");
    setPadFaceSwapOverride(xbox, false);
    expect(effectiveFaceSwap(xbox)).toBe(false); // pad choice > "xbox family swaps"
  });

  it("is PER PAD — one player's choice never moves another player's controller", () => {
    // The local-multiplayer case: two pads, one machine, one browser, different conventions.
    setPadFaceSwapOverride(xbox, false);
    expect(effectiveFaceSwap(xbox)).toBe(false);
    expect(effectiveFaceSwap(dualsense)).toBe(false); // untouched, still its auto answer
    expect(effectiveFaceSwap(switchpro)).toBe(false);

    setPadFaceSwapOverride(switchpro, true);
    expect(effectiveFaceSwap(switchpro)).toBe(true);
    expect(effectiveFaceSwap(xbox)).toBe(false); // unchanged
    expect(effectiveFaceSwap(dualsense)).toBe(false); // unchanged
  });

  it("keys off the pad's id, not its index — a re-enumerated pad keeps its choice", () => {
    setPadFaceSwapOverride(gp(DUALSENSE, 1), true);
    // Same physical pad, new Gamepad-API slot after a Bluetooth sleep/wake.
    expect(effectiveFaceSwap(gp(DUALSENSE, 3))).toBe(true);
  });

  it("falls back to an index key for a pad with no id, without throwing", () => {
    const nameless = { index: 4 };
    expect(getPadFaceSwapOverride(nameless)).toBeUndefined();
    setPadFaceSwapOverride(nameless, true);
    expect(effectiveFaceSwap(nameless)).toBe(true);
    setPadFaceSwapOverride(nameless, null);
    expect(effectiveFaceSwap(nameless)).toBe(false);
  });
});
