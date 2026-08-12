import { describe, expect, it } from "vitest";

import {
  elementTimeFor, entryDurationSec, isQueueEndStall, seekPlan, trackTimeAt,
} from "./musicTimeline";

// The mapping between the element's ONE clock (queue-seconds, because the whole queue is one
// SourceBuffer) and what every consumer means by "how far into this song". Getting it wrong is not
// subtle: the bar reads 43-minute positions, the lock screen shows a 43-minute track, and the lyrics
// scroll to the wrong line — which is exactly the class of bug that only misbehaves while somebody
// is actually listening.

const entries = [
  { trackId: 1, startSec: 0, durationSec: 62, complete: true },
  { trackId: 2, startSec: 62, durationSec: 55, complete: true },
  { trackId: 3, startSec: 117, durationSec: 177, complete: false },
];

describe("trackTimeAt — element clock → track", () => {
  it("maps a position inside each track to that track's own offset", () => {
    expect(trackTimeAt(entries, 0)).toMatchObject({ trackId: 1, offsetSec: 0 });
    expect(trackTimeAt(entries, 30)).toMatchObject({ trackId: 1, offsetSec: 30 });
    expect(trackTimeAt(entries, 62)).toMatchObject({ trackId: 2, offsetSec: 0 });
    expect(trackTimeAt(entries, 100)).toMatchObject({ trackId: 2, offsetSec: 38 });
    expect(trackTimeAt(entries, 200)).toMatchObject({ trackId: 3, offsetSec: 83 });
  });

  it("reports the track's own duration, not the queue's", () => {
    expect(trackTimeAt(entries, 100).durationSec).toBe(55);
    expect(trackTimeAt(entries, 200).durationSec).toBe(177);
  });

  // ⚠ The mapping must be immune to eviction. `remove()` drops bytes from the front but does NOT
  // shift the timeline, so an entry's startSec stays true all session. A mapping that keyed off
  // buffered.start() instead would slide every position forward by whatever had been dropped.
  it("is unaffected by eviction moving the buffered start", () => {
    const afterEviction = trackTimeAt(entries, 200);   // 40 s of the front may be long gone
    expect(afterEviction).toMatchObject({ trackId: 3, offsetSec: 83 });
  });

  it("clamps inside the track rather than reading past its end", () => {
    // A playhead a hair past a track's last byte must not render as a thumb off the end of the bar.
    expect(trackTimeAt(entries, 61.99).offsetSec).toBeLessThanOrEqual(62);
    expect(trackTimeAt(entries, 116).offsetSec).toBeLessThanOrEqual(55);
  });

  it("is null before anything has been appended", () => {
    expect(trackTimeAt([], 10)).toBe(null);
    expect(trackTimeAt(entries, -5)).toBe(null);
  });
});

describe("elementTimeFor — track → element clock", () => {
  it("round-trips with trackTimeAt", () => {
    const at = elementTimeFor(entries, 2, 38);
    expect(at).toBe(100);
    expect(trackTimeAt(entries, at)).toMatchObject({ trackId: 2, offsetSec: 38 });
  });

  it("clamps an offset past the track's end", () => {
    expect(elementTimeFor(entries, 1, 9999)).toBe(62);
  });

  it("is null for a track that is not in the buffer's plan", () => {
    expect(elementTimeFor(entries, 99, 0)).toBe(null);
  });
});

describe("entryDurationSec", () => {
  // The buffer's own answer beats the catalog's: it accounts for whatever the encoder actually
  // produced, and it is the only answer for a track whose metadata never carried a duration.
  it("prefers the distance to the next entry's start", () => {
    const drifted = [
      { trackId: 1, startSec: 0, durationSec: 62 },
      { trackId: 2, startSec: 61.3, durationSec: 55 },
    ];
    expect(entryDurationSec(drifted, 0)).toBeCloseTo(61.3, 2);
  });

  it("falls back to the payload duration for the last entry", () => {
    expect(entryDurationSec(entries, 2)).toBe(177);
  });

  it("is zero when nothing knows", () => {
    expect(entryDurationSec([{ trackId: 1, startSec: 0 }], 0)).toBe(0);
    expect(entryDurationSec([], 0)).toBe(0);
  });
});

describe("seekPlan", () => {
  const buffered = { bufferedStart: 40, bufferedEnd: 250 };

  // The case that must feel native: the bytes are already there, so it is a local operation.
  it("seeks inside the buffer directly, in element time", () => {
    const plan = seekPlan({ entries, ...buffered, trackId: 2, offsetSec: 38 });
    expect(plan).toMatchObject({ kind: "inBuffer", elementTime: 100 });
  });

  it("restarts the track when the target was evicted", () => {
    const plan = seekPlan({ entries, ...buffered, trackId: 1, offsetSec: 5 });
    expect(plan.kind).toBe("restart");
    expect(plan.reason).toMatch(/evicted/);
  });

  // Not appended yet: the lanes are piped ffmpeg with no Range, so "this track from 2:30" cannot be
  // fetched at all. Restarting the track is the honest answer, and it is what a manual jump does.
  it("restarts the track when the target is past what is appended", () => {
    const plan = seekPlan({ entries, ...buffered, trackId: 3, offsetSec: 170 });
    expect(plan.kind).toBe("restart");
    expect(plan.reason).toMatch(/not appended/);
  });

  it("never seeks exactly to the buffered edge, which lands on nothing", () => {
    const plan = seekPlan({ entries, bufferedStart: 0, bufferedEnd: 100, trackId: 2, offsetSec: 38 });
    expect(plan.kind).toBe("restart");
  });

  it("says so when the track is not in the buffer's plan at all", () => {
    expect(seekPlan({ entries, ...buffered, trackId: 99, offsetSec: 0 }).kind).toBe("unavailable");
  });
});

describe("isQueueEndStall — the guard the field run asked for", () => {
  // Measured on the phone: after endOfStream() the element drained its last 161 s and fired
  // `waiting`, NOT `ended`, at t=2586 with readyState CURRENT. Anything waiting on that `ended`
  // waited forever — which at a queue end is harmless, and at a cross-engine hand-off is silence
  // for the rest of the night.
  it("treats a stall at the end of an ENDED stream as the end", () => {
    expect(isQueueEndStall({ endedStream: true, currentTime: 2586, bufferedEnd: 2586.4 })).toBe(true);
  });

  it("ignores a stall in the middle of an ended stream — that is a real buffer problem", () => {
    expect(isQueueEndStall({ endedStream: true, currentTime: 900, bufferedEnd: 2586 })).toBe(false);
  });

  // The discriminator that keeps this from swallowing every mid-queue stall: we must have ENDED the
  // stream ourselves. A stall on a stream still being appended to is the dry-buffer failure.
  it("never fires on a stream that is still open", () => {
    expect(isQueueEndStall({ endedStream: false, currentTime: 2586, bufferedEnd: 2586.4 })).toBe(false);
  });

  it("does not fire before anything is buffered", () => {
    expect(isQueueEndStall({ endedStream: true, currentTime: 0, bufferedEnd: 0 })).toBe(false);
  });
});
