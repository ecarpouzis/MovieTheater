import { describe, it, expect } from "vitest";
import { DIRECT_BPS, ABR_PROFILES, rungDown, climbTarget, climbHoldBar, isBottomRung } from "./streamAbr";

// The measured library numbers these tests are written against (probe-playback.py, 2026-08-12):
// Kong: Skull Island — 1920x800 HEVC, source video 5,760,524 bps: Jellyfin COPIES it at the 12 and
// 8 Mbps rungs (their ceilings clear the source) and only re-encodes at 4 Mbps.
// Space Jam 2160p — source video 20,371,866 bps: every transcode rung is below it, so all of them
// genuinely re-encode.
const KONG_BPS = 5_760_524;
const SPACE_JAM_BPS = 20_371_866;

// A fat 4K remux (the case the 30/20 Mbps rungs were added for on 2026-09-02, the fiber re-baseline):
// every rung re-encodes, and a viewer who loses DIRECT should land on a rung that still looks like 4K.
const FAT_4K_BPS = 60_000_000;

describe("rungDown", () => {
  it("walks one rung at a time when the source bitrate is unknown", () => {
    expect(rungDown(DIRECT_BPS)).toBe(30_000_000);
    expect(rungDown(30_000_000)).toBe(20_000_000);
    expect(rungDown(20_000_000)).toBe(12_000_000);
    expect(rungDown(12_000_000)).toBe(8_000_000);
    expect(rungDown(4_000_000)).toBe(1_500_000);
  });

  // The usefulness margin: a rung within 15% of the source re-encodes into ~the same bytes (a restart
  // plus a generation loss for nothing), so it is skipped like a rung above the source. 20 Mbps sits
  // at 98% of Space Jam's 20.37 Mbps video — not a real drop.
  it("skips a rung that sits within 15% of the source bitrate", () => {
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS)).toBe(12_000_000);
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS, 40_000_000)).toBe(12_000_000); // 40/1.5 clears 20, but 20 is useless here
  });

  it("gives a fat 4K remux the 4K rungs before the 1080p cliff", () => {
    expect(rungDown(DIRECT_BPS, FAT_4K_BPS)).toBe(30_000_000); // blind walk: one rung
    expect(rungDown(DIRECT_BPS, FAT_4K_BPS, 50_000_000)).toBe(30_000_000); // 50/1.5 ≈ 33 → 30
    expect(rungDown(DIRECT_BPS, FAT_4K_BPS, 40_000_000)).toBe(20_000_000); // 40/1.5 ≈ 26.7 → 20
    expect(rungDown(DIRECT_BPS, FAT_4K_BPS, 25_000_000)).toBe(12_000_000); // 25/1.5 ≈ 16.7 → 12
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

  // The informed drop: with a fresh throughput estimate, land in ONE step on the highest rung the
  // measured link clears with 1.5x headroom. The 2026-08-16 case: Original stalling on a link
  // delivering ~13 Mbps — the blind walk went to 12 Mbps (still above the link) and stalled again.
  it("uses a fresh estimate to land on a rung the link actually clears", () => {
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS, 13_000_000)).toBe(8_000_000); // 13/1.5 ≈ 8.7 → 8
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS, 19_000_000)).toBe(12_000_000);
  });

  it("takes the lowest candidate when even it lacks headroom — least-bad on offer", () => {
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS, 1_800_000)).toBe(1_500_000);
  });

  it("still respects the source-bitrate skip with an estimate in hand", () => {
    // Kong: 12/8 Mbps re-deliver the copy; a 20 Mbps estimate must not land on them.
    expect(rungDown(DIRECT_BPS, KONG_BPS, 20_000_000)).toBe(4_000_000);
  });

  it("falls back to the one-rung walk without an estimate", () => {
    expect(rungDown(DIRECT_BPS, SPACE_JAM_BPS, undefined)).toBe(12_000_000);
  });
});

