import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── The queue must survive a reload ─────────────────────────────────────────
// `enabled` is `!!userData?.hasPassword`, and App.js starts `userData` at null — so the provider's
// FIRST render is always disabled, whoever is logged in. The restore effect bails on that render,
// and the persist effect (which had no such guard) then saw an empty queue and deleted the stored
// one before it could ever be read back. The queue could therefore never survive a refresh, which
// is exactly what a listener hits after the player wedges and they reload to get out of it.

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
const QUEUE_KEY = "music.queue";

const SAVED = {
  queue: [
    { id: 1, title: "One", artist: "A", album: "X", durationSec: 100 },
    { id: 2, title: "Two", artist: "A", album: "X", durationSec: 100 },
  ],
  index: 1,
};

let player;
function Probe() {
  player = useMusicPlayer();
  return null;
}

beforeEach(() => {
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
  window.HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve());
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
  window.localStorage.clear();
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe("queue persistence across a reload", () => {
  it("survives the disabled first render that App.js always produces", async () => {
    window.localStorage.setItem(QUEUE_KEY, JSON.stringify(SAVED));

    // Render 1: userData hasn't arrived, so streaming is off — the real mount order.
    const view = render(<MusicPlayerProvider enabled={false}><Probe /></MusicPlayerProvider>);
    await act(async () => {});

    // The stored queue must still be there to be read once auth resolves.
    expect(window.localStorage.getItem(QUEUE_KEY)).not.toBeNull();

    // Render 2: userData arrives, hasPassword true.
    await act(async () => {
      view.rerender(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
    });

    expect(player.queue.map((t) => t.id)).toEqual([1, 2]);
    expect(player.current?.id).toBe(2);
    expect(player.playing).toBe(false); // restored PAUSED (§Phase 7)
  });

  it("still restores when the provider is enabled from the very first render", async () => {
    window.localStorage.setItem(QUEUE_KEY, JSON.stringify(SAVED));
    render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
    await act(async () => {});
    expect(player.queue.map((t) => t.id)).toEqual([1, 2]);
    expect(window.localStorage.getItem(QUEUE_KEY)).not.toBeNull();
  });

  it("keeps persisting after a restore, so the next reload sees the latest position", async () => {
    window.localStorage.setItem(QUEUE_KEY, JSON.stringify(SAVED));
    render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
    await act(async () => {});
    await act(async () => { player.playAt(0); });
    expect(JSON.parse(window.localStorage.getItem(QUEUE_KEY)).index).toBe(0);
  });

  it("clears storage when the listener actually empties the queue", async () => {
    window.localStorage.setItem(QUEUE_KEY, JSON.stringify(SAVED));
    render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);
    await act(async () => {});
    await act(async () => { player.stop(); });
    expect(window.localStorage.getItem(QUEUE_KEY)).toBeNull();
  });

  it("does not wipe a stored queue for an account that can't stream at all", async () => {
    window.localStorage.setItem(QUEUE_KEY, JSON.stringify(SAVED));
    render(<MusicPlayerProvider enabled={false}><Probe /></MusicPlayerProvider>);
    await act(async () => {});
    expect(window.localStorage.getItem(QUEUE_KEY)).not.toBeNull();
  });
});
