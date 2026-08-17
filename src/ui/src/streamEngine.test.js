import { describe, it, expect, vi } from "vitest";

// The engine only touches Hls inside createHls; the offset math under test is pure, and happy-dom
// has no MediaSource for the real library to probe at import.
vi.mock("hls.js", () => ({ default: { Events: {}, ErrorDetails: {}, ErrorTypes: {} } }));

import { timelineOffsetFromInitPts, bandwidthSample } from "./streamEngine";

// A fresh hls.js instance reports its CONFIGURED default (500 kbps) from bandwidthEstimate until the
// EWMA has real fragment samples — it never says "I don't know". Since every ABR switch builds a new
// instance while the 5s samplers keep running, an unguarded sampler feeds that placeholder to the ABR
// as a measurement; the throughput-informed drop would then price a healthy link at 0.5 Mbps and slam
// it to the bottom rung. bandwidthSample is the guard.
describe("bandwidthSample", () => {
  const DEFAULT_ESTIMATE = 500_000;

  it("discards the placeholder a fresh instance reports before it can estimate", () => {
    expect(
      bandwidthSample({ bandwidthEstimate: DEFAULT_ESTIMATE, abrEwmaDefaultEstimate: DEFAULT_ESTIMATE })
    ).toBeNull();
  });

  it("passes a real measurement through once the estimator has data", () => {
    expect(
      bandwidthSample({ bandwidthEstimate: 31_000_000, abrEwmaDefaultEstimate: DEFAULT_ESTIMATE })
    ).toBe(31_000_000);
    // A genuinely slow link still reports — it just has to differ from the canned value.
    expect(
      bandwidthSample({ bandwidthEstimate: 1_400_000, abrEwmaDefaultEstimate: DEFAULT_ESTIMATE })
    ).toBe(1_400_000);
  });

  it("is null with no instance or an unusable estimate (destroyed mid-restart)", () => {
    expect(bandwidthSample(null)).toBeNull();
    expect(bandwidthSample(undefined)).toBeNull();
    expect(bandwidthSample({ bandwidthEstimate: NaN, abrEwmaDefaultEstimate: DEFAULT_ESTIMATE })).toBeNull();
    expect(bandwidthSample({ bandwidthEstimate: 0, abrEwmaDefaultEstimate: DEFAULT_ESTIMATE })).toBeNull();
  });
});

describe("timelineOffsetFromInitPts", () => {
  // The measured 2026-07-29 join: segment slot 2711.71 s carried a first packet at true PTS
  // 2702.950 (the previous source keyframe, one ~8.6 s GOP back), so initPTS = −8.76 s at a 90 kHz
  // timescale. The offset must come back POSITIVE: currentTime = content + 8.76, so a cue authored
  // at content 2705 has to be moved to 2713.76 to land on its dialogue.
  it("returns the positive shift for a join that snapped back to the previous keyframe", () => {
    const offset = timelineOffsetFromInitPts({ initPTS: -8.76 * 90_000, timescale: 90_000 });
    expect(offset).toBeCloseTo(8.76, 6);
  });

  it("reads an object-valued initPTS ({ baseTime, timescale }) the same way", () => {
    const offset = timelineOffsetFromInitPts({
      initPTS: { baseTime: -8.76 * 90_000, timescale: 90_000, trackId: 1 },
      timescale: 90_000,
    });
    expect(offset).toBeCloseTo(8.76, 6);
  });

  it("is 0 for a keyframe-aligned start (a join at position 0)", () => {
    expect(timelineOffsetFromInitPts({ initPTS: 0, timescale: 90_000 })).toBe(0);
  });

  it("returns null rather than a bogus offset when the payload can't be read", () => {
    expect(timelineOffsetFromInitPts(undefined)).toBeNull();
    expect(timelineOffsetFromInitPts({})).toBeNull();
    expect(timelineOffsetFromInitPts({ initPTS: 900, timescale: 0 })).toBeNull();
    expect(timelineOffsetFromInitPts({ initPTS: NaN, timescale: 90_000 })).toBeNull();
    expect(timelineOffsetFromInitPts({ initPTS: { baseTime: 900 } })).toBeNull();
  });
});