describe("the 4K rungs on the climb", () => {
  it("climbs a fat 4K remux back onto 30 / 20 Mbps as the link allows, never straight to 1080p", () => {
    // From 12 Mbps: 50 Mbps clears 30 (needs 45) but not DIRECT (60 × 1.5 = 90); 35 clears only 20 (30).
    expect(climbTarget(12_000_000, 50_000_000, ABR_PROFILES.auto, FAT_4K_BPS)).toBe(30_000_000);
    expect(climbTarget(12_000_000, 35_000_000, ABR_PROFILES.auto, FAT_4K_BPS)).toBe(20_000_000);
    expect(climbTarget(12_000_000, 95_000_000, ABR_PROFILES.auto, FAT_4K_BPS)).toBe(DIRECT_BPS);
  });

  it("does not climb onto a rung within 15% of the source — DIRECT is the only step up from there", () => {
    // Space Jam at 12 Mbps with a 31 Mbps link: 20 Mbps (98% of the source) is skipped; DIRECT clears.
    expect(climbTarget(12_000_000, 31_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(DIRECT_BPS);
    // ...and with 30 Mbps of link neither DIRECT (30.6) nor the useless 20 rung qualifies: hold.
    expect(climbTarget(12_000_000, 30_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(12_000_000);
    // The hold bar is priced off DIRECT, not the skipped 20 Mbps rung.
    expect(climbHoldBar(12_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(SPACE_JAM_BPS * 1.15);
    // For the fat remux the first step up from 12 is the 20 Mbps rung.
    expect(climbHoldBar(12_000_000, ABR_PROFILES.auto, FAT_4K_BPS)).toBe(20_000_000 * 1.15);
  });

  it("keeps Mobile Auto off the 4K rungs (its ceiling is 8 Mbps)", () => {
    expect(climbTarget(1_500_000, 95_000_000, ABR_PROFILES["auto-mobile"], FAT_4K_BPS)).toBe(8_000_000);
  });
});

describe("climbTarget", () => {
  it("jumps to the highest supported rung — every switch is a restart, so take one, not four", () => {
    // 13 Mbps clears only the 8 Mbps rung (12 needs 18); a 30 Mbps link with a fat source clears 12
    // but not lossless — one restart straight to 12, skipping 4 and 8 on the way.
    expect(climbTarget(4_000_000, 13_000_000, ABR_PROFILES.auto)).toBe(8_000_000);
    expect(climbTarget(1_500_000, 30_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(12_000_000);
  });

  it("jumps to the highest supported rung under the phone ceiling (mobile auto)", () => {
    expect(climbTarget(1_500_000, 13_000_000, ABR_PROFILES["auto-mobile"])).toBe(8_000_000);
  });

  it("holds the current cap without headroom", () => {
    expect(climbTarget(4_000_000, 5_000_000, ABR_PROFILES.auto)).toBe(4_000_000);
    expect(climbTarget(4_000_000, undefined, ABR_PROFILES.auto)).toBe(4_000_000);
  });

  // The lossless tier costs the FILE'S bitrate, not a fixed bar. The 2026-08-16 flaw: a fixed
  // 18 Mbps gate let auto climb into a ~21.6 Mbps remux over a ~30 Mbps link — almost no headroom at
  // the exact tier whose browser-quota-bound buffer is smallest.
  it("gates the lossless tier on the source bitrate when known", () => {
    expect(climbTarget(12_000_000, 25_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(12_000_000); // needs 20.4×1.5 ≈ 30.6
    expect(climbTarget(12_000_000, 31_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(DIRECT_BPS);
    // Unknown source: fall back to the top-transcode-rung stand-in, as before.
    expect(climbTarget(12_000_000, 19_000_000, ABR_PROFILES.auto)).toBe(DIRECT_BPS);
  });
});

describe("climbHoldBar", () => {
  it("is the NEXT rung's cost with hold headroom — the weak-evidence floor for the climb streak", () => {
    // At 8 Mbps the next rung is 12 → 13.8 Mbps; a 16 Mbps sample (short of the 18 climb bar) is
    // dead-zone, not weak.
    expect(climbHoldBar(8_000_000, ABR_PROFILES.auto)).toBe(12_000_000 * 1.15);
  });

  it("prices the lossless step by the source bitrate", () => {
    expect(climbHoldBar(12_000_000, ABR_PROFILES.auto, SPACE_JAM_BPS)).toBe(SPACE_JAM_BPS * 1.15);
  });

  it("is null at the ceiling — nothing above to hold a streak for", () => {
    expect(climbHoldBar(DIRECT_BPS, ABR_PROFILES.auto)).toBe(null);
    expect(climbHoldBar(8_000_000, ABR_PROFILES["auto-mobile"])).toBe(null);
  });
});
