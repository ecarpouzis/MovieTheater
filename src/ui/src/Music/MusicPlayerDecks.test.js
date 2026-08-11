import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── The A/B decks ───────────────────────────────────────────────────────────
// Swapping `src` on a single element is the one thing a backgrounded page cannot do: the element
// drops to HAVE_NOTHING, the page stops playing audio, and with it goes the licence that let the
// page run at all — so play() is refused and the album stops at the boundary. Both Chrome and
// Firefox did this, which is what marked it as architectural rather than a browser quirk.
//
// So the next track is buffered on the OTHER element while this one still plays, and the boundary
// is a flip: no src assignment, no fetch, nothing to be refused for want of data.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const { api } = vi.hoisted(() => ({
  api: {
    getMusicCapabilities: vi.fn(),
    getMusicFavorites: vi.fn(),
    startMusicTrack: vi.fn(),
  },
}));

vi.mock("../MovieAPI", () => ({ MovieAPI: api }));
vi.mock("./MusicMiniPlayer", () => ({ default: () => null }));

const ok = (body) => Promise.resolve({ ok: true, json: () => Promise.resolve(body) });

const TRACKS = [
  { id: 1, title: "One", artist: "A", album: "X", durationSec: 100 },
  { id: 2, title: "Two", artist: "A", album: "X", durationSec: 100 },
  { id: 3, title: "Three", artist: "A", album: "X", durationSec: 100 },
];

let player;
function Probe() {
  player = useMusicPlayer();
  return null;
}

function fakePlayhead(audio, { currentTime, duration }) {
  Object.defineProperty(audio, "currentTime", { value: currentTime, configurable: true });
  Object.defineProperty(audio, "duration", { value: duration, configurable: true });
}

let playSpy;

beforeEach(() => {
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2, sizeBytes: 4 * 1024 * 1024 }));
  playSpy = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.play = playSpy;
  window.HTMLMediaElement.prototype.pause = vi.fn();
  // The player now DOWNLOADS a track before playing it, so tests need a fetch and object-URL
  // environment. createObjectURL echoes the URL the bytes came from, so a deck's src says which
  // track it is holding — `blob:https://gw/2` reads as "track 2's bytes, in memory".
  global.fetch = vi.fn((u) => Promise.resolve({
    ok: true,
    headers: { get: () => "1024" },
    blob: () => Promise.resolve({ size: 1024, __url: u }),
  }));
  global.URL.createObjectURL = (b) => `blob:${b.__url}`;
  global.URL.revokeObjectURL = vi.fn();
  window.HTMLMediaElement.prototype.load = vi.fn();
  window.localStorage.clear();   // a persisted queue must not leak between tests
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function mountPlaying() {
  const view = render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
  const decks = () => [...view.container.querySelectorAll("audio")];
  await act(async () => { player.playTracks(TRACKS, 0); });
  return { view, decks, live: () => player.audioRef.current };
}

/** Let the current track get under way, which is what triggers downloading the next one. */
async function runToPrefetch(el) {
  await act(async () => {
    fakePlayhead(el, { currentTime: 10, duration: 100 });
    el.dispatchEvent(new Event("timeupdate"));
  });
  await act(async () => {});   // let the download settle
}

