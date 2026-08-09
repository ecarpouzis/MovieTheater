import { describe, it, expect } from "vitest";
import { trackIsPlayable, shuffled, recoveryDecision, diagnoseStreamUrl, withFavorite, outputChannelCount, HOST_HEALTHY, mediaErrorReason, stallVerdict, shouldPark, retryDelayMs } from "./MusicPlayerContext";

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

  // The healthy answer is the one the failure path must be able to RECOGNISE, so it can decline to
  // print it over an account the element already gave. Pinning the identity keeps the two in step.
  it("returns the recognisable healthy-host sentence, not a lookalike", async () => {
    expect(await diagnoseStreamUrl("http://x/s/t/MusicFile", answering(200))).toBe(HOST_HEALTHY);
  });
});

// The element's own MediaError is first-hand; the HEAD probe is a guess. These four codes are the
// difference between "your connection dropped" and "this file won't decode" — one sentence apiece.
describe("mediaErrorReason", () => {
  it("names each MediaError code", () => {
    expect(mediaErrorReason({ code: 1 })).toMatch(/aborted/);
    expect(mediaErrorReason({ code: 2 })).toMatch(/connection/);
    expect(mediaErrorReason({ code: 3 })).toMatch(/decode/);
    expect(mediaErrorReason({ code: 4 })).toMatch(/format/);
  });

  // Every non-answer has to be the same non-answer: the caller falls back to the probe on null, and
  // an undefined-shaped object reaching it must not throw on the path where playback already failed.
  it("says nothing when the element gave no error", () => {
    expect(mediaErrorReason(null)).toBe(null);
    expect(mediaErrorReason(undefined)).toBe(null);
    expect(mediaErrorReason({})).toBe(null);
    expect(mediaErrorReason({ code: 99 })).toBe(null);
  });
});

// The bug this vertical actually had: leave an album playing, walk away, and a couple of minutes
// later it had stopped itself. The stream was healthy the whole time — the phone's screen went off,
// the page stopped running, and the watchdog read its own sleep as silence from the stream.
describe("stallVerdict", () => {
  const tick = (over) => stallVerdict({ hidden: false, sinceTickMs: 2000, sinceProgressMs: 0, loading: false, ...over });

  it("waits while the playhead is still moving", () => {
    expect(tick({ sinceProgressMs: 4000 })).toBe("wait");
  });

  it("fails a stream that has gone quiet with the network idle", () => {
    expect(tick({ sinceProgressMs: 13000 })).toBe("fail");
  });

  // The regression that matters. A hidden page's clock measures how long the phone was asleep, and
  // acting on it tears `src` off a perfectly healthy stream that nobody was watching.
  it("never fails while the page is hidden, no matter how long the gap looks", () => {
    expect(tick({ hidden: true, sinceProgressMs: 10 * 60 * 1000 })).toBe("rearm");
    expect(tick({ hidden: true, sinceProgressMs: 10 * 60 * 1000, loading: true })).toBe("rearm");
  });

  // Same blind spot reached the other way: a renderer frozen by device sleep or a long GC never
  // reports itself hidden, but its own ticks arrive late — which is the tell.
  it("never fails on the first tick back from a frozen renderer", () => {
    expect(tick({ sinceTickMs: 90000, sinceProgressMs: 90000 })).toBe("rearm");
  });

  // A big FLAC on a phone re-requests the rest of the file every time Chrome's media buffer drains.
  // That is the browser working, and yanking the source out from under it is how a rebuffer became
  // a dead session.
  it("gives a still-loading element room to rebuffer", () => {
    expect(tick({ sinceProgressMs: 20000, loading: true })).toBe("wait");
    expect(tick({ sinceProgressMs: 20000, loading: false })).toBe("fail");
    expect(tick({ sinceProgressMs: 50000, loading: true })).toBe("fail");
  });
});

