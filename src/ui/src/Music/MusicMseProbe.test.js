import { describe, expect, it } from "vitest";

import {
  PROBE_STEPS, PROBE_TYPES, buildCapabilityMatrix, chunkRanges, describeCandidate, formatBytes,
  gapDistribution, hiddenFetchSummary, pushRing, summarizeRun, switchReasonFor, treatmentFor,
} from "./MusicMseProbe";

// The probe page's arithmetic and classification, tested without a MediaSource — which this
// environment does not have, and which no unit test should pretend to have. The DOM half of the page
// is measured by running it on the phone; these are the parts that can be got wrong silently.

describe("buildCapabilityMatrix", () => {
  it("reports every treatment row, and the mp4a.6B trap among them", () => {
    const caps = buildCapabilityMatrix({
      isTypeSupported: (mime) => mime !== 'audio/mp4; codecs="mp4a.6B"',
      hasMediaSource: true,
      hasManagedMediaSource: false,
    });
    expect(caps.rows).toHaveLength(PROBE_TYPES.length);
    expect(caps.rows.find((r) => r.key === "mpeg").supported).toBe(true);
    expect(caps.rows.find((r) => r.key === "mp3mp4").supported).toBe(false);
    expect(caps.anyTreatment).toBe(true);
  });

  it("says NO to everything when there is no MediaSource at all (ladder rung 7)", () => {
    const caps = buildCapabilityMatrix({
      isTypeSupported: () => true,
      hasMediaSource: false,
      hasManagedMediaSource: false,
    });
    expect(caps.rows.every((r) => !r.supported)).toBe(true);
    expect(caps.anyTreatment).toBe(false);
  });

  it("treats a thrown isTypeSupported as unsupported rather than breaking the page", () => {
    const caps = buildCapabilityMatrix({
      isTypeSupported: () => { throw new Error("bad codec string"); },
      hasMediaSource: true,
      hasManagedMediaSource: true,
    });
    expect(caps.rows.every((r) => !r.supported)).toBe(true);
    expect(caps.hasManagedMediaSource).toBe(true);
  });

  // The trap row is only ever expected to be false; if a browser says yes it must be visible as a
  // finding, so the row keeps its flag rather than being dropped from the matrix.
  it("keeps the trap row flagged", () => {
    const caps = buildCapabilityMatrix({ isTypeSupported: () => false, hasMediaSource: true });
    expect(caps.rows.find((r) => r.key === "mp3mp4").trap).toBe(true);
  });
});

describe("treatmentFor", () => {
  const mp3 = { mimeType: "audio/mpeg", url: "u/file", universalUrl: "u/universal" };
  const flac = { mimeType: "audio/flac", url: "u/file", fmp4Url: "u/fmp4", universalUrl: "u/universal" };
  const wma = { mimeType: "audio/mpeg", url: "u/transcode", universalUrl: "u/universal" };
  const all = () => true;
  const none = () => false;

  it("routes an mp3 to its raw bytes — no ffmpeg, no server work", () => {
    expect(treatmentFor(mp3, all)).toEqual({ lane: "file", url: "u/file", mime: "audio/mpeg" });
  });

  it("routes a flac to the fMP4 lane, which is bit-perfect", () => {
    expect(treatmentFor(flac, all)).toEqual({
      lane: "fmp4", url: "u/fmp4", mime: 'audio/mp4; codecs="flac"',
    });
  });

  // Rung 1 of the fallback ladder, and the reason routing is per-FORMAT rather than "is MediaSource
  // present": Firefox's MSE has no MP3 decoder, so its mp3s must take the universal treatment.
  it("falls to universal when the bit-perfect treatment is unsupported", () => {
    expect(treatmentFor(mp3, none).lane).toBe("universal");
    expect(treatmentFor(flac, none).lane).toBe("universal");
  });

  it("falls to universal when the server minted no fMP4 URL", () => {
    const { fmp4Url, ...withoutFmp4 } = flac;
    expect(fmp4Url).toBe("u/fmp4");
    expect(treatmentFor(withoutFmp4, all).lane).toBe("universal");
  });

  it("never routes anything but a flac payload to the fMP4 lane (the mp4a.6B trap)", () => {
    [mp3, wma].forEach((payload) => {
      const t = treatmentFor(payload, all);
      expect(t.url).not.toBe("u/fmp4");
      expect(t.mime).not.toContain("6B");
    });
  });

  it("returns null when there is no lane at all", () => {
    expect(treatmentFor({ mimeType: "audio/x-ape" }, all)).toBe(null);
    expect(treatmentFor(null, all)).toBe(null);
  });
});

