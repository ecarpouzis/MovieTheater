import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── The track boundary, tested the only way that can catch the bug ───────────
// The failure this guards was invisible in every pure-function test: an album stopped at the end of
// a track whenever the phone's screen was off, and started the NEXT track when the phone was picked
// up. Nothing errored — the player simply awaited a fresh signed URL at the exact moment the page
// lost its licence to run in the background, and play() was refused for having no audio in flight.
//
// So the property under test is a TIMING one, and the assertions are deliberately made with no
// `await` between the `ended` event and the check: the next source must already be on the element
// and playing by the time the handler returns. An assertion after an await would pass just as
// happily against the old code, which is precisely why the bug shipped.

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
// The bar is a big tree of album art, visualizer and lyrics panes; none of it takes part in the
// hand-off, and mounting it would drag butterchurn into a unit test.
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

/** The element's playhead, which happy-dom leaves as inert defaults. */
function fakePlayhead(audio, { currentTime, duration }) {
  Object.defineProperty(audio, "currentTime", { value: currentTime, configurable: true });
  Object.defineProperty(audio, "duration", { value: duration, configurable: true });
}

let playSpy;

beforeEach(() => {
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
  playSpy = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.play = playSpy;
  window.HTMLMediaElement.prototype.pause = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function mountPlaying() {
  const view = render(<MusicPlayerProvider><Probe /></MusicPlayerProvider>);
  const audio = view.container.querySelector("audio");
  await act(async () => { player.playTracks(TRACKS, 0); });
  return { view, audio };
}

describe("gapless hand-off at the track boundary", () => {
  it("mints the next track's URL before the current one ends", async () => {
    const { audio } = await mountPlaying();
    expect(audio.src).toBe("https://gw/1");
    expect(api.startMusicTrack).toHaveBeenCalledTimes(1);

    // Mid-track: nothing to prefetch yet — a whole album's URLs minted at once would be pointless
    // work and would age the tokens for tracks nobody may reach.
    await act(async () => {
      fakePlayhead(audio, { currentTime: 10, duration: 100 });
      audio.dispatchEvent(new Event("timeupdate"));
    });
    expect(api.startMusicTrack).toHaveBeenCalledTimes(1);

    await act(async () => {
      fakePlayhead(audio, { currentTime: 80, duration: 100 });
      audio.dispatchEvent(new Event("timeupdate"));
    });
    expect(api.startMusicTrack).toHaveBeenLastCalledWith(2);
  });

  it("swaps to the next source SYNCHRONOUSLY on ended, with no round trip in between", async () => {
    const { audio } = await mountPlaying();
    await act(async () => {
      fakePlayhead(audio, { currentTime: 80, duration: 100 });
      audio.dispatchEvent(new Event("timeupdate"));
    });
    const startsBefore = api.startMusicTrack.mock.calls.length;
    playSpy.mockClear();

    // No await: this is the assertion that fails against a player which fetches on `ended`.
    audio.dispatchEvent(new Event("ended"));
    expect(audio.src).toBe("https://gw/2");
    expect(playSpy).toHaveBeenCalledTimes(1);
    expect(api.startMusicTrack).toHaveBeenCalledTimes(startsBefore);

    // …and the index bookkeeping that follows must not then re-load the track that is already
    // playing — a second Stream/Start here would put the silent gap straight back.
    await act(async () => {});
    expect(player.current.id).toBe(2);
    expect(api.startMusicTrack).toHaveBeenCalledTimes(startsBefore);
    expect(audio.src).toBe("https://gw/2");
  });

  it("falls back to the ordinary load when the prefetch never arrived", async () => {
    api.startMusicTrack.mockImplementationOnce((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }))
      .mockImplementationOnce(() => Promise.reject(new Error("offline")));
    const { audio } = await mountPlaying();
    await act(async () => {
      fakePlayhead(audio, { currentTime: 80, duration: 100 });
      audio.dispatchEvent(new Event("timeupdate"));
    });

    await act(async () => { audio.dispatchEvent(new Event("ended")); });
    expect(player.current.id).toBe(2);
    expect(audio.src).toBe("https://gw/2"); // minted the slow way, on the track change
  });

  it("does not hand off a URL the queue has moved past", async () => {
    const { audio } = await mountPlaying();
    await act(async () => {
      fakePlayhead(audio, { currentTime: 80, duration: 100 });
      audio.dispatchEvent(new Event("timeupdate"));
    });
    // The listener drops the track that was queued next. The prefetched URL is now for nothing.
    await act(async () => { player.removeAt(1); });
    await act(async () => { audio.dispatchEvent(new Event("ended")); });
    expect(audio.src).toBe("https://gw/1"); // end of queue: stays put, never jumps to a dropped track
  });
});
