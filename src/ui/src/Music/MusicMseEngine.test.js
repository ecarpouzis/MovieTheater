import { beforeEach, describe, expect, it, vi } from "vitest";

import { createMseEngine, mintWindowIds, mintIsFresh, trackAtTime, MINT_LIFETIME_MS } from "./MusicMseEngine";

// The engine, exercised without a browser that has MediaSource — which this environment does not,
// and which is exactly why every rule it plays by was extracted into pure functions first. The fake
// below accepts appends and grows its buffered range; it claims nothing about decoding. What IS
// asserted here is the bookkeeping the phone cannot check for us: which lane was fetched, when a
// mint may be issued, what a QuotaExceeded does, and whether a death is reported.

class FakeSourceBuffer extends EventTarget {
  constructor(mime) {
    super();
    this.mime = mime;
    this.mode = "";
    this.endSec = 0;
    this.appends = [];
    this.removals = [];
    this.changeTypes = [];
    this.quotaAfterBytes = Infinity;
    this.bytes = 0;
    this.buffered = { length: 1, start: () => 0, end: () => this.endSec };
  }
  appendBuffer(chunk) {
    if (this.bytes + chunk.byteLength > this.quotaAfterBytes) {
      const e = new Error("quota");
      e.name = "QuotaExceededError";
      throw e;
    }
    this.bytes += chunk.byteLength;
    this.appends.push(chunk.byteLength);
    // 1 second of audio per 32 KB, i.e. the universal lane's bitrate. Close enough for arithmetic.
    this.endSec += chunk.byteLength / 32000;
    setTimeout(() => this.dispatchEvent(new Event("updateend")), 0);
  }
  remove(start, end) {
    this.removals.push([start, end]);
    // Eviction frees the bytes it dropped, which is what lets the retry-once path succeed.
    this.bytes = Math.max(0, this.bytes - (end - start) * 32000);
    setTimeout(() => this.dispatchEvent(new Event("updateend")), 0);
  }
  changeType(mime) { this.changeTypes.push(mime); this.mime = mime; }
}

class FakeMediaSource extends EventTarget {
  constructor() {
    super();
    this.readyState = "open";
    this.buffers = [];
    setTimeout(() => this.dispatchEvent(new Event("sourceopen")), 0);
  }
  static isTypeSupported() { return true; }
  addSourceBuffer(mime) {
    const sb = new FakeSourceBuffer(mime);
    this.buffers.push(sb);
    return sb;
  }
  endOfStream() { this.readyState = "ended"; }
}

function fakeAudio() {
  return { currentTime: 0, src: "", play: () => Promise.resolve(), pause: () => {} };
}

/** A Stream/Start payload, as the batch endpoint mints them. */
function payload(id, over = {}) {
  return {
    trackId: id, title: `t${id}`, mimeType: "audio/mpeg",
    url: `u/${id}/file`, universalUrl: `u/${id}/universal`,
    sizeBytes: 3_000_000, durationSec: 200, sampleRateHz: 44100, channels: 2,
    ...over,
  };
}

function makeApi(payloads) {
  const calls = [];
  return {
    calls,
    startMusicTracks: vi.fn(async (ids) => {
      calls.push(ids);
      return { ok: true, json: async () => ({ tracks: ids.map((id) => payloads[id] || payload(id)), skipped: [] }) };
    }),
  };
}

/** A body that streams bytes in 256 KB chunks, recording which URL was asked for. `total` may be a
 *  function of the URL, so one track can be made big enough to fill the append window on its own. */
function installFetch(seen, { total = 1_000_000, fail = () => false } = {}) {
  vi.stubGlobal("fetch", vi.fn(async (url) => {
    seen.push(String(url));
    if (fail(String(url))) return { ok: false, status: 404 };
    const size = typeof total === "function" ? total(String(url)) : total;
    let sent = 0;
    return {
      ok: true,
      status: 200,
      body: {
        getReader: () => ({
          read: async () => {
            if (sent >= size) return { done: true };
            const chunk = Math.min(262144, size - sent);
            sent += chunk;
            return { done: false, value: new Uint8Array(chunk) };
          },
          cancel: async () => {},
        }),
      },
    };
  }));
}

const queue = [{ id: 1, durationSec: 200 }, { id: 2, durationSec: 200 }, { id: 3, durationSec: 200 }];

