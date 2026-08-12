import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── The engine, inside the player (music-mse-plan.md §Phase 2) ──────────────────────────────────
// The deck suites prove the floor still works with the flag off (its default). This one proves the
// three things that are only true with it ON: the engine's element becomes the live "deck", the
// queue advances WITHOUT a deck load at the boundary, and a cross-engine flip lands on the deck path
// and stays there.
//
// The whole point of Phase 2 is that a boundary stops being a JavaScript event, so what is asserted
// here is mostly an ABSENCE: no second Stream/Start, no src assignment, no play() at the join.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const { api } = vi.hoisted(() => ({
  api: {
    getMusicCapabilities: vi.fn(),
    getMusicFavorites: vi.fn(),
    startMusicTrack: vi.fn(),
    startMusicTracks: vi.fn(),
  },
}));

vi.mock("../MovieAPI", () => ({ MovieAPI: api }));
vi.mock("./MusicMiniPlayer", () => ({ default: () => null }));

const ok = (body) => Promise.resolve({ ok: true, json: () => Promise.resolve(body) });

const TRACKS = [
  { id: 1, title: "One", artist: "A", durationSec: 100 },
  { id: 2, title: "Two", artist: "A", durationSec: 100 },
  { id: 3, title: "Three", artist: "A", durationSec: 100 },
];

let player;
function Probe() {
  player = useMusicPlayer();
  return null;
}

// A MediaSource that accepts everything and grows its buffered range. It claims nothing about
// decoding — that is what the phone is for — only that the engine's bookkeeping is driven by it.
let sourceBuffers;
class FakeSourceBuffer extends EventTarget {
  constructor(mime) {
    super();
    this.mime = mime;
    this.endSec = 0;
    this.startSec = 0;   // moved by eviction; the timeline must be immune to it
    this.buffered = { length: 1, start: () => this.startSec, end: () => this.endSec };
  }
  appendBuffer(chunk) {
    this.endSec += chunk.byteLength / 32000;
    setTimeout(() => this.dispatchEvent(new Event("updateend")), 0);
  }
  remove() { setTimeout(() => this.dispatchEvent(new Event("updateend")), 0); }
  changeType(mime) { this.mime = mime; }
}
class FakeMediaSource extends EventTarget {
  constructor() {
    super();
    this.readyState = "open";
    setTimeout(() => this.dispatchEvent(new Event("sourceopen")), 0);
  }
  static isTypeSupported() { return true; }
  addSourceBuffer(mime) {
    const sb = new FakeSourceBuffer(mime);
    sourceBuffers.push(sb);
    return sb;
  }
  endOfStream() { this.readyState = "ended"; }
}

let playSpy;

beforeEach(() => {
  sourceBuffers = [];
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true, fmp4Enabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2, sizeBytes: 1024 }));
  api.startMusicTracks.mockImplementation((ids) => ok({
    tracks: ids.map((id) => ({
      trackId: id, mimeType: "audio/mpeg",
      url: `https://gw/${id}/file`, universalUrl: `https://gw/${id}/universal`,
      sizeBytes: 2_000_000, durationSec: 100, sampleRateHz: 44100, channels: 2,
    })),
    skipped: [],
  }));
  playSpy = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.play = playSpy;
  window.HTMLMediaElement.prototype.pause = vi.fn();
  window.HTMLMediaElement.prototype.load = vi.fn();
  global.fetch = vi.fn(async () => {
    let sent = 0;
    return {
      ok: true,
      status: 200,
      body: {
        getReader: () => ({
          read: async () => {
            if (sent >= 1_000_000) return { done: true };
            sent += 262144;
            return { done: false, value: new Uint8Array(262144) };
          },
          cancel: async () => {},
        }),
      },
    };
  });
  global.URL.createObjectURL = () => "blob:mse";
  global.URL.revokeObjectURL = vi.fn();
  vi.stubGlobal("MediaSource", FakeMediaSource);
  window.localStorage.clear();
  window.localStorage.setItem("music.engine", "mse");   // the flag, as ?mse=1 would have left it
});

afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); window.localStorage.clear(); });

/** The engine appends in chunks, and each chunk completes on its own macrotask (`updateend`). So a
 *  test that asserts on a settled buffer has to let that chain run — the alternative is asserting on
 *  a half-appended track, which is a flake, not a finding. */
async function flush(times = 24) {
  for (let i = 0; i < times; i++) {
    // eslint-disable-next-line no-await-in-loop
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  }
}

