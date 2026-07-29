import { describe, it, expect } from "vitest";
import { cueShiftSeconds, retimedCue } from "./subtitleStyle";

describe("subtitle cue re-timing (nudge + timeline baseline)", () => {
  it("adds the viewer's nudge to the invisible timeline baseline", () => {
    expect(cueShiftSeconds(0, 8.76)).toBeCloseTo(8.76, 6);
    expect(cueShiftSeconds(-500, 8.76)).toBeCloseTo(8.26, 6); // 500 ms earlier than the baseline
    expect(cueShiftSeconds(250, 0)).toBeCloseTo(0.25, 6); // direct play: nudge alone
    expect(cueShiftSeconds(0, undefined)).toBe(0);
  });

  it("moves a cue onto the shifted media timeline", () => {
    // Content 2705 on a session whose timeline runs 8.76 s ahead must fire at currentTime 2713.76.
    const { start, end } = retimedCue({ start: 2705, end: 2708 }, 1, cueShiftSeconds(0, 8.76));
    expect(start).toBeCloseTo(2713.76, 6);
    expect(end).toBeCloseTo(2716.76, 6);
  });

  it("solves from the ORIGINAL times, so re-applying never compounds", () => {
    const orig = { start: 10, end: 12 };
    const once = retimedCue(orig, 1, 8.76);
    const again = retimedCue(orig, 1, 8.76);
    expect(again).toEqual(once);
  });

  it("keeps start >= 0 and end strictly after start under a negative shift", () => {
    const { start, end } = retimedCue({ start: 1, end: 1.2 }, 1, -30);
    expect(start).toBe(0);
    expect(end).toBeGreaterThan(start);
  });

  it("applies the rate correction before the shift", () => {
    const { start } = retimedCue({ start: 100, end: 102 }, 1.001, 8.76);
    expect(start).toBeCloseTo(100 * 1.001 + 8.76, 6);
  });
});
