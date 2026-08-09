import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── Stranded at the track boundary ──────────────────────────────────────────
// The hand-off (MusicPlayerHandoff.test.js) only covers the boundary where the next track's URL was
// already in hand. When it ISN'T — the prefetch hadn't landed yet — `ended` falls back to an
// ordinary load, and that load is a round trip starting at the exact moment the page stops playing
// audio and therefore loses its licence to keep running in the background. On a phone with the
// screen off the fetch simply doesn't land.
//
// Nothing fails in that state, which is why it produced NO error message: the element never errors,
// it just sits holding the PREVIOUS track's spent URL. The old code made it unrecoverable by
// clearing the wake-retry flag immediately before starting that load, so picking the phone up did
// nothing and the play button silently rejected on the dead source. Only a reload got out of it.

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
];

let player;
function Probe() {
  player = useMusicPlayer();
  return null;
}

let hidden;
function setHidden(v) {
  hidden = v;
  document.dispatchEvent(new Event("visibilitychange"));
}

let playSpy;

beforeEach(() => {
  hidden = false;
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
  Object.defineProperty(document, "visibilityState", {
    configurable: true,
    get: () => (hidden ? "hidden" : "visible"),
  });
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
  playSpy = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.play = playSpy;
  window.HTMLMediaElement.prototype.pause = vi.fn();
  window.localStorage.clear();
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function mountPlaying() {
  const view = render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
  const audio = view.container.querySelector("audio");
  await act(async () => { player.playTracks(TRACKS, 0); });
  return { view, audio };
}

/** A Stream/Start that never answers — a fetch issued by a renderer that then froze. */
function neverResolves() {
  api.startMusicTrack.mockImplementation(() => new Promise(() => {}));
}

describe("a boundary whose load never lands", () => {
  it("leaves the element on the old track and reports no error", async () => {
    const { audio } = await mountPlaying();
    expect(audio.src).toBe("https://gw/1");

    setHidden(true);
    neverResolves();
    await act(async () => { audio.dispatchEvent(new Event("ended")); });

    // The queue advanced, but nothing has arrived to play — and nothing errored, which is exactly
    // why the listener saw no message.
    expect(player.current.id).toBe(2);
    expect(audio.src).toBe("https://gw/1");
    expect(player.error).toBeFalsy();
  });

  it("re-drives the load when the phone is picked up, instead of replaying a spent URL", async () => {
    const { audio } = await mountPlaying();
    setHidden(true);
    neverResolves();
    await act(async () => { audio.dispatchEvent(new Event("ended")); });

    // The phone comes back. The load for track 2 is the step that never happened, so THAT is what
    // has to be retried — not play() on track 1's finished source.
    api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
    await act(async () => { setHidden(false); });

    expect(api.startMusicTrack).toHaveBeenLastCalledWith(2);
    expect(audio.src).toBe("https://gw/2");
  });

  it("makes the play button load the current track rather than silently reject", async () => {
    const { audio } = await mountPlaying();
    setHidden(true);
    neverResolves();
    await act(async () => { audio.dispatchEvent(new Event("ended")); });

    api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
    playSpy.mockClear();
    await act(async () => { player.toggle(); });

    expect(api.startMusicTrack).toHaveBeenLastCalledWith(2);
    expect(audio.src).toBe("https://gw/2");
  });

  it("still just plays when the element already holds the current track", async () => {
    const { audio } = await mountPlaying();
    Object.defineProperty(audio, "paused", { value: true, configurable: true });
    const startsBefore = api.startMusicTrack.mock.calls.length;
    playSpy.mockClear();

    await act(async () => { player.toggle(); });

    // No re-mint: pausing and resuming the track you are on must not cost a round trip.
    expect(api.startMusicTrack).toHaveBeenCalledTimes(startsBefore);
    expect(playSpy).toHaveBeenCalledTimes(1);
  });

  it("does not arm a wake retry for a load started while the page is visible", async () => {
    const { audio } = await mountPlaying();
    // Visible the whole time, and the element is playing: a later visibilitychange must not fire a
    // stray play() at a listener who deliberately hit pause.
    await act(async () => { audio.dispatchEvent(new Event("play")); });
    await act(async () => { player.toggle(); });   // pause
    playSpy.mockClear();
    await act(async () => { setHidden(true); });
    await act(async () => { setHidden(false); });
    expect(playSpy).not.toHaveBeenCalled();
  });
});
