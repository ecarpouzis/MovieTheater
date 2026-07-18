import { describe, it, expect, beforeEach } from "vitest";
import {
  controllerFamilyFor,
  controllerLabelFor,
  getFaceSwapMode,
  setFaceSwapMode,
  effectiveFaceSwap,
} from "./controllerIdentity";

const gp = (id, index = 0) => ({ id, index });

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
    const xbox = gp("Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)");
    const dualsense = gp("DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)");

    setFaceSwapMode("auto");
    expect(effectiveFaceSwap(xbox)).toBe(true);
    expect(effectiveFaceSwap(dualsense)).toBe(false);

    setFaceSwapMode("xbox");
    expect(effectiveFaceSwap(dualsense)).toBe(true); // override wins even for a non-Xbox pad

    setFaceSwapMode("nintendo");
    expect(effectiveFaceSwap(xbox)).toBe(false); // override wins even for an Xbox pad
  });
});
