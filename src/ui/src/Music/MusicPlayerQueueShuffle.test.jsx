import { render, act, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import { MusicPlayerProvider, useMusicPlayer } from "./MusicPlayerContext";

// ── Shuffling the queue you are already listening to ────────────────────────
// shuffleTracks is "play this list, shuffled" and starts over at track 0. shuffleQueue is the queue
// flyout's control and must NOT do that: the listener likes what is playing and wants the rest of it
// reordered. The whole correctness of it is one property — the pivot track does not move — because
// `current` is `queue[index]` and the load effect keys on `current.id`, so a pivot that moved would
// re-load (and restart) the song the user is in the middle of.

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

const TRACKS = Array.from({ length: 12 }, (_, i) => ({
  id: i + 1, title: `T${i + 1}`, artist: "A", album: "X", durationSec: 100,
}));

let player;
function Probe() {
  player = useMusicPlayer();
  return null;
}

const mount = () => render(<MusicPlayerProvider enabled><Probe /></MusicPlayerProvider>);

beforeEach(() => {
  api.getMusicCapabilities.mockReturnValue(ok({ transcodeEnabled: true }));
  api.getMusicFavorites.mockReturnValue(ok({ trackIds: [] }));
  api.startMusicTrack.mockImplementation((id) => ok({ trackId: id, url: `https://gw/${id}`, channels: 2 }));
  window.HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.pause = vi.fn();
  global.fetch = vi.fn((u) => Promise.resolve({
    ok: true,
    headers: { get: () => "1024" },
    blob: () => Promise.resolve({ size: 1024, __url: u }),
  }));
  global.URL.createObjectURL = (b) => `blob:${b.__url}`;
  global.URL.revokeObjectURL = vi.fn();
  // Persisted queues leak between test files if this is skipped — the player would restore someone
  // else's queue over the one under test.
  window.localStorage.clear();
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

async function startAt(i) {
  mount();
  await act(async () => {});
  await act(async () => { player.playTracks(TRACKS, i); });
  return player;
}

describe("shuffleQueue", () => {
  it("leaves the playing track exactly where it is", async () => {
    await startAt(3);
    const before = player.current.id;
    await act(async () => { player.shuffleQueue(); });

    expect(player.index).toBe(3);
    expect(player.current.id).toBe(before);
    expect(player.queue[3].id).toBe(before);
  });

  it("leaves what has already been played alone and reorders only what is ahead", async () => {
    await startAt(3);
    const head = player.queue.slice(0, 4).map((t) => t.id);
    const tailBefore = player.queue.slice(4).map((t) => t.id);

    await act(async () => { player.shuffleQueue(); });

    expect(player.queue.slice(0, 4).map((t) => t.id)).toEqual(head);
    // Same set, still every track exactly once — a lossy shuffle is invisible by eye.
    expect([...player.queue.slice(4)].map((t) => t.id).sort((a, b) => a - b))
      .toEqual([...tailBefore].sort((a, b) => a - b));
    expect(player.queue).toHaveLength(TRACKS.length);
  });

  it("actually changes the order it is asked to change", async () => {
    // Not a strict guarantee of any one call, so it is asserted over several: a shuffle that
    // returned the input would pass every other test in this file.
    await startAt(0);
    const original = player.queue.map((t) => t.id).join(",");
    let moved = false;
    for (let attempt = 0; attempt < 8 && !moved; attempt += 1) {
      // eslint-disable-next-line no-await-in-loop
      await act(async () => { player.shuffleQueue(); });
      moved = player.queue.map((t) => t.id).join(",") !== original;
    }
    expect(moved).toBe(true);
  });

  it("keeps the stored copy in step, because the queue outlives the session", async () => {
    await startAt(2);
    await act(async () => { player.shuffleQueue(); });

    const saved = JSON.parse(window.localStorage.getItem(QUEUE_KEY));
    expect(saved.queue.map((t) => t.id)).toEqual(player.queue.map((t) => t.id));
    expect(saved.index).toBe(2);
  });

  it("is a no-op on the last track, where there is nothing left to reorder", async () => {
    await startAt(TRACKS.length - 1);
    const before = player.queue;
    await act(async () => { player.shuffleQueue(); });
    // Same ARRAY, not merely an equal one: returning `q` unchanged is what stops a pointless
    // re-render and a pointless localStorage write.
    expect(player.queue).toBe(before);
  });
});
