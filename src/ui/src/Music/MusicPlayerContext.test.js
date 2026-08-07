import { describe, it, expect } from "vitest";
import { trackIsPlayable, shuffled, recoveryDecision, diagnoseStreamUrl, withFavorite } from "./MusicPlayerContext";

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

// A stream that dies mid-song used to end the session: the element errored, the bar said "Playback
// failed.", and nothing retried, resumed or advanced. These pin the replacement policy — and above
// all that it TERMINATES, since the failure path is the one place a retry loop could run forever.
describe("recoveryDecision", () => {
  const at = (over) => recoveryDecision({ attempts: 0, consecutiveFailures: 0, hasNext: true, ...over });

  it("retries the same track while it still has budget", () => {
    expect(at({ attempts: 0 })).toBe("retry");
    expect(at({ attempts: 1 })).toBe("retry");
  });

  it("moves on once this track's retries are spent", () => {
    expect(at({ attempts: 2 })).toBe("skip");
    expect(at({ attempts: 99 })).toBe("skip");
  });

  it("stops instead of skipping when there is nowhere to skip to", () => {
    expect(at({ attempts: 2, hasNext: false })).toBe("stop");
  });

  // The backstop for a dead gateway: without it, a server failing everything would walk the whole
  // queue at speed and call that playback.
  it("stops when enough tracks have failed back-to-back", () => {
    expect(at({ attempts: 2, consecutiveFailures: 1 })).toBe("skip");
    expect(at({ attempts: 2, consecutiveFailures: 2 })).toBe("stop");
    expect(at({ attempts: 2, consecutiveFailures: 9 })).toBe("stop");
  });

  // Termination, stated as a property: from any state, repeatedly applying the decision reaches
  // "stop" — retries are bounded per track and skips are bounded across tracks.
  it("always terminates, even with an infinite queue", () => {
    let attempts = 0;
    let consecutiveFailures = 0;
    const seen = [];
    for (let step = 0; step < 50; step++) {
      const d = recoveryDecision({ attempts, consecutiveFailures, hasNext: true });
      seen.push(d);
      if (d === "stop") break;
      if (d === "retry") attempts += 1;
      else { attempts = 0; consecutiveFailures += 1; } // skipped: a fresh track, one more failure
    }
    expect(seen).toContain("stop");
  });
});

// Every prod outage this vertical has had showed the same four words — "Playback failed." — for four
// different causes. A media error carries no status, so the status has to be asked for.
describe("diagnoseStreamUrl", () => {
  const answering = (status) => () => Promise.resolve({ status, ok: status >= 200 && status < 300 });

  it("names a 404 as the host not finding the file", async () => {
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", answering(404))).toMatch(/can't find the file \(404\)/);
  });

  it("names a 403 as a refused token", async () => {
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", answering(403))).toMatch(/refused the token \(403\)/);
  });

  it("reports any other error status verbatim", async () => {
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", answering(500))).toMatch(/answered 500/);
  });

  // A thrown fetch is the CORS/host-down case, and it must not throw out of the diagnosis — this
  // runs on the path where playback has ALREADY failed.
  it("treats a thrown fetch as no answer rather than propagating", async () => {
    const boom = () => Promise.reject(new TypeError("Failed to fetch"));
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", boom)).toMatch(/didn't answer/);
  });

  it("says nothing when there is no URL to ask about", async () => {
    expect(await diagnoseStreamUrl(null, answering(200))).toBe(null);
  });

  // A host that serves the bytes fine points the finger back at the browser/codec.
  it("distinguishes a healthy host from a playable file", async () => {
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", answering(200))).toMatch(/couldn't play it/);
  });
});

// The heart writes optimistically and rolls back if the request fails. Both directions go through
// withFavorite, so what these pin is that it really is its own inverse — a hand-written rollback is
// exactly how you end up with a filled heart over a favorite the server never recorded.
describe("withFavorite", () => {
  it("adds a track that wasn't favorited", () => {
    expect([...withFavorite(new Set([1, 2]), 3, true)].sort()).toEqual([1, 2, 3]);
  });

  it("removes a track that was", () => {
    expect([...withFavorite(new Set([1, 2, 3]), 2, false)].sort()).toEqual([1, 3]);
  });

  it("is idempotent in both directions", () => {
    expect([...withFavorite(new Set([1]), 1, true)]).toEqual([1]);
    expect([...withFavorite(new Set([1]), 2, false)]).toEqual([1]);
  });

  // The rollback path: applying !want must land back exactly where it started, whichever way the
  // toggle went.
  it("undoes itself exactly", () => {
    const before = new Set([4, 5, 6]);
    for (const [id, want] of [[7, true], [5, false]]) {
      const after = withFavorite(before, id, want);
      expect([...withFavorite(after, id, !want)].sort()).toEqual([...before].sort());
    }
  });

  // A mutated-in-place Set is reference-identical, so React bails on the update and the heart never
  // changes colour — the bug this returns a copy to avoid.
  it("returns a new Set and leaves the original alone", () => {
    const before = new Set([1]);
    const after = withFavorite(before, 2, true);
    expect(after).not.toBe(before);
    expect([...before]).toEqual([1]);
  });
});
