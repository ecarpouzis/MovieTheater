import { describe, it, expect } from "vitest";
import { trackIsPlayable, shuffled } from "./MusicPlayerContext";

// The player used to gate on `!t.requiresTranscode` inline, in four separate places. That greyed
// out every non-native codec even though the gateway has always had an ffmpeg route and
// Stream/Start has always chosen it — 92 tracks in this library (85 .wma, 6 .aif, 1 .aiff),
// including a whole Green Day album, were unplayable for no reason. These tests pin the rule so it
// can't drift back into four copies.

const native = { id: 1, requiresTranscode: false, missing: false };
const wma = { id: 2, requiresTranscode: true, missing: false };
const gone = { id: 3, requiresTranscode: false, missing: true };

describe("trackIsPlayable", () => {
  it("plays a native codec whether or not transcoding is available", () => {
    expect(trackIsPlayable(native, false)).toBe(true);
    expect(trackIsPlayable(native, true)).toBe(true);
  });

  it("plays a transcode-only track when the server will transcode", () => {
    expect(trackIsPlayable(wma, true)).toBe(true);
  });

  it("refuses a transcode-only track when the server will not", () => {
    expect(trackIsPlayable(wma, false)).toBe(false);
  });

  it("never plays a missing file, even with transcoding on", () => {
    expect(trackIsPlayable(gone, true)).toBe(false);
    expect(trackIsPlayable({ ...wma, missing: true }, true)).toBe(false);
  });

  it("treats a null track as unplayable rather than throwing", () => {
    expect(trackIsPlayable(null, true)).toBe(false);
    expect(trackIsPlayable(undefined, false)).toBe(false);
  });

  // The capability answer arrives asynchronously; until it does the flag is undefined, and the
  // safe direction is "not playable" — the optimistic one produces a dead click.
  it("is pessimistic before the capability check answers", () => {
    expect(trackIsPlayable(wma, undefined)).toBe(false);
  });
});

describe("shuffled", () => {
  const tracks = [{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }, { id: 5 }];

  it("keeps every track exactly once", () => {
    const out = shuffled(tracks);
    expect(out).toHaveLength(tracks.length);
    expect(out.map((t) => t.id).sort()).toEqual([1, 2, 3, 4, 5]);
  });

  it("does not mutate the input", () => {
    const input = tracks.slice();
    shuffled(input);
    expect(input).toEqual(tracks);
  });

  it("actually reorders (deterministic rand that always picks index 0)", () => {
    // rand()->0 makes Fisher-Yates swap every element with the head, which reverses nothing but
    // does permute — the point is that the order is not simply the input.
    const out = shuffled(tracks, () => 0);
    expect(out.map((t) => t.id)).not.toEqual([1, 2, 3, 4, 5]);
    expect(out.map((t) => t.id).sort()).toEqual([1, 2, 3, 4, 5]);
  });

  it("survives empty and null input", () => {
    expect(shuffled([])).toEqual([]);
    expect(shuffled(null)).toEqual([]);
  });
});