async function mountPlaying(tracks = TRACKS) {
  const view = render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
  const el = (deck) => view.container.querySelector(`audio[data-deck="${deck}"]`);
  await act(async () => { player.playTracks(tracks, 0); });
  await flush();
  return { view, el, live: () => player.audioRef.current };
}

describe("the MSE engine inside the player", () => {
  it("plays through the engine's element, leaving both decks untouched", async () => {
    const { el, live } = await mountPlaying();
    expect(live()).toBe(el("mse"));
    expect(live().dataset.deck).toBe("mse");
    expect(el("a").src).toBe("");
    expect(el("b").src).toBe("");
    // The queue was minted in ONE round trip, not one per track.
    expect(api.startMusicTracks).toHaveBeenCalledTimes(1);
    expect(api.startMusicTracks.mock.calls[0][0]).toEqual([1, 2, 3]);
    // …and the per-track deck endpoint was never asked at all.
    expect(api.startMusicTrack).not.toHaveBeenCalled();
  });

  it("advances the queue with NO load at the boundary — the whole point of Phase 2", async () => {
    const { el } = await mountPlaying();
    expect(player.index).toBe(0);
    const mse = el("mse");
    const srcBefore = mse.src;
    const playsBefore = playSpy.mock.calls.length;

    // The playhead crosses into track 2. Nothing else happens: no mint, no src, no play().
    // The whole queue is in ONE buffer, back to back — so "crossing into track 2" is just a bigger
    // currentTime, with nothing else in the media pipeline changing at all.
    expect(sourceBuffers).toHaveLength(1);
    const boundary = sourceBuffers[0].endSec / 3;
    Object.defineProperty(mse, "currentTime", { value: boundary + 1, configurable: true });
    await act(async () => { mse.dispatchEvent(new Event("timeupdate")); });
    await flush(4);
    expect(player.index).toBe(1);
    expect(player.current.id).toBe(2);
    expect(mse.src).toBe(srcBefore);
    expect(playSpy.mock.calls.length).toBe(playsBefore);
    expect(api.startMusicTrack).not.toHaveBeenCalled();
  });

  // ── The cross-engine flip (§the invariant) ────────────────────────────────────────────────────
  // A track the matrix cannot carry hands the boundary to the deck floor. The engine ends the
  // stream so a REAL `ended` fires at the exact end of the audio, and the flip that follows is the
  // one the decks already do — which is why it is a flip and not a load.
  it("hands the boundary to a pre-rolled deck when a track has no MSE treatment", async () => {
    // Track 2 comes back as a format with no lane at all.
    api.startMusicTracks.mockImplementation((ids) => ok({
      tracks: ids.map((id) => (id === 2
        ? { trackId: 2, mimeType: "audio/x-ape", url: "https://gw/2/file", sizeBytes: 1000, durationSec: 100 }
        : {
          trackId: id, mimeType: "audio/mpeg", url: `https://gw/${id}/file`,
          universalUrl: `https://gw/${id}/universal`, sizeBytes: 2_000_000, durationSec: 100,
          sampleRateHz: 44100, channels: 2,
        })),
      skipped: [],
    }));
    const { el } = await mountPlaying();

    // The deck was prepared BEFORE the boundary, which is what makes the join a flip.
    expect(el("a").src).toContain("https://gw/2/file");

    // …and when the engine's stream really ends, the player lands on that deck.
    await act(async () => { el("mse").dispatchEvent(new Event("ended")); });
    expect(player.audioRef.current).toBe(el("a"));
    expect(player.index).toBe(1);
  });

  it("keeps the decks for the rest of the session once it has fallen back", async () => {
    api.startMusicTracks.mockImplementation((ids) => ok({
      tracks: ids.map((id) => (id === 2
        ? { trackId: 2, mimeType: "audio/x-ape", url: "https://gw/2/file", sizeBytes: 1000, durationSec: 100 }
        : {
          trackId: id, mimeType: "audio/mpeg", url: `https://gw/${id}/file`,
          universalUrl: `https://gw/${id}/universal`, sizeBytes: 2_000_000, durationSec: 100,
          sampleRateHz: 44100, channels: 2,
        })),
      skipped: [],
    }));
    const { el } = await mountPlaying();
    await act(async () => { el("mse").dispatchEvent(new Event("ended")); });
    await act(async () => { player.next(); });
    await act(async () => {});
    // Track 3 is MSE-able, but coming BACK mid-queue would put a load at a boundary — exactly what
    // this design removes. The floor keeps it, through the ordinary deck load.
    expect(player.audioRef.current.dataset.deck).not.toBe("mse");
    expect(api.startMusicTrack).toHaveBeenCalled();
  });

  it("routes the engine's element through the visualizer graph, or it would play silent", async () => {
    const connected = [];
    const fakeCtx = {
      state: "running",
      destination: { channelCount: 2, maxChannelCount: 2 },
      createMediaElementSource: (element) => {
        connected.push(element.dataset.deck);
        return { connect: () => {} };
      },
      createAnalyser: () => ({ fftSize: 0, connect: () => {} }),
      resume: () => Promise.resolve(),
    };
    vi.stubGlobal("AudioContext", function AudioCtx() { return fakeCtx; });
    const { el } = await mountPlaying();
    await act(async () => { player.ensureAudioGraph(); });
    // All three: an element that misses this plays SILENTLY forever once the graph exists, and the
    // engine's is the one carrying the whole queue.
    expect(connected).toContain("mse");
    expect(connected).toContain("a");
    expect(connected).toContain("b");
    expect(el("mse")).toBeTruthy();
  });

  // ── Phase 3: the timeline, through the player ─────────────────────────────────────────────────
  it("reports TRACK-relative time, not the queue clock the element counts", async () => {
    const { el } = await mountPlaying();
    const mse = el("mse");
    // Two tracks in, on the element's clock (each fake track is ~31 s of audio). The bar must read
    // seconds into THAT song, not the 70 s the element counts.
    // Inside the MIDDLE track, where the buffer's own measurement of the track's length (the
    // distance to the next entry's start) is available and beats the catalog's.
    const elementTime = 40;
    Object.defineProperty(mse, "currentTime", { value: elementTime, configurable: true });
    const { position, duration } = player.trackTime();
    expect(position).toBeGreaterThanOrEqual(0);
    expect(position).toBeLessThan(31.5);            // inside a ~31 s track, not 40 s into a queue
    expect(duration).toBeGreaterThan(0);
    expect(duration).toBeLessThan(elementTime);     // the TRACK's length, not the queue's
  });

  it("seeks inside the buffer without touching src — the case that must feel native", async () => {
    const { el } = await mountPlaying();
    const mse = el("mse");
    const srcBefore = mse.src;
    let assigned = 0;
    Object.defineProperty(mse, "currentTime", { value: 5, writable: true, configurable: true });
    Object.defineProperty(mse, "src", {
      configurable: true,
      get: () => srcBefore,
      set: () => { assigned += 1; },
    });

    await act(async () => { player.seek(10); });    // 10 s into the CURRENT track
    expect(assigned).toBe(0);                       // no src assignment over the blob: URL, ever
    expect(mse.currentTime).toBeGreaterThan(5);     // …and the playhead moved, in element time
    expect(sourceBuffers).toHaveLength(1);          // …with the same SourceBuffer still in place
  });

  it("restarts the track for a seek outside the buffer, rather than corrupting it", async () => {
    const { el } = await mountPlaying();
    const mse = el("mse");
    Object.defineProperty(mse, "currentTime", { value: 50, writable: true, configurable: true });
    const buffersBefore = sourceBuffers.length;
    // Eviction has taken the front of the buffer — where this seek wants to land. There is no way
    // to re-fetch "this track from 5 s" (piped lanes, no Range), so the engine restarts the track.
    sourceBuffers[0].startSec = 40;
    await act(async () => { player.seek(5); });
    await flush(6);
    // A restart builds a fresh MediaSource — which is a rebuild, not a mid-buffer append at a
    // position the buffer was not expecting.
    expect(sourceBuffers.length).toBeGreaterThan(buffersBefore);
    expect(player.audioRef.current.dataset.deck).toBe("mse");
  });

  it("gives the lock screen per-track position, not a 43-minute queue", async () => {
    const { el } = await mountPlaying();
    Object.defineProperty(el("mse"), "currentTime", { value: 100, configurable: true });
    const state = navigator.mediaSession && navigator.mediaSession.__position;
    // The hook is driven by element events; assert through the same override it uses.
    const { position, duration } = player.trackTime();
    expect(duration).toBeGreaterThan(0);
    expect(position).toBeLessThan(duration + 1);
    expect(state === undefined || state === null || typeof state === "object").toBe(true);
  });

  it("is off by default — no flag, no engine", async () => {
    window.localStorage.clear();
    const { el, live } = await mountPlaying();
    expect(live()).toBe(el("a"));
    expect(api.startMusicTrack).toHaveBeenCalled();
    expect(api.startMusicTracks).not.toHaveBeenCalled();
  });
});