describe("A/B decks", () => {
  it("renders two decks and starts on the first", async () => {
    const { decks, live } = await mountPlaying();
    expect(decks()).toHaveLength(2);
    expect(live().dataset.deck).toBe("a");
    expect(live().src).toBe("https://gw/1");
  });

  it("buffers the next track on the IDLE deck while this one is still playing", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const [a, b] = decks();
    expect(a.src).toBe("https://gw/1");        // still playing, straight off the gateway
    // A file that FITS Chrome's media buffer is preloaded by the ELEMENT, not by script. A JS
    // fetch is the first thing a backgrounded phone stops running, so it is used only where it
    // earns its keep — a file too big for the buffer, see the test below.
    expect(global.fetch).not.toHaveBeenCalledWith("https://gw/2", expect.anything());
    expect(b.src).toBe("https://gw/2");         // …and track 2 is already buffering natively
  });

  it("crosses the boundary as a FLIP — no fetch and no src assignment", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const startsBefore = api.startMusicTrack.mock.calls.length;
    const [, b] = decks();
    playSpy.mockClear();

    // No await: the next track must be live by the time the handler returns.
    live().dispatchEvent(new Event("ended"));

    expect(player.audioRef.current).toBe(b);          // deck flipped
    expect(player.audioRef.current.src).toBe("https://gw/2"); // already buffered on the idle deck
    expect(playSpy).toHaveBeenCalledTimes(1);
    expect(api.startMusicTrack).toHaveBeenCalledTimes(startsBefore); // nothing fetched at the boundary

    await act(async () => {});
    expect(player.current.id).toBe(2);
    expect(api.startMusicTrack).toHaveBeenCalledTimes(startsBefore); // and no re-load after either
  });

  it("alternates decks across two boundaries, so a whole album can play through", async () => {
    const { decks, live } = await mountPlaying();
    const [a, b] = decks();

    await runToPrefetch(a);
    await act(async () => { a.dispatchEvent(new Event("ended")); });
    expect(player.audioRef.current).toBe(b);
    expect(player.current.id).toBe(2);

    await runToPrefetch(b);
    await act(async () => { b.dispatchEvent(new Event("ended")); });
    expect(player.audioRef.current).toBe(a);          // back to deck A
    expect(player.current.id).toBe(3);
    expect(a.src).toBe("https://gw/3");
  });

  it("needs NO network at the boundary — the bytes are already here", async () => {
    const { live } = await mountPlaying();
    await runToPrefetch(live());
    global.fetch.mockClear();
    api.startMusicTrack.mockClear();

    live().dispatchEvent(new Event("ended"));

    // This is the property the whole design exists for: a sleeping phone cannot be asked for the
    // network, so crossing a boundary must not require it.
    expect(global.fetch).not.toHaveBeenCalled();
    expect(api.startMusicTrack).not.toHaveBeenCalled();
  });

  it("ignores events from the idle deck — its loads are preparation, not playback", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const [, b] = decks();

    // The idle deck failing to buffer must not tell the listener their track is broken.
    await act(async () => { b.dispatchEvent(new Event("error")); });
    expect(player.error).toBeFalsy();

    // Nor may its pause/ended events move the player's state.
    await act(async () => { b.dispatchEvent(new Event("ended")); });
    expect(player.current.id).toBe(1);
  });

  it("falls back to a real load when the idle deck never buffered", async () => {
    const { live } = await mountPlaying();
    // No prefetch ran, so nothing is on the other deck.
    await act(async () => { live().dispatchEvent(new Event("ended")); });
    expect(player.current.id).toBe(2);
    expect(player.audioRef.current.src).toBe("https://gw/2");
  });

  it("never STREAMS a file over Chrome's buffer cap — it downloads it first", async () => {
    // 40 MB, like the Caravan Palace FLAC that died mid-song. Chrome's buffer tops out at
    // 16 MiB - 32 KiB, so streaming this guarantees a re-request for `bytes=16744448-` part-way
    // through — which is exactly what Caddy's access log caught, and exactly what a phone with its
    // screen off cannot answer.
    api.startMusicTrack.mockImplementation((id) =>
      ok({ trackId: id, url: `https://gw/${id}`, channels: 2, sizeBytes: 42163278 }));

    const view = render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
    await act(async () => { player.playTracks(TRACKS, 0); });
    await act(async () => {});

    expect(global.fetch).toHaveBeenCalledWith("https://gw/1", expect.anything());
    expect(player.audioRef.current.src).toBe("blob:https://gw/1"); // bytes in hand, not a stream
    view.unmount();
  });

  it("still streams a small file, so a 6 MB mp3 starts instantly", async () => {
    const { live } = await mountPlaying();   // 4 MB in the default mock
    expect(live().src).toBe("https://gw/1");
    expect(global.fetch).not.toHaveBeenCalledWith("https://gw/1", expect.anything());
  });

  it("keeps both decks at the same volume, so a flip is never a jump in loudness", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    await act(async () => { player.setVolume(0.25); });
    const [a, b] = decks();
    expect(a.volume).toBeCloseTo(0.25);
    expect(b.volume).toBeCloseTo(0.25);
  });
});

