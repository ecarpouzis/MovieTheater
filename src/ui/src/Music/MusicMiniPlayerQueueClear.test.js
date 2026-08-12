import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// ── The "Clear" control in the queue flyout ─────────────────────────────────
// The queue survives the session now (§Phase 7), which turned "I'm done with this queue" from a
// non-problem into one with no control attached to it: a queue you stopped wanting followed you to
// the next visit, and the only way to shrink it was ✕ per row. The ✕ at the end of the bar does
// empty it, but it is labelled "Close player" and reads as "hide the bar" — the wrong thing to
// reach for when the bar is the part you want to keep.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const { player } = vi.hoisted(() => ({
  player: {
    queue: [
      { id: 1, title: "One", artist: "A" },
      { id: 2, title: "Two", artist: "A" },
    ],
    index: 0,
    current: { id: 1, title: "One", artist: "A", album: "X" },
    playing: false,
    error: null,
    buffering: false,
    audioRef: { current: null },
    trackTime: 0,
    clearQueue: vi.fn(),
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

const openQueue = () => {
  render(<MemoryRouter><MusicMiniPlayer /></MemoryRouter>);
  fireEvent.click(screen.getByLabelText("Queue"));
  return screen.getByTestId("music-queue-clear");
};

beforeEach(() => { player.clearQueue.mockClear(); });
afterEach(cleanup);

describe("clearing the queue from the bar", () => {
  it("takes two taps, because there is no undo", () => {
    const clear = openQueue();
    fireEvent.click(clear);
    expect(player.clearQueue).not.toHaveBeenCalled();   // armed, not fired
    expect(screen.getByTestId("music-queue-clear").textContent).toMatch(/sure/i);

    fireEvent.click(screen.getByTestId("music-queue-clear"));
    expect(player.clearQueue).toHaveBeenCalledTimes(1);
  });

  it("disarms when the flyout is closed, so the confirm is never lying in wait", () => {
    const clear = openQueue();
    fireEvent.click(clear);                              // arm it
    fireEvent.click(screen.getByLabelText("Queue"));     // close the flyout
    fireEvent.click(screen.getByLabelText("Queue"));     // and open it again
    expect(screen.getByTestId("music-queue-clear").textContent).not.toMatch(/sure/i);
    fireEvent.click(screen.getByTestId("music-queue-clear"));
    expect(player.clearQueue).not.toHaveBeenCalled();
  });

  it("clears the queue rather than merely stopping it", () => {
    // stop() leaves the same state behind, but says nothing about the STORED copy — and the stored
    // copy is the entire reason this control exists.
    const clear = openQueue();
    fireEvent.click(clear);
    fireEvent.click(screen.getByTestId("music-queue-clear"));
    expect(player.clearQueue).toHaveBeenCalled();
    expect(player.stop).not.toHaveBeenCalled();
  });
});
