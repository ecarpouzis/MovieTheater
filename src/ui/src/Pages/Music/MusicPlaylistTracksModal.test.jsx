import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import MusicPlaylistTracksModal from "./MusicPlaylistTracksModal";

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };
// antd's Modal measures the scrollbar on open.
global.matchMedia = global.matchMedia || ((q) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }));

const { api, player } = vi.hoisted(() => ({
  api: { getMusicPlaylistItems: vi.fn() },
  player: {
    playTracks: vi.fn(),
    shuffleTracks: vi.fn(),
    enqueue: vi.fn(),
    isPlayable: vi.fn(),
    canTranscode: true,
  },
}));

vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));
vi.mock("../../Music/MusicPlayerContext", () => ({ useMusicPlayer: () => player }));

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const ITEMS = [
  { id: 101, title: "Whirring", artistName: "The Joy Formidable", albumTitle: "The Big Roar", albumId: 7, durationSec: 219 },
  { id: 102, title: "Austere", artistName: "The Joy Formidable", albumTitle: "The Big Roar", albumId: 7, durationSec: 240 },
  { id: 103, title: "Gone", artistName: "Missing Band", albumTitle: "Nowhere", albumId: 8, durationSec: 100, missing: true },
];

beforeEach(() => {
  vi.clearAllMocks();
  player.isPlayable.mockImplementation((t) => !t.missing);
  api.getMusicPlaylistItems.mockReturnValue(ok({ name: "Road trip", isFavorites: false, items: ITEMS }));
});

afterEach(cleanup);

async function open(props = {}) {
  render(<MusicPlaylistTracksModal open playlistId={5} onClose={vi.fn()} {...props} />);
  await waitFor(() => expect(screen.getByText("Whirring")).toBeTruthy());
}

describe("playlist tracklist — taking ONE track off a playlist", () => {
  it("queues just the clicked track, leaving the rest of the playlist alone", async () => {
    await open();
    // Row order matches the playlist, so the second queue button is the second track.
    fireEvent.click(screen.getAllByLabelText("Add to queue")[1]);
    expect(player.enqueue).toHaveBeenCalledTimes(1);
    const queued = player.enqueue.mock.calls[0][0];
    expect(queued).toHaveLength(1);
    expect(queued[0].id).toBe(102);
    expect(queued[0].title).toBe("Austere");
  });

  it("does not disturb what is already playing — queueing never calls playTracks", async () => {
    await open();
    fireEvent.click(screen.getAllByLabelText("Add to queue")[0]);
    expect(player.playTracks).not.toHaveBeenCalled();
    expect(player.shuffleTracks).not.toHaveBeenCalled();
  });

  it("carries the fields the player needs to actually stream the track", async () => {
    await open();
    fireEvent.click(screen.getAllByLabelText("Add to queue")[0]);
    // The API's row shape is not the queue's; a mapping that drops albumId leaves the bar with no
    // art, and one that drops artist leaves it with no second line.
    expect(player.enqueue.mock.calls[0][0][0]).toMatchObject({
      id: 101, title: "Whirring", artist: "The Joy Formidable", album: "The Big Roar", albumId: 7, durationSec: 219,
    });
  });

  it("plays the playlist FROM the clicked track rather than that track alone", async () => {
    await open();
    fireEvent.click(screen.getByText("Austere"));
    expect(player.playTracks).toHaveBeenCalledTimes(1);
    const [tracks, startIndex] = player.playTracks.mock.calls[0];
    expect(tracks).toHaveLength(3);
    expect(startIndex).toBe(1);
  });

  it("refuses to queue a missing file, so the click can't be silently dropped", async () => {
    await open();
    const buttons = screen.getAllByLabelText("Add to queue");
    expect(buttons[2].disabled).toBe(true);
    fireEvent.click(buttons[2]);
    expect(player.enqueue).not.toHaveBeenCalled();
  });
});

describe("playlist tracklist — the whole-playlist verbs", () => {
  it("appends the whole playlist without replacing the queue", async () => {
    await open();
    fireEvent.click(screen.getByText("☰ Queue"));
    expect(player.enqueue).toHaveBeenCalledTimes(1);
    // Only the playable ones can reach the player anyway, but the mapping must not drop rows on its
    // own — enqueue's isPlayable filter is the single place that decision is made.
    expect(player.enqueue.mock.calls[0][0]).toHaveLength(3);
    expect(player.playTracks).not.toHaveBeenCalled();
  });

  it("still offers Play and Shuffle, which DO replace the queue", async () => {
    await open();
    fireEvent.click(screen.getByText("▶ Play"));
    expect(player.playTracks).toHaveBeenCalledWith(expect.any(Array), 0);
    fireEvent.click(screen.getByText("🔀 Shuffle"));
    expect(player.shuffleTracks).toHaveBeenCalledTimes(1);
  });

  it("hands a single track to the playlist picker, not the whole list", async () => {
    const onAddToPlaylist = vi.fn();
    await open({ onAddToPlaylist });
    fireEvent.click(screen.getAllByLabelText("Add to playlist")[0]);
    expect(onAddToPlaylist).toHaveBeenCalledWith([{ id: 101, title: "Whirring" }], "Whirring");
  });
});

describe("playlist tracklist — loading edges", () => {
  it("says so when the playlist can't be loaded instead of showing an empty list", async () => {
    api.getMusicPlaylistItems.mockReturnValue(Promise.resolve({ ok: false, status: 500 }));
    render(<MusicPlaylistTracksModal open playlistId={5} onClose={vi.fn()} />);
    await waitFor(() => expect(screen.getByText(/couldn't be loaded/i)).toBeTruthy());
  });

  it("does not fetch until it is actually opened", () => {
    render(<MusicPlaylistTracksModal open={false} playlistId={5} onClose={vi.fn()} />);
    expect(api.getMusicPlaylistItems).not.toHaveBeenCalled();
  });

  it("names an empty Favorites list after how you fill it", async () => {
    api.getMusicPlaylistItems.mockReturnValue(ok({ name: "Favorites", isFavorites: true, items: [] }));
    render(<MusicPlaylistTracksModal open playlistId={1} onClose={vi.fn()} />);
    await waitFor(() => expect(screen.getByText(/Nothing favorited yet/i)).toBeTruthy());
  });
});