describe("chunkRanges", () => {
  it("covers the whole file exactly once", () => {
    const ranges = chunkRanges(2500, 1000);
    expect(ranges).toEqual([{ start: 0, end: 1000 }, { start: 1000, end: 2000 }, { start: 2000, end: 2500 }]);
  });

  it("does not emit an empty trailing chunk on an exact multiple", () => {
    expect(chunkRanges(2000, 1000)).toHaveLength(2);
  });

  it("is empty for no bytes, and one chunk for a nonsense chunk size", () => {
    expect(chunkRanges(0, 1000)).toEqual([]);
    expect(chunkRanges(500, 0)).toEqual([{ start: 0, end: 500 }]);
  });
});

describe("gapDistribution", () => {
  it("finds the worst gap and names the event before it", () => {
    const g = gapDistribution([
      { at: 1000, event: "media:timeupdate" },
      { at: 1250, event: "media:timeupdate" },
      { at: 1500, event: "sb:updateend" },
      { at: 92000, event: "media:timeupdate" },  // the screen went off here
      { at: 92250, event: "media:timeupdate" },
    ]);
    expect(g.count).toBe(5);
    expect(g.maxGapMs).toBe(90500);
    expect(g.maxGapAfter).toBe("sb:updateend");
    expect(g.spanMs).toBe(91250);
  });

  it("buckets the gaps", () => {
    const g = gapDistribution([
      { at: 0, event: "a" }, { at: 100, event: "b" }, { at: 3000, event: "c" }, { at: 70000, event: "d" },
    ]);
    const byLabel = Object.fromEntries(g.buckets.map((b) => [b.label, b.count]));
    expect(byLabel["< 1s"]).toBe(1);
    expect(byLabel["1–5s"]).toBe(1);
    expect(byLabel["> 60s"]).toBe(1);
  });

  it("survives an empty or single-entry census", () => {
    expect(gapDistribution([]).maxGapMs).toBe(0);
    expect(gapDistribution([{ at: 5, event: "a" }]).count).toBe(1);
    expect(gapDistribution(undefined).buckets).toHaveLength(5);
  });
});

describe("pushRing", () => {
  it("bounds the ring, keeping the newest entries", () => {
    let ring = [];
    for (let i = 0; i < 5; i++) ring = pushRing(ring, i, 3);
    expect(ring).toEqual([2, 3, 4]);
  });

  it("does not mutate the list it was given (the state it came from is still rendering)", () => {
    const first = [1];
    const second = pushRing(first, 2, 10);
    expect(first).toEqual([1]);
    expect(second).toEqual([1, 2]);
  });
});

describe("switchReasonFor", () => {
  const flac44 = { mime: 'audio/mp4; codecs="flac"', sampleRateHz: 44100, channels: 2 };

  it("names a codec/container change", () => {
    expect(switchReasonFor({ ...flac44, mime: "audio/mpeg" }, flac44)).toBe("codec/container");
  });

  // The one that was measured the hard way: same MIME, different rate, and without a changeType the
  // SourceBuffer errors out a couple of hundred KB in.
  it("names a sample-rate change even though the MIME is identical", () => {
    expect(switchReasonFor(flac44, { ...flac44, sampleRateHz: 96000 })).toBe("sample rate");
  });

  it("names a channel-count change", () => {
    expect(switchReasonFor(flac44, { ...flac44, channels: 1 })).toBe("channel count");
  });

  it("is null when nothing differs — a changeType there would test something else", () => {
    expect(switchReasonFor(flac44, { ...flac44 })).toBe(null);
  });

  it("does not invent a switch out of a missing value", () => {
    expect(switchReasonFor({ mime: "audio/mpeg" }, { mime: "audio/mpeg" })).toBe(null);
    expect(switchReasonFor(null, flac44)).toBe(null);
  });
});

