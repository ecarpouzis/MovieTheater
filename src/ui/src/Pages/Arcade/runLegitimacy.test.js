import { describe, it, expect } from "vitest";
import { hasSaveStates, NO_SAVE_STATE_SYSTEMS, HEAVY_LANE_SYSTEMS, QUICK_SLOT } from "./arcadeSystems";

// Run legitimacy is OBSERVED, not asserted: a run is legit until a taint says otherwise, and the room's
// competitive mode is only a guardrail. These are the rules the toast, the boards, and the launch modal
// all key off, so they get a test of their own — a silent flip here would either hand out trophies for
// save-scummed runs or withhold them from clean ones, and neither shows up as a crash.
// See docs/arcade-clean-start-plan.md.

// Mirrors AchievementToast / ArcadeLeaderboards. Deliberately does NOT consult competitive/hardcore.
const isLegit = (e) => !e.cheat && !e.savescum && !e.timeplay;

describe("run legitimacy", () => {
  it("treats an untainted CASUAL run as legit", () => {
    expect(isLegit({ competitive: false })).toBe(true);
  });

  it("treats a tainted COMPETITIVE run as not legit", () => {
    expect(isLegit({ competitive: true, savescum: true })).toBe(false);
  });

  it("is dirtied by any single taint", () => {
    expect(isLegit({ cheat: true })).toBe(false);
    expect(isLegit({ savescum: true })).toBe(false);
    expect(isLegit({ timeplay: true })).toBe(false);
  });

  it("ignores the room mode entirely — it is a guardrail, not a qualifier", () => {
    for (const competitive of [true, false]) {
      expect(isLegit({ competitive })).toBe(true);
      expect(isLegit({ competitive, cheat: true })).toBe(false);
    }
  });
});

describe("start options by system", () => {
  it("offers save-state starts on normal libretro systems", () => {
    for (const sys of ["nes", "snes", "n64", "ps1", "gc", "wii", "dc", "3ds", "nds", "saturn"]) {
      expect(hasSaveStates(sys)).toBe(true);
    }
  });

  it("collapses to Clean Start on noSaveStates cores (psp/scummvm)", () => {
    // config.worker-gl.yaml sets noSaveStates: true for psp — a t=106 returns ErrNoSaveStates, so
    // Continue/Quickload would be dead UI. Its progress is the memstick card, seeded every boot.
    // ScummVM is here for its own reason: retro_serialize_size returns 0.
    expect(hasSaveStates("psp")).toBe(false);
    expect(hasSaveStates("scummvm")).toBe(false);
  });

  it("keeps save-states for ps2 — only its PERIODIC autosave is held off", () => {
    // Regression guard for 2026-07-22..08-01, when ps2 sat in NO_SAVE_STATE_SYSTEMS and the Save/Load
    // buttons were hidden. PCSX2 serializes fine on demand (patch 0030); what was actually unwanted
    // was the 300 s timer doing it unbidden, which is now `noAutoSaveStates` in the worker config.
    // If this flips back to false, manual Save/Load and the seeded-state repro workflow die with it.
    expect(hasSaveStates("ps2")).toBe(true);
  });

  it("collapses to Clean Start on every heavy/capture lane system", () => {
    for (const sys of HEAVY_LANE_SYSTEMS) expect(hasSaveStates(sys)).toBe(false);
  });

  it("is case-insensitive and safe on missing input", () => {
    expect(hasSaveStates("PSP")).toBe(false);
    expect(hasSaveStates(null)).toBe(true);
    expect(hasSaveStates(undefined)).toBe(true);
  });

  it("keeps the heavy lane a subset of the no-save-state set", () => {
    for (const sys of HEAVY_LANE_SYSTEMS) expect(NO_SAVE_STATE_SYSTEMS.has(sys)).toBe(true);
  });

  it("gates the in-room save-state controls, not just the launch modal", () => {
    // hasSaveStates now drives BOTH: the Clean-Start collapse in ArcadePage AND whether Save / Load /
    // Snapshot render at all in the room. It previously only did the former, so those buttons were
    // still offered on cores that cannot serialize and failed silently when pressed.
    for (const sys of ["psp", "scummvm"]) expect(hasSaveStates(sys)).toBe(false);
    for (const sys of ["snes", "ps1", "n64", "ps2"]) expect(hasSaveStates(sys)).toBe(true);
  });

  it("keeps the quick slot off slot 0, which is the auto Continue slot", () => {
    // SaveStore.QuickSlot. If these ever collide, pressing Save would overwrite the player's
    // save-on-quit state — the exact bug the separate slot exists to prevent.
    expect(QUICK_SLOT).not.toBe(0);
  });
});