function engineWith({ api, hidden = false, audio = fakeAudio(), quotaBytes, handlers = {} }) {
  return createMseEngine({
    audio,
    api,
    mediaSourceCtor: FakeMediaSource,
    isTypeSupported: () => true,
    quotaBytes,
    isHidden: () => hidden,
    ...handlers,
  });
}

describe("mintWindowIds", () => {
  it("covers the window in seconds, not in tracks", () => {
    const long = Array.from({ length: 50 }, (_, i) => ({ id: i + 1, durationSec: 600 }));
    // 2 h of 10-minute tracks is 12 of them.
    expect(mintWindowIds(long, 0)).toHaveLength(12);
  });

  it("starts where the appends are, not at the top of the queue", () => {
    expect(mintWindowIds(queue, 2)).toEqual([3]);
  });

  it("assumes a sane duration when the queue has none, rather than minting the world", () => {
    const unknown = Array.from({ length: 200 }, (_, i) => ({ id: i + 1 }));
    expect(mintWindowIds(unknown, 0).length).toBeLessThanOrEqual(30);
  });
});

describe("mintIsFresh", () => {
  it("retires a token well before its 6 h expiry", () => {
    const at = 1_000_000;
    expect(mintIsFresh({ mintedAt: at }, at + 1000)).toBe(true);
    expect(mintIsFresh({ mintedAt: at }, at + MINT_LIFETIME_MS + 1)).toBe(false);
    expect(mintIsFresh(undefined, at)).toBe(false);
  });
});

describe("trackAtTime", () => {
  const appended = [
    { trackId: 1, startSec: 0 }, { trackId: 2, startSec: 200 }, { trackId: 3, startSec: 410 },
  ];
  it("names the track the playhead is inside", () => {
    expect(trackAtTime(appended, 0).trackId).toBe(1);
    expect(trackAtTime(appended, 199).trackId).toBe(1);
    expect(trackAtTime(appended, 200).trackId).toBe(2);
    expect(trackAtTime(appended, 409).trackId).toBe(2);
    expect(trackAtTime(appended, 999).trackId).toBe(3);
  });
  it("is null before anything has been appended", () => {
    expect(trackAtTime([], 5)).toBe(null);
  });
});

