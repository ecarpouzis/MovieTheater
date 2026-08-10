import { render, screen, cleanup } from "@testing-library/react";
import { vi, describe, it, expect, afterEach } from "vitest";

import MusicMiniPlayer from "./MusicMiniPlayer";

// The bar has to say when it is fetching a track whole (anything over Chrome's media buffer cap is
// downloaded before it plays). On a WAN uplink that is a multi-second silence, and a bar that just
// sits there is indistinguishable from the wedged player this whole effort has been chasing.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const TRACK = { id: 1, title: "Mirrors", artist: "Caravan Palace", album: "Gangbusters Melody Club" };

function playerStub(over = {}) {
  return {
    current: TRACK,
    queue: [TRACK],
    index: 0,
    playing: false,
    error: null,
    buffering: false,
    audioRef: { current: null },
    favoriteIds: new Set(),
    isFavorite: () => false,
    toggleFavorite: vi.fn(),
    visualizerOn: false,
    lyricsOn: false,
    lyricsSettings: {},
    next: vi.fn(), prev: vi.fn(), toggle: vi.fn(), seek: vi.fn(), stop: vi.fn(),
    playAt: vi.fn(), removeAt: vi.fn(), setVolume: vi.fn(),
    toggleVisualizer: vi.fn(), toggleLyrics: vi.fn(), ensureAudioGraph: vi.fn(),
    ...over,
  };
}

let stub;
vi.mock("./MusicPlayerContext", () => ({ useMusicPlayer: () => stub }));
vi.mock("react-router-dom", () => ({ useHistory: () => ({ push: vi.fn() }), useLocation: () => ({ pathname: "/music" }) }));

afterEach(cleanup);

describe("the play bar while a track is being fetched", () => {
  it("says it is buffering instead of sitting silent", () => {
    stub = playerStub({ buffering: true });
    render(<MusicMiniPlayer />);
    expect(screen.getByTestId("music-buffering")).toBeTruthy();
  });

  it("shows the artist and album normally when it is not", () => {
    stub = playerStub();
    render(<MusicMiniPlayer />);
    expect(screen.queryByTestId("music-buffering")).toBeNull();
    expect(screen.getByText(/Caravan Palace/)).toBeTruthy();
  });

  it("lets a real error win — a failure matters more than a wait", () => {
    stub = playerStub({ buffering: true, error: "Playback interrupted — waiting for the connection." });
    render(<MusicMiniPlayer />);
    expect(screen.getByTestId("music-error-line")).toBeTruthy();
    expect(screen.queryByTestId("music-buffering")).toBeNull();
  });
});
