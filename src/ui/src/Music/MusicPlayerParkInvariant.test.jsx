import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── The `park:live` invariant reporter ──────────────────────────────────────────────────────────
// Both "two songs at once" bugs (2026-08-12, 2026-08-13) ended in the same state: an <audio> element
// playing where nothing in the player could reach it — pause and Clear queue touch the ACTIVE deck,
// cancelPreroll the IDLE one, and every handler ignores an element that is not live. Neither wrote a
// single incident row, because nothing FAILED in any sense the tripwire set recognises: a seek
// detour is a healthy, deliberate operation that happened to leave a second source running.
//
// So the backstop now checks the invariant instead of assuming it. parkEngineDeck() runs AFTER
// engine.destroy() has already paused the element; finding it still playing means the primary
// silencer didn't, and that is worth a row and a beacon.
//
// ⚠ Its own module file, not a describe block bolted onto MusicPlayerMse.test.jsx. musicDiag's
// report budget (REPORT_MAX_PER_SESSION, REPORT_MIN_GAP_MS) and its event ring are MODULE state,
// shared by every test in a file — asserting "no report was sent" next to tests that legitimately
// send some would be reading someone else's mail. Vitest gives each file its own module registry,
// which is the isolation these assertions actually need.

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

// The same MediaSource stand-in the engine suites use: it accepts everything and grows its buffered
// range, and attaching a new one closes every previous one (the real supersession mechanism).
let sourceBuffers;
let mediaSources;
class FakeSourceBuffer extends EventTarget {
  constructor(mime) {
    super();
    this.mime = mime;
    this.endSec = 0;
    this.startSec = 0;
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
    mediaSources.forEach((ms) => { ms.readyState = "closed"; });
    mediaSources.push(this);
    this.readyState = "open";
    setTimeout(() => this.dispatchEvent(new Event("sourceopen")), 0);
  }
  static isTypeSupported() { return true; }
  addSourceBuffer(mime) {
    if (this.readyState !== "open") throw new Error("MediaSource is not open");
    const sb = new FakeSourceBuffer(mime);
    sourceBuffers.push(sb);
    return sb;
  }
  endOfStream() { this.readyState = "ended"; }
}

/**
 * The one knob this suite exists for: swallow the NEXT pause() aimed at the engine's element, i.e.
 * simulate a `destroy()` that ended the stream and walked away — which is exactly what the shipped
 * code did until 2026-08-13, and exactly the regression this reporter has to notice.
 */
let swallowNextMsePause;
let beacons;

beforeEach(() => {
  sourceBuffers = [];
  mediaSources = [];
  swallowNextMsePause = false;
  beacons = [];
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

  // play/pause keep `paused` truthful — the DOM shim never really plays, so a stub that only counted
  // calls would leave `paused` permanently true and the invariant could never read as violated.
  const setSounding = (el, on) => {
    Object.defineProperty(el, "paused", { value: !on, configurable: true });
  };
  window.HTMLMediaElement.prototype.play = vi.fn(function playStub() {
    setSounding(this, true);
    return Promise.resolve();
  });
  window.HTMLMediaElement.prototype.pause = vi.fn(function pauseStub() {
    if (swallowNextMsePause && this.dataset.deck === "mse") {
      swallowNextMsePause = false;   // one pause only: the BACKSTOP's own pause must still land
      return;
    }
    setSounding(this, false);
  });
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

  // setupTests.js installs a no-op beacon so no suite touches the network; this one wants to READ
  // what would have been sent, which is the documented way to override it.
  navigator.sendBeacon = vi.fn((url, blob) => {
    beacons.push({ url, blob });
    return true;
  });

  // Persisted queues AND the persisted diag ring leak between test files otherwise.
  window.localStorage.clear();
  window.localStorage.setItem("music.engine", "mse");
});

afterEach(() => { cleanup(); vi.clearAllMocks(); vi.unstubAllGlobals(); window.localStorage.clear(); });