describe("the engine", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    // happy-dom's createObjectURL only takes a Blob; the engine hands it a MediaSource, which is
    // exactly what a real browser wants and what no DOM shim implements.
    URL.createObjectURL = () => "blob:engine-test";
    URL.revokeObjectURL = () => {};
  });

  it("appends the queue back to back into ONE buffer, so a boundary is not an event", async () => {
    const api = makeApi({});
    const seen = [];
    installFetch(seen);
    const engine = engineWith({ api });
    await engine.start({ queue, index: 0 });
    const state = engine.inspect();
    expect(state.appended.length).toBeGreaterThan(1);
    // Track 2 starts where track 1 stopped — read off the buffer, not summed from DB durations.
    expect(state.appended[1].startSec).toBeGreaterThan(0);
    expect(state.appended[1].startSec).toBeCloseTo(state.appended[0].bytesAppended / 32000, 1);
  });

  // ── The pre-mint window ───────────────────────────────────────────────────────────────────────
  it("mints ahead while visible", async () => {
    const api = makeApi({});
    installFetch([]);
    const engine = engineWith({ api });
    await engine.start({ queue, index: 0 });
    expect(api.startMusicTracks).toHaveBeenCalled();
    expect(api.calls[0]).toEqual([1, 2, 3]);
  });

  // THE rule: a mint is a JS fetch, the first thing a backgrounded page stops being allowed to run.
  // No route may NEED one while asleep, so the engine must not even try.
  it("NEVER mints while hidden", async () => {
    const api = makeApi({});
    installFetch([]);
    const engine = engineWith({ api, hidden: true });
    await engine.start({ queue, index: 0 }).catch(() => {});
    expect(api.startMusicTracks).not.toHaveBeenCalled();
  });

  it("plays on from the window while hidden, without asking for anything new", async () => {
    const api = makeApi({});
    const seen = [];
    installFetch(seen);
    let hidden = false;
    const engine = createMseEngine({
      audio: fakeAudio(), api, mediaSourceCtor: FakeMediaSource, isTypeSupported: () => true,
      isHidden: () => hidden,
    });
    await engine.start({ queue, index: 0 });
    const mintsBefore = api.startMusicTracks.mock.calls.length;
    hidden = true;
    await engine.pump();
    expect(api.startMusicTracks.mock.calls.length).toBe(mintsBefore);
    // …and it still had URLs in hand, so it kept appending.
    expect(seen.length).toBeGreaterThan(0);
  });

  // ── Rung 5 ────────────────────────────────────────────────────────────────────────────────────
  it("evicts behind the playhead and retries once when the buffer is full", async () => {
    installFetch([]);
    const audio = fakeAudio();
    audio.currentTime = 100;          // there IS something behind the playhead to drop
    const rungs = [];
    const buffers = [];
    const engine = createMseEngine({
      audio, api: makeApi({}), isTypeSupported: () => true, isHidden: () => false,
      onRung: (n) => rungs.push(n),
      mediaSourceCtor: class extends FakeMediaSource {
        addSourceBuffer(mime) {
          const sb = super.addSourceBuffer(mime);
          sb.quotaAfterBytes = 600_000;   // two chunks, then no
          buffers.push(sb);
          return sb;
        }
      },
    });
    await engine.start({ queue, index: 0 });

    expect(rungs).toContain(5);                       // the ladder rung was logged
    expect(buffers[0].removals.length).toBeGreaterThan(0);   // …by evicting
    expect(buffers[0].appends.length).toBeGreaterThan(2);    // …and the retry then took
  });

  // ── The lane decisions, end to end ────────────────────────────────────────────────────────────
  it("fetches the bit-perfect lane while visible and the universal lane once hidden", async () => {
    const hiRes = payload(2, {
      mimeType: "audio/flac", fmp4Url: "u/2/fmp4", universalUrl: "u/2/universal",
      sizeBytes: 38_000_000, durationSec: 120, sampleRateHz: 96000,
    });
    const seen = [];
    // Track 1 is fat enough (6.5 MB ≈ 203 s at the fake's 32 KB/s) to fill the append window on its
    // own, so the visible start() stops before track 2 — which is what leaves the demotion decision
    // to the hidden pump below rather than making it in advance.
    installFetch(seen, { total: (u) => (u.includes("/1/") ? 6_500_000 : 1_000_000) });
    let hidden = false;
    const audio = fakeAudio();
    const engine = createMseEngine({
      audio, api: makeApi({ 2: hiRes }), mediaSourceCtor: FakeMediaSource,
      isTypeSupported: () => true, isHidden: () => hidden,
    });
    // Visible: track 1 is appended and the window is minted for BOTH, which is what lets the hidden
    // pump below proceed without a fetch it would not be allowed to make.
    await engine.start({ queue: [queue[0], { id: 2, durationSec: 120 }], index: 0 });
    expect(seen.some((u) => u.includes("/1/"))).toBe(true);

    // Now the screen goes off before track 2 is appended. 2.5 Mbps buys ~40 s of runway against a
    // 90 s execution gap, so the bit-perfect lane is not sleep-viable and must be demoted.
    expect(engine.inspect().appended.map((a) => a.trackId)).toEqual([1]);
    seen.length = 0;
    hidden = true;
    // Far enough in that the window has drained past the low-water mark — the engine tops up in
    // bursts rather than replacing every second as it is played.
    audio.currentTime = 195;
    await engine.pump();
    expect(seen.some((u) => u === "u/2/universal")).toBe(true);
    expect(seen.some((u) => u === "u/2/fmp4")).toBe(false);
  });

  // ⚠ Regression (Eric's phone log, 2026-08-11 18:17): a PARTIAL entry resumes on the lane it
  // started with. A hi-res flac begun bit-perfect while visible must NOT have its resume re-routed
  // to the universal encode once hidden — the byte cursor only means something in the fMP4 stream —
  // and the per-pump re-evaluation also logged "demoted" four times a second, flooding the diag
  // ring and evicting the evidence of the actual failure.
  it("resumes a partial append on its ORIGINAL lane after the screen goes off", async () => {
    const hiRes = payload(1, {
      mimeType: "audio/flac", fmp4Url: "u/1/fmp4", universalUrl: "u/1/universal",
      sizeBytes: 38_000_000, durationSec: 120, sampleRateHz: 96000,
    });
    const seen = [];
    installFetch(seen, { total: 38_000_000 });
    let hidden = false;
    const audio = fakeAudio();
    const engine = createMseEngine({
      audio, api: makeApi({ 1: hiRes }), mediaSourceCtor: FakeMediaSource,
      isTypeSupported: () => true, isHidden: () => hidden,
    });
    await engine.start({ queue: [{ id: 1, durationSec: 120 }], index: 0 });
    const entry = engine.inspect().appended[0];
    expect(entry.complete).toBe(false);            // stopped at its quota-derived ceiling, mid-track
    expect(entry.treatment.lane).toBe("fmp4");
    seen.length = 0;
    hidden = true;
    audio.currentTime = 20;                        // drained past the low-water mark
    await engine.pump();
    expect(seen).toContain("u/1/fmp4");            // sticky: same stream, same byte cursor
    expect(seen.some((u) => u.includes("universal"))).toBe(false);
  });

  it("calls a changeType on a sample-rate switch even when the MIME is identical", async () => {
    const flac44 = payload(1, { mimeType: "audio/flac", fmp4Url: "u/1/fmp4", sampleRateHz: 44100 });
    const flac96 = payload(2, { mimeType: "audio/flac", fmp4Url: "u/2/fmp4", sampleRateHz: 96000 });
    const buffers = [];
    installFetch([]);
    const engine = createMseEngine({
      audio: fakeAudio(), api: makeApi({ 1: flac44, 2: flac96 }),
      isTypeSupported: () => true, isHidden: () => false,
      mediaSourceCtor: class extends FakeMediaSource {
        addSourceBuffer(mime) { const sb = super.addSourceBuffer(mime); buffers.push(sb); return sb; }
      },
    });
    await engine.start({ queue: [queue[0], queue[1]], index: 0 });
    expect(buffers[0].changeTypes).toContain('audio/mp4; codecs="flac"');
  });

  // ── The cross-engine hand-off ─────────────────────────────────────────────────────────────────
  it("asks for a deck and ends the stream when a track has no treatment", async () => {
    const needed = [];
    installFetch([]);
    const engine = createMseEngine({
      audio: fakeAudio(), api: makeApi({}), mediaSourceCtor: FakeMediaSource,
      // Only the first track's lane is playable; everything else has no row in the matrix.
      isTypeSupported: (mime) => mime === "audio/mpeg",
      isHidden: () => false,
      onDeckNeeded: (track) => needed.push(track.id),
    });
    await engine.start({ queue: [queue[0], { id: 2, durationSec: 200 }], index: 0 });
    // Make the second track untreatable by withholding every lane it could use.
    const engine2 = createMseEngine({
      audio: fakeAudio(),
      api: {
        startMusicTracks: async (ids) => ({
          ok: true,
          json: async () => ({
            tracks: ids.map((id) => (id === 2
              ? { trackId: 2, mimeType: "audio/x-ape", sizeBytes: 1000, durationSec: 100 }
              : payload(id))),
            skipped: [],
          }),
        }),
      },
      mediaSourceCtor: FakeMediaSource,
      isTypeSupported: () => true,
      isHidden: () => false,
      onDeckNeeded: (track) => needed.push(track.id),
    });
    await engine2.start({ queue: [queue[0], queue[1]], index: 0 });
    expect(needed).toContain(2);
    expect(engine2.inspect().endedStream).toBe(true);
  });

  // ── Incidents ─────────────────────────────────────────────────────────────────────────────────
  it("reports the first rung use of a session, once", async () => {
    const beacons = [];
    navigator.sendBeacon = (url, blob) => { beacons.push({ url, blob }); return true; };
    installFetch([], { fail: (u) => u.includes("/file") });
    const engine = engineWith({ api: makeApi({}) });
    await engine.start({ queue, index: 0 }).catch(() => {});
    expect(beacons.length).toBe(1);
    expect(beacons[0].url).toBe("/API/Music/Incident");
  });

  it("advances the index from the playhead, not from a render", async () => {
    const advanced = [];
    const audio = fakeAudio();
    installFetch([]);
    const engine = engineWith({ api: makeApi({}), audio, handlers: { onAdvance: (id) => advanced.push(id) } });
    await engine.start({ queue, index: 0 });
    expect(advanced).toEqual([1]);
    const second = engine.inspect().appended[1];
    audio.currentTime = second.startSec + 1;
    await engine.pump();
    expect(advanced).toEqual([1, 2]);
  });
});
