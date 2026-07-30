import { describe, it, expect } from "vitest";
import { HEAVY_LANE_SYSTEMS } from "./arcadeSystems";

// Which time controls a room may offer. Mirrors ArcadeRoomPage's chord gate and the two buttons.
//
// The asymmetry is the point, and it is why REWIND_SYSTEMS was deleted rather than extended:
//   fast-forward is pacing-only, so it works on any core with a retro_run — a SYSTEM property.
//   rewind needs the worker's savestate ring, armed per CORE. N64 has two cores whose serialize
//   costs differ 5x (parallel_n64 2.42 ms, mupen64plus_next 11.61 ms — the latter left unarmed
//   because it cost ~8% of the room's frame rate), so "does this system rewind?" has no answer.
// The server resolves the room's core and sends descriptor.canRewind; the client only obeys it.
const canFastForward = (d) => !d.competitive && !d.spectator
  && !HEAVY_LANE_SYSTEMS.has(String(d.system || "").toLowerCase());
const canRewind = (d) => !d.competitive && !d.spectator && !!d.canRewind;

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