async function flush(times = 24) {
  for (let i = 0; i < times; i++) {
    // eslint-disable-next-line no-await-in-loop
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  }
}

async function mountPlaying() {
  const view = render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
  const el = (deck) => view.container.querySelector(`audio[data-deck="${deck}"]`);
  await act(async () => { player.playTracks(TRACKS, 0); });
  await flush();
  return { el };
}

/** Seek somewhere the engine's buffer cannot reach — the detour that hands the track to a deck. */
async function detour(el) {
  const mse = el("mse");
  Object.defineProperty(mse, "currentTime", { value: 50, writable: true, configurable: true });
  sourceBuffers[0].startSec = 40;          // eviction took the front — where the seek wants to go
  await act(async () => { player.seek(5); });
  await flush(6);
}

/** Every beacon body, parsed. sendBeacon is handed a Blob, so the text has to be read back out. */
async function reports() {
  const out = [];
  for (const b of beacons) {
    // eslint-disable-next-line no-await-in-loop
    out.push(JSON.parse(await b.blob.text()));
  }
  return out;
}

describe("park:live — the invariant reporter", () => {
  it("fires when parking finds the engine's element still playing", async () => {
    const { el } = await mountPlaying();
    expect(el("mse").paused).toBe(false);

    // destroy() ends the stream and forgets to pause: the pre-2026-08-13 behaviour, and the shape of
    // any future regression. The backstop must NOTICE rather than quietly clean up after it.
    swallowNextMsePause = true;
    await detour(el);

    const sent = await reports();
    expect(sent).toHaveLength(1);
    expect(sent[0].summary).toMatch(/park found the engine's element still playing/);
    // Enough to reconstruct it: which deck the player thought was live, whether an engine object was
    // still around, and the element's own account of itself.
    expect(sent[0].summary).toMatch(/deckRef=a/);
    expect(sent[0].summary).toMatch(/engineAlive=false/);
    expect(sent[0].summary).toMatch(/t=\d+s/);
    expect(sent[0].trackId).toBe(1);

    // Recorded with diagnostics OFF — that is what putting it in the ALWAYS set buys, and without it
    // the beacon would carry a ring with no evidence in it.
    const ring = sent[0].events.filter((e) => e.event === "park:live");
    expect(ring).toHaveLength(1);
    expect(ring[0].data.deckRef).toBe("a");
    expect(ring[0].data.audio.src).toEqual(expect.any(String));

    // …and the backstop still did its job: noticing is not a substitute for silencing.
    expect(el("mse").paused).toBe(true);
  });

  it("stays silent through a healthy detour — zero noise when the invariant holds", async () => {
    const { el } = await mountPlaying();
    await detour(el);

    expect(el("mse").paused).toBe(true);
    expect(await reports()).toEqual([]);
    expect(navigator.sendBeacon).not.toHaveBeenCalled();
  });

  it("stays silent through an ordinary engine takeover, where a live deck is EXPECTED", async () => {
    // The symmetric case, deliberately not tripwired: parkDecks() is the PRIMARY silencer when the
    // engine takes the floor over, so a sounding deck there is the normal state of affairs. A skip
    // restarts the engine and runs exactly that path.
    const { el } = await mountPlaying();
    await act(async () => { player.playAt(1); });
    await flush();

    expect(await reports()).toEqual([]);
    expect([el("a"), el("b")].every((d) => d.paused !== false)).toBe(true);
  });

  it("reports once per session, not once per park", async () => {
    // The condition is a STUCK STATE, not an event: an element playing where it must not be is still
    // playing at the next park too. Unlatched, one fault would spend the whole report budget.
    const { el } = await mountPlaying();
    swallowNextMsePause = true;
    await detour(el);
    expect(await reports()).toHaveLength(1);

    // Another takeover, violating the invariant again.
    await act(async () => { player.playAt(2); });
    await flush();
    swallowNextMsePause = true;
    await act(async () => { player.clearQueue(); });
    await flush(4);

    expect(await reports()).toHaveLength(1);
  });
});