describe("hiddenFetchSummary", () => {
  // The gate, as two numbers. A fetch issued while hidden that never comes back is the finding that
  // falsifies the whole design, so "issued but neither completed nor failed" must be counted as its
  // own thing rather than rounding into either.
  const issued = (seq) => ({ seq, hiddenAtIssue: true, state: "issued" });
  const done = (seq) => ({ seq, hiddenAtIssue: true, state: "completed" });
  const failed = (seq) => ({ seq, hiddenAtIssue: true, state: "failed" });

  it("passes when every hidden fetch came back", () => {
    const s = hiddenFetchSummary([issued(1), done(1), issued(2), done(2)]);
    expect(s).toMatchObject({ issued: 2, completed: 2, unanswered: 0, status: "pass" });
  });

  it("fails when none of them did — the design's central bet, lost", () => {
    const s = hiddenFetchSummary([issued(1), issued(2)]);
    expect(s).toMatchObject({ issued: 2, completed: 0, unanswered: 2, status: "fail" });
  });

  it("calls it partial when some landed", () => {
    expect(hiddenFetchSummary([issued(1), done(1), issued(2)]).status).toBe("partial");
  });

  it("counts a hidden fetch once however many rows it wrote", () => {
    expect(hiddenFetchSummary([issued(1), done(1)]).issued).toBe(1);
  });

  it("separates a failure from a silence", () => {
    const s = hiddenFetchSummary([issued(1), failed(1)]);
    expect(s).toMatchObject({ failed: 1, unanswered: 0, status: "fail" });
  });

  it("says nothing rather than passing when the phone was never asleep", () => {
    expect(hiddenFetchSummary([{ seq: 1, hiddenAtIssue: false, state: "completed" }]).status).toBe("skip");
    expect(hiddenFetchSummary([]).status).toBe("skip");
  });
});

describe("summarizeRun", () => {
  it("reads out one row per probe, in the order the gate runs them", () => {
    const s = summarizeRun({ results: {}, fetchLog: [], census: [] });
    expect(s.probes.map((p) => p.key)).toEqual(PROBE_STEPS.map((p) => p.key));
    expect(s.probes.every((p) => p.status === "none")).toBe(true);
    expect(s.overall).toBe("NO RESULT");
  });

  it("is INCOMPLETE while a run is still going", () => {
    const s = summarizeRun({
      results: { caps: { status: "pass", verdict: "PASS", at: 1 } },
      fetchLog: [], census: [],
    });
    expect(s.overall).toBe("INCOMPLETE");
  });

  it("is FAIL the moment any probe fails, even with others still to run", () => {
    const s = summarizeRun({
      results: { caps: { status: "pass", at: 1 }, join: { status: "fail", verdict: "FAIL — x", at: 2 } },
      fetchLog: [], census: [],
    });
    expect(s.overall).toBe("FAIL");
  });

  // A skipped probe is not a failure: a library with no mono file cannot answer the mono question,
  // and that must not read as the browser having failed it.
  it("passes when everything that could run did, and the rest were skipped", () => {
    const results = {};
    PROBE_STEPS.forEach((step, i) => {
      results[step.key] = { status: i % 2 ? "skip" : "pass", verdict: "…", at: i };
    });
    expect(summarizeRun({ results, fetchLog: [], census: [] }).overall).toBe("PASS");
  });

  it("carries the two headline numbers", () => {
    const s = summarizeRun({
      results: {},
      fetchLog: [{ seq: 1, hiddenAtIssue: true, state: "issued" }],
      census: [{ at: 0, event: "a" }, { at: 30000, event: "b" }],
    });
    expect(s.hidden.issued).toBe(1);
    expect(s.gap.maxGapMs).toBe(30000);
  });
});

describe("describeCandidate", () => {
  it("labels a candidate with the properties the probe is actually testing", () => {
    expect(describeCandidate({
      title: "Track", extension: ".flac", sampleRateHz: 96000, channels: 2, sizeBytes: 30 * 1024 * 1024,
    })).toBe("Track — .flac · 96 kHz · 2 ch · 30.00 MB");
  });

  it("names mono as mono, because that is the whole point of that slot", () => {
    expect(describeCandidate({ title: "T", extension: ".mp3", channels: 1, sizeBytes: 1024 }))
      .toContain("mono");
  });

  it("says what an empty slot means rather than showing a blank", () => {
    expect(describeCandidate(null)).toBe("none in this library");
  });
});

describe("formatBytes", () => {
  it("reads at a glance on a phone", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(900)).toBe("900 B");
    expect(formatBytes(2048)).toBe("2.0 KB");
    expect(formatBytes(12 * 1024 * 1024)).toBe("12.00 MB");
  });
});