// ── Who fetches, and the muted pre-roll ─────────────────────────────────────
// The boundary kept failing on a sleeping phone even with two decks, because the next track was
// being downloaded by a JS `fetch()` — and JS is exactly what a backgrounded page stops being
// allowed to run. The element's own buffering is not script; it is the same pipeline that keeps
// the current track playing with the screen off. So the fetching is handed to the element, and the
// next deck is started (muted) before the boundary so the page never has an audio-less instant.
describe("the boundary must not depend on JavaScript", () => {
  it("lets the ELEMENT fetch when the page is hidden, even for a file over the buffer cap", async () => {
    api.startMusicTrack.mockImplementation((id) =>
      ok({ trackId: id, url: `https://gw/${id}`, channels: 2, sizeBytes: 42163278 }));
    const { decks, live } = await mountPlaying();
    Object.defineProperty(document, "hidden", { value: true, configurable: true });
    global.fetch.mockClear();

    await runToPrefetch(live());

    const [, b] = decks();
    // A 40 MB file is exactly the case the in-memory download exists for — but hidden, that fetch
    // is the thing that does not land, and an empty deck at the boundary is the bug itself.
    expect(global.fetch).not.toHaveBeenCalledWith("https://gw/2", expect.anything());
    expect(b.src).toBe("https://gw/2");
    Object.defineProperty(document, "hidden", { value: false, configurable: true });
  });

  it("still downloads a too-big file in script while the page is VISIBLE", async () => {
    api.startMusicTrack.mockImplementation((id) =>
      ok({ trackId: id, url: `https://gw/${id}`, channels: 2, sizeBytes: 42163278 }));
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const [, b] = decks();
    // Awake, the download still earns its keep: a file over the buffer cap is evicted and
    // re-requested mid-song, which a phone that falls asleep later cannot service.
    expect(global.fetch).toHaveBeenCalledWith("https://gw/2", expect.anything());
    expect(b.src).toBe("blob:https://gw/2");
  });

  it("starts the next deck playing MUTED before the boundary", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const [, b] = decks();
    playSpy.mockClear();

    await act(async () => {
      fakePlayhead(live(), { currentTime: 95, duration: 100 });   // inside PREROLL_LEAD_SEC
      live().dispatchEvent(new Event("timeupdate"));
    });

    expect(playSpy).toHaveBeenCalledTimes(1);   // the IDLE deck was started
    expect(b.muted).toBe(true);                 // silently
  });

  it("crosses a pre-rolled boundary with no play() at all — it is already playing", async () => {
    const { decks, live } = await mountPlaying();
    await runToPrefetch(live());
    const [, b] = decks();
    await act(async () => {
      fakePlayhead(live(), { currentTime: 95, duration: 100 });
      live().dispatchEvent(new Event("timeupdate"));
    });
    // The pre-rolled deck is playing; happy-dom won't flip `paused` for us, so say so explicitly.
    Object.defineProperty(b, "paused", { value: false, configurable: true });
    playSpy.mockClear();

    live().dispatchEvent(new Event("ended"));

    expect(player.audioRef.current).toBe(b);
    expect(b.muted).toBe(false);                 // unmuted at the flip…
    expect(playSpy).not.toHaveBeenCalled();      // …and never asked to start, so never refusable
  });
});
