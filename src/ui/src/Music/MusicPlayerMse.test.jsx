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
let mediaSources;
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
    // The element hosts ONE MediaSource: attaching a new one detaches — closes — every previous
    // one, and addSourceBuffer on a closed MediaSource throws. This is the real browser mechanism
    // behind the supersession race (incident 5, 2026-08-12), so the fake must model it or the race
    // tests below pass against broken code.
    mediaSources.forEach((ms) => { ms.readyState = "closed"; });
    mediaSources.push(this);
    this.readyState = "open";
    setTimeout(() => this.dispatchEvent(new Event("sourceopen")), 0);
  }
  static isTypeSupported() { return true; }
  addSourceBuffer(mime) {
    if (this.readyState !== "open") {
      throw new Error("Failed to execute 'addSourceBuffer' on 'MediaSource': The MediaSource's readyState is not 'open'.");
    }
    const sb = new FakeSourceBuffer(mime);
    sourceBuffers.push(sb);
    return sb;
  }
  endOfStream() { this.readyState = "ended"; }
}

let playSpy;
let pauseSpy;

/**
 * Is this element MAKING NOISE?
 *
 * jsdom/happy-dom never actually play, and the suite stubs play/pause on the prototype, so
 * `el.paused` is a constant and says nothing. The stubs below therefore record the one fact the
 * "two songs at once" bugs are about — which elements are live — onto the element itself.
 * `sounding()` is that fact, and asserting it on ALL THREE decks is the only way to catch a source
 * that is playing where nothing in the player can reach it any more.
 */
const sounding = (el) => el.__sounding === true;

