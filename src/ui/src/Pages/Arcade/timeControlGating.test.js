import { describe, it, expect } from "vitest";
import { HEAVY_LANE_SYSTEMS } from "./arcadeSystems";
import { roomActionAvailability, refusalMessage } from "./roomActions";

// Which time controls a room may offer — roomActions.js, the one gate the chords, the room's buttons,
// the touch pad and the overlay menu all share.
//
// The asymmetry is the point, and it is why REWIND_SYSTEMS was deleted rather than extended:
//   fast-forward is pacing-only, so it works on any core with a retro_run — a SYSTEM property.
//   rewind needs the worker's savestate ring, armed per CORE. N64 has two cores whose serialize
//   costs differ 5x (parallel_n64 2.42 ms, mupen64plus_next 11.61 ms — the latter left unarmed
//   because it cost ~8% of the room's frame rate), so "does this system rewind?" has no answer.
// The server resolves the room's core and sends descriptor.canRewind; the client only obeys it.
const canFastForward = (d) => roomActionAvailability(d).fastForward;
const canRewind = (d) => roomActionAvailability(d).rewind;

describe("time-control gating", () => {
  it("offers rewind only when the SERVER says the ring is armed", () => {
    expect(canRewind({ system: "n64", canRewind: true })).toBe(true);
    expect(canRewind({ system: "n64", canRewind: false })).toBe(false);
  });

  it("gives the same system opposite answers on different cores", () => {
    // The case a system-keyed set could never express.
    expect(canRewind({ system: "n64", coreKey: "parallel_n64", canRewind: true })).toBe(true);
    expect(canRewind({ system: "n64", coreKey: null, canRewind: false })).toBe(false);
  });

  it("never offers rewind when the flag is missing", () => {
    // An absent capability must read as "no", not as "probably fine" — a Rewind button on an
    // unarmed worker is accepted and silently does nothing, the failure mode this replaced.
    expect(canRewind({ system: "snes" })).toBe(false);
    expect(canRewind({ system: "snes", canRewind: undefined })).toBe(false);
  });

  it("offers fast-forward on every libretro-lane system, armed or not", () => {
    for (const sys of ["nes", "snes", "n64", "ps1", "gc", "dc", "psp", "ps2"]) {
      expect(canFastForward({ system: sys })).toBe(true);
    }
  });

  it("withholds fast-forward on the heavy/capture lane — no retro_run to pace", () => {
    for (const sys of HEAVY_LANE_SYSTEMS) {
      expect(canFastForward({ system: sys })).toBe(false);
    }
  });

  it("withholds both in a competitive room and from a spectator", () => {
    for (const d of [{ competitive: true }, { spectator: true }]) {
      expect(canRewind({ system: "snes", canRewind: true, ...d })).toBe(false);
      expect(canFastForward({ system: "snes", ...d })).toBe(false);
    }
  });
});

describe("the rest of the room-action gate", () => {
  it("save states follow the core, and stop in a competitive room", () => {
    expect(roomActionAvailability({ system: "snes" }).quickSave).toBe(true);
    expect(roomActionAvailability({ system: "psp" }).quickLoad).toBe(false);
    expect(roomActionAvailability({ system: "snes", competitive: true }).quickLoad).toBe(false);
  });

  it("reset is the owner's, and a spectator gets nothing", () => {
    expect(roomActionAvailability({ system: "snes", slot: 0 }).reset).toBe(true);
    expect(roomActionAvailability({ system: "snes", slot: 1 }).reset).toBe(false);
    expect(Object.values(roomActionAvailability({ system: "snes", slot: 0, spectator: true, canRewind: true })).some(Boolean)).toBe(false);
  });

  it("says why a refused action was refused", () => {
    expect(refusalMessage("rewind", { competitive: true })).toMatch(/Competitive/);
    expect(refusalMessage("reset", { slot: 2 })).toMatch(/owner/);
    expect(refusalMessage("quickSave", { system: "psp" })).toMatch(/no save states/);
  });
});