// The second way the walk-away bug came back: with the watchdog fixed, a REAL network hiccup while
// the phone slept still burned the whole recovery budget — every fetch rejects instantly when the
// radio naps, so 2 retries + a skip + the next track's retries all failed inside the same second,
// and the session was terminally stopped by the time the network returned. Parking is the answer:
// a network-level failure on a hidden or offline page spends no budget and waits for the world.
describe("shouldPark", () => {
  it("parks a network failure while the page is hidden", () => {
    expect(shouldPark({ networkLevel: true, hidden: true, offline: false })).toBe(true);
  });

  it("parks a network failure while the browser says offline, even in the foreground", () => {
    expect(shouldPark({ networkLevel: true, hidden: false, offline: true })).toBe(true);
  });

  // Visible + online + still failing is the one combination where the bounded budget is the right
  // tool: the world is fine and the stream is not, and that path must terminate.
  it("spends budget as before when the page is visible and online", () => {
    expect(shouldPark({ networkLevel: true, hidden: false, offline: false })).toBe(false);
  });

  // A bad decode or a refused token will be exactly as broken when the network returns. Parking a
  // content failure would wait forever for a change that changes nothing.
  it("never parks a content-level failure, hidden or not", () => {
    expect(shouldPark({ networkLevel: false, hidden: true, offline: true })).toBe(false);
    expect(shouldPark({ networkLevel: false, hidden: false, offline: false })).toBe(false);
  });

  it("treats missing fields as false rather than throwing", () => {
    expect(shouldPark({})).toBe(false);
    expect(shouldPark({ networkLevel: true })).toBe(false);
  });
});

// Budgeted retries wait before re-minting. The delays only matter relative to the failure they
// answer: anything > 0 outlives an instant rejection, and the clamp means a miscounted attempt
// index can only slow a retry down, never make it undefined-instant.
describe("retryDelayMs", () => {
  it("waits a beat on the first retry and longer on the second", () => {
    expect(retryDelayMs(0)).toBeGreaterThan(0);
    expect(retryDelayMs(1)).toBeGreaterThan(retryDelayMs(0));
  });

  it("clamps out-of-range attempt counts to a real delay", () => {
    expect(retryDelayMs(99)).toBe(retryDelayMs(1));
    expect(retryDelayMs(-1)).toBe(retryDelayMs(0));
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

// Opening the visualizer routes the <audio> element through a Web Audio graph permanently, and
// AudioDestinationNode defaults to 2 channels in "explicit" mode — so before this rule existed, one
// visualizer open folded every later track to stereo for the rest of the session. The fix is NOT
// "open the output as wide as the device allows": pinning a stereo track to 6 channels makes the
// browser emit 5.1 with four silent channels, which stops the OS/receiver upmixer from ever
// engaging. These pin both halves of that.
describe("outputChannelCount", () => {
  it("leaves stereo at 2 even on a 5.1-capable device", () => {
    expect(outputChannelCount(2, 6)).toBe(2);
  });

  it("carries a surround source through at its real width", () => {
    expect(outputChannelCount(6, 6)).toBe(6);
  });

  // Assigning above maxChannelCount throws IndexSizeError, so the clamp is load-bearing, not tidy.
  it("clamps a wider source to what the device can emit", () => {
    expect(outputChannelCount(8, 6)).toBe(6);
  });

  // Unknown must mean stereo: a track the backfill hasn't reached yet, or a format that wouldn't
  // report, has to behave exactly as it did before this feature existed.
  it("treats unknown as stereo", () => {
    for (const unknown of [0, null, undefined, NaN, -1]) {
      expect(outputChannelCount(unknown, 6)).toBe(2);
    }
  });

  // Mono is up-mixed to L/R by the "speakers" rules, which is what we want — narrowing the output to
  // 1 would hand the OS a mono stream instead.
  it("does not narrow below stereo for a mono source", () => {
    expect(outputChannelCount(1, 6)).toBe(2);
  });

  // A device that reports nothing useful must not produce a 0-channel destination.
  it("survives a device that reports no max", () => {
    expect(outputChannelCount(6, 0)).toBe(2);
    expect(outputChannelCount(6, undefined)).toBe(2);
  });
});
