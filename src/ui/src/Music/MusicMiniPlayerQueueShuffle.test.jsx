import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// ── The "Shuffle" control in the queue flyout ───────────────────────────────
// The queue had no way to reorder itself: the only shuffles on the site build a NEW queue (an album,
// a playlist), which is the wrong verb once you are already listening to one. Unlike Clear it is not
// destructive, so it fires on the first tap — but it does have to be honest about when it can do
// nothing, because a "Shuffle" that visibly does nothing reads as broken.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const track = (id) => ({ id, title: `T${id}`, artist: "A" });

const { player } = vi.hoisted(() => ({
  player: {
    queue: [],
    index: 0,
    current: { id: 1, title: "T1", artist: "A", album: "X" },
    playing: false,
    error: null,
    buffering: false,
    audioRef: { current: null },
    trackTime: 0,
    clearQueue: vi.fn(),
    shuffleQueue: vi.fn(),
    stop: vi.fn(),
    playAt: vi.fn(),
    removeAt: vi.fn(),
    next: vi.fn(),
    prev: vi.fn(),
    toggle: vi.fn(),
    seek: vi.fn(),
    setVolume: vi.fn(),
    toggleVisualizer: vi.fn(),
    toggleLyrics: vi.fn(),
    isFavorite: () => false,
    toggleFavorite: vi.fn(),
    lyricsSettings: {},
    setLyricsSetting: vi.fn(),
  },
}));

vi.mock("./MusicPlayerContext", () => ({ useMusicPlayer: () => player }));
vi.mock("./MusicVisualizer", () => ({ default: () => null }));
vi.mock("./MusicLyricsPane", () => ({ default: () => null }));
vi.mock("../Pages/Music/MusicPlaylistPickerModal", () => ({ default: () => null }));

import MusicMiniPlayer from "./MusicMiniPlayer";

function openQueue({ length, index }) {
  player.queue = Array.from({ length }, (_, i) => track(i + 1));
  player.index = index;
  player.current = player.queue[index] || player.queue[0];
  render(<MemoryRouter><MusicMiniPlayer /></MemoryRouter>);
  fireEvent.click(screen.getByLabelText("Queue"));
  return screen.getByTestId("music-queue-shuffle");
}

beforeEach(() => {
  player.shuffleQueue.mockClear();
  // Persisted queues leak between test files.
  window.localStorage.clear();
});
afterEach(cleanup);

describe("shuffling the queue from the bar", () => {
  it("fires on the first tap — unlike Clear, nothing is lost", () => {
    fireEvent.click(openQueue({ length: 6, index: 0 }));
    expect(player.shuffleQueue).toHaveBeenCalledTimes(1);
  });

  it("is disabled when fewer than two tracks are still ahead", () => {
    // index 4 of 6 leaves exactly one track to come: "shuffle the rest" is a list of one.
    expect(openQueue({ length: 6, index: 4 })).toBeDisabled();
  });

  it("is enabled as soon as two tracks are ahead of the playhead", () => {
    expect(openQueue({ length: 6, index: 3 })).not.toBeDisabled();
  });

  it("does not clear or stop anything", () => {
    fireEvent.click(openQueue({ length: 6, index: 0 }));
    expect(player.clearQueue).not.toHaveBeenCalled();
    expect(player.stop).not.toHaveBeenCalled();
  });
});