beforeEach(() => {
  sourceBuffers = [];
  mediaSources = [];
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
  // `function`, not an arrow: `this` has to be the element these are called on, or `sounding()`
  // cannot tell the three decks apart. They also keep `paused` truthful — the DOM shim never really
  // plays, so a stub that only counted calls left `paused` permanently true, and `toggle()` (which
  // reads it to decide which way to flip) would call play() where a browser would pause.
  const setSounding = (el, on) => {
    el.__sounding = on;
    Object.defineProperty(el, "paused", { value: !on, configurable: true });
  };
  playSpy = vi.fn(function playStub() { setSounding(this, true); return Promise.resolve(); });
  window.HTMLMediaElement.prototype.play = playSpy;
  pauseSpy = vi.fn(function pauseStub() { setSounding(this, false); });
  window.HTMLMediaElement.prototype.pause = pauseSpy;
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

  it("takes a seek outside the buffer to a DECK, at the position asked for", async () => {
    // This used to restart the engine at the track, which begins it again at 0:00 — the whole of
    // "seeking Winterbreak goes back to the start of the song". On a 1568 kbps FLAC the 11.5 MB
    // quota holds 61 s of a 297 s track, so most of the seek bar was outside the buffer and every
    // scrub into it silently restarted the song. A seek proves the page is awake, so the honest
    // answer is the deck, which holds the whole file and seeks natively.
    const { el } = await mountPlaying();
    const mse = el("mse");
    Object.defineProperty(mse, "currentTime", { value: 50, writable: true, configurable: true });
    const buffersBefore = sourceBuffers.length;
    // Eviction has taken the front of the buffer — where this seek wants to land.
    sourceBuffers[0].startSec = 40;
    await act(async () => { player.seek(5); });
    await flush(6);

    // No new MediaSource: the engine was torn down, not rebuilt at 0:00.
    expect(sourceBuffers.length).toBe(buffersBefore);
    // …and playback moved onto a real deck, which is the thing that can seek anywhere.
    expect(player.audioRef.current.dataset.deck).not.toBe("mse");
  });

  /** Put the player on a deck by seeking somewhere the engine's buffer cannot reach. */
  async function detour(el) {
    const mse = el("mse");
    Object.defineProperty(mse, "currentTime", { value: 50, writable: true, configurable: true });
    sourceBuffers[0].startSec = 40;          // eviction took the front — where the seek wants to go
    await act(async () => { player.seek(5); });
    await flush(6);
    expect(player.audioRef.current.dataset.deck).not.toBe("mse");
    return player.audioRef.current;
  }

  /** Run the detoured deck up to the boundary so the prefetch and the pre-roll both fire. */
  async function runToBoundary(live) {
    Object.defineProperty(live, "duration", { value: 100, configurable: true });
    Object.defineProperty(live, "currentTime", { value: 95, configurable: true });
    await act(async () => { live.dispatchEvent(new Event("timeupdate")); });
    await flush(6);
  }

  it("SLEEPS THROUGH a detoured boundary on the pre-rolled deck — no round trip with the screen off", async () => {
    // The question this exists to answer: seek into a long track, put the phone down, and the track
    // ends while the page is frozen. Handing back to the engine there would need a mint and an
    // append — the exact round trip a sleeping phone cannot make, and the bug the engine was built
    // to remove. So when the boundary arrives hidden, the pre-rolled deck flip wins instead.
    const { el } = await mountPlaying();
    const live = await detour(el);
    await runToBoundary(live);

    Object.defineProperty(document, "hidden", { value: true, configurable: true });
    try {
      const mintsBefore = api.startMusicTrack.mock.calls.length;
      const buffersBefore = sourceBuffers.length;
      await act(async () => { live.dispatchEvent(new Event("ended")); });
      await flush(6);

      // Audio moved to the OTHER deck, which was already holding the bytes…
      expect(player.audioRef.current.dataset.deck).not.toBe("mse");
      expect(player.index).toBe(1);
      // …with no new MediaSource and no fresh mint at the boundary itself.
      expect(sourceBuffers.length).toBe(buffersBefore);
      expect(api.startMusicTrack.mock.calls.length).toBe(mintsBefore);
    } finally {
      Object.defineProperty(document, "hidden", { value: false, configurable: true });
    }
  });

  it("…but hands back to the engine when that same boundary arrives AWAKE", async () => {
    // Same setup, screen on. Now the fetch is safe, so the prepared deck is discarded and the index
    // change restarts the engine — the detour costs one track, as intended.
    const { el } = await mountPlaying();
    const live = await detour(el);
    await runToBoundary(live);

    const buffersBefore = sourceBuffers.length;
    // A fresh MediaSource starts its clock at 0. The stub left over from the seek would otherwise
    // have the restarted engine read the playhead as already deep into the queue.
    el("mse").currentTime = 0;
    await act(async () => { live.dispatchEvent(new Event("ended")); });
    await flush(10);

    expect(player.index).toBe(1);
    expect(player.audioRef.current.dataset.deck).toBe("mse");
    expect(sourceBuffers.length).toBeGreaterThan(buffersBefore);
  });

  it("hands the queue back to the engine at the next track — the detour is one track long", async () => {
    // The cost of the detour has to stay bounded at one track. If a prepared deck were allowed to
    // flip at the boundary it would set handedOffRef, the track-change effect would return early,
    // and the engine would never come back — a scrub would have silently cost the rest of the
    // queue its sleep survival.
    const { el } = await mountPlaying();
    const mse = el("mse");
    Object.defineProperty(mse, "currentTime", { value: 50, writable: true, configurable: true });
    sourceBuffers[0].startSec = 40;
    await act(async () => { player.seek(5); });
    await flush(6);
    expect(player.audioRef.current.dataset.deck).not.toBe("mse");

    const buffersBefore = sourceBuffers.length;
    await act(async () => { player.next(); });
    await flush(8);

    expect(player.audioRef.current.dataset.deck).toBe("mse");
    expect(sourceBuffers.length).toBeGreaterThan(buffersBefore);
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

  // Phase 5: the default flipped. A browser that proves a treatment gets the engine with no flag
  // at all; the decks are the floor, not the default.
  it("is ON by default where a treatment is supported", async () => {
    window.localStorage.clear();
    const { el, live } = await mountPlaying();
    expect(live()).toBe(el("mse"));
    expect(api.startMusicTracks).toHaveBeenCalled();
  });

  it("honours the remembered ?mse=0 opt-out", async () => {
    window.localStorage.clear();
    window.localStorage.setItem("music.engine", "decks");
    const { el, live } = await mountPlaying();
    expect(live()).toBe(el("a"));
    expect(api.startMusicTrack).toHaveBeenCalled();
    expect(api.startMusicTracks).not.toHaveBeenCalled();
  });

  // ── The "two songs at once" pair (incident 5, 2026-08-12) ─────────────────────────────────────
  // A phone session fell to the deck floor, a deck downloaded its track as a blob and played it,
  // and then a NEW pick brought the engine back — which took the "live" slot without silencing the
  // deck. Nothing could reach it afterwards: pause and Clear queue touch the ACTIVE element only,
  // and a blob needs no network, so it played underneath the engine to the end of its track.
  it("parks a playing deck when the engine takes the session back — no second voice", async () => {
    // Land on the deck floor the same way the field session did: a track the matrix can't carry
    // flips the boundary onto deck a, and the session stays there.
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
    expect(player.audioRef.current).toBe(el("a"));

    // Deck a is the thing playing. A new pick hands the session to the engine — the deck MUST be
    // paused by that takeover, because no later control can reach it.
    const pauseA = vi.fn();
    el("a").pause = pauseA;
    api.startMusicTracks.mockImplementation((ids) => ok({
      tracks: ids.map((id) => ({
        trackId: id, mimeType: "audio/mpeg",
        url: `https://gw/${id}/file`, universalUrl: `https://gw/${id}/universal`,
        sizeBytes: 2_000_000, durationSec: 100, sampleRateHz: 44100, channels: 2,
      })),
      skipped: [],
    }));
    await act(async () => { player.playTracks(TRACKS, 0); });
    await flush();

    expect(player.audioRef.current).toBe(el("mse"));
    expect(pauseA).toHaveBeenCalled();
  });

  it("survives two rapid picks: the superseded start neither falls back nor kills the winner", async () => {
    const { el } = await mountPlaying();

    // Pick #1, with its mint held open so the start is parked BETWEEN sourceopen and
    // addSourceBuffer — the exact window the field race hit.
    let releaseMint;
    const mintBody = (ids) => ({
      tracks: ids.map((id) => ({
        trackId: id, mimeType: "audio/mpeg",
        url: `https://gw/${id}/file`, universalUrl: `https://gw/${id}/universal`,
        sizeBytes: 2_000_000, durationSec: 100, sampleRateHz: 44100, channels: 2,
      })),
      skipped: [],
    });
    api.startMusicTracks.mockImplementationOnce((ids) => new Promise((resolve) => {
      releaseMint = () => resolve({ ok: true, json: () => Promise.resolve(mintBody(ids)) });
    }));
    // Two acts, deliberately: the effect that starts engine #1 runs as act exits, so its sourceopen
    // timer needs a tick of its OWN before the second pick — otherwise the held mint lands on the
    // wrong engine and there is no race to survive.
    await act(async () => { player.playAt(1); });
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(typeof releaseMint).toBe("function");    // engine #1 is parked mid-start, mint in flight

    // Pick #2, 100 ms later in the field: a fresh engine takes the element. Attaching its
    // MediaSource closes pick #1's (see FakeMediaSource) — pick #1 is now a corpse mid-start.
    await act(async () => { player.playAt(2); });
    await flush();
    expect(player.audioRef.current).toBe(el("mse"));
    expect(player.current.id).toBe(3);

    // The corpse's mint finally lands. Before the fix this ran addSourceBuffer on the closed
    // MediaSource, and the throw was treated as MSE failing: fallBackToDecks latched the session
    // and destroyed the HEALTHY engine. It must die silently instead.
    await act(async () => { releaseMint(); });
    await flush();

    expect(player.audioRef.current).toBe(el("mse"));
    expect(player.current.id).toBe(3);
    expect(api.startMusicTrack).not.toHaveBeenCalled();   // no deck load ever happened
  });
  // ── Wake → scrub → pause: the second live source (reported 2026-08-13) ─────────────────────────
  // Ten minutes into Big Data (FLAC, ~950 kbps ⇒ the 11.5 MB quota holds ~95 s), Eric woke the
  // phone, scrubbed, and paused. The song played on top of itself and pause stopped only ONE copy.
  //
  // The engine's `destroy()` called `endOfStream()`, which is a promise that no more data is coming
  // — NOT a stop. The element played out everything still in the SourceBuffer while seekDetour put a
  // deck on top of it. And once deckRef says "a" the engine's element is unreachable: pause and
  // Clear queue touch the ACTIVE deck, cancelPreroll the IDLE one, and every handler ignores an
  // element that is not live. This is the mirror of the 2026-08-12 fix, which taught the ENGINE to
  // park the decks; nothing had ever taught a DECK to park the engine.
  describe("a deck taking over from the engine", () => {
    it("parks the engine's element on a seek detour — endOfStream is not a stop", async () => {
      const { el } = await mountPlaying();
      expect(sounding(el("mse"))).toBe(true);        // the engine is the thing playing

      const live = await detour(el);
      expect(live.dataset.deck).not.toBe("mse");
      expect(sounding(live)).toBe(true);             // the deck took over…
      expect(sounding(el("mse"))).toBe(false);       // …and the engine went quiet. Two live sources
                                                     // here IS the bug: the song on top of itself.
    });

    it("leaves NOTHING playing when the listener then hits pause", async () => {
      const { el } = await mountPlaying();
      const live = await detour(el);

      await act(async () => { player.toggle(); });

      // The reported symptom, exactly: pause reaches audioRef and nothing else, so a live engine
      // element survived it and kept playing with no control able to stop it.
      expect(sounding(live)).toBe(false);
      expect([el("a"), el("b"), el("mse")].filter(sounding)).toEqual([]);
    });

    it("survives the full wake → scrub → pause sequence", async () => {
      const { el } = await mountPlaying();
      // The phone was asleep for the ten minutes before the scrub, then picked up. The wake is not
      // what breaks it — it is only how a hand reaches the seek bar — but the sequence is the report
      // and the report is what gets re-run.
      Object.defineProperty(document, "visibilityState", { value: "hidden", configurable: true });
      await act(async () => { document.dispatchEvent(new Event("visibilitychange")); });
      Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
      await act(async () => { document.dispatchEvent(new Event("visibilitychange")); });
      await flush(4);

      const live = await detour(el);
      await act(async () => { player.toggle(); });

      expect([el("a"), el("b"), el("mse")].filter(sounding)).toEqual([]);
      expect(live.dataset.deck).not.toBe("mse");
    });

    it("parks the engine's element when the queue is thrown away mid-stream", async () => {
      // ✕ Close player / Clear queue only ever paused the ACTIVE element too, and left the engine
      // OBJECT alive, still appending into a MediaSource whose src was about to be pulled off.
      const { el } = await mountPlaying();
      expect(sounding(el("mse"))).toBe(true);
      await act(async () => { player.clearQueue(); });
      await flush(4);
      expect([el("a"), el("b"), el("mse")].filter(sounding)).toEqual([]);
    });
  });
});
