import { describe, it, expect } from "vitest";
import { DIRECT_BPS, ABR_PROFILES, rungDown, climbTarget, isBottomRung } from "./streamAbr";

// The measured library numbers these tests are written against (probe-playback.py, 2026-08-12):
// Kong: Skull Island — 1920x800 HEVC, source video 5,760,524 bps: Jellyfin COPIES it at the 12 and
// 8 Mbps rungs (their ceilings clear the source) and only re-encodes at 4 Mbps.
// Space Jam 2160p — source video 20,371,866 bps: every transcode rung is below it, so all of them
// genuinely re-encode.
const KONG_BPS = 5_760_524;
const SPACE_JAM_BPS = 20_371_866;

describe("rungDown", () => {
  it("walks one rung at a time when the source bitrate is unknown", () => {
    expect(rungDown(DIRECT_BPS)).toBe(12_000_000);
    expect(rungDown(12_000_000)).toBe(8_000_000);
    expect(rungDown(4_000_000)).toBe(1_500_000);
  });

  it("clamps at the bottom rung", () => {
    expect(rungDown(1_500_000)).toBe(1_500_000);
    expect(isBottomRung(rungDown(1_500_000))).toBe(true);
  });

  it("skips the rungs whose cap sits above the source — they re-deliver the same copy", () => {
    // 12 and 8 Mbps both copy this file, so the first drop that can actually help is 4 Mbps.
    expect(rungDown(DIRECT_BPS, KONG_BPS)).toBe(4_000_000);
    expect(rungDown(12_000_000, KONG_BPS)).toBe(4_000_000);
  });

  it("keeps every rung when they all sit below the source", () => {
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS)).toBe(12_000_000);
    expect(rungDown(12_000_000, SPACE_JAM_BPS)).toBe(8_000_000);
  });

  it("still lands on the bottom rung for a source below the whole ladder", () => {
    expect(rungDown(DIRECT_BPS, 900_000)).toBe(1_500_000);
  });
});

describe("climbTarget", () => {
  it("steps one rung with 1.5x headroom (desktop auto)", () => {
    expect(climbTarget(4_000_000, 13_000_000, ABR_PROFILES.auto)).toBe(8_000_000);
  });

  it("jumps to the highest supported rung under the phone ceiling (mobile auto)", () => {
    expect(climbTarget(1_500_000, 13_000_000, ABR_PROFILES["auto-mobile"])).toBe(8_000_000);
  });

  it("holds the current cap without headroom", () => {
    expect(climbTarget(4_000_000, 5_000_000, ABR_PROFILES.auto)).toBe(4_000_000);
    expect(climbTarget(4_000_000, undefined, ABR_PROFILES.auto)).toBe(4_000_000);
  });
});
