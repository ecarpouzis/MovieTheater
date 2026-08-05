import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import MusicPage from "./MusicPage";

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const { api } = vi.hoisted(() => ({
  api: {
    getMusicAlbums: vi.fn(),
    getMusicArtists: vi.fn(),
    getMusicArtist: vi.fn(),
    searchMusicTracks: vi.fn(),
    getMyMusicPlaylists: vi.fn(),
    getMusicPlaylistItems: vi.fn(),
    getMusicAlbumArt: (id) => `/MusicAlbumArt?id=${id}`,
    getMusicAlbumArtThumb: (id) => `/MusicAlbumArtThumb?id=${id}`,
  },
}));

vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));
vi.mock("../../Music/MusicPlayerContext", () => ({ useMusicPlayer: () => ({ playTracks: vi.fn() }) }));

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const ARTISTS = [
  // Sorted the way /API/Music/Artists returns them (by SortName), each carrying the cover of its
  // alphabetically-first album that has art.
  { id: 1, name: "Air", sortName: "Air", albumCount: 2, trackCount: 20, artAlbumId: 11, hasArt: true, dominantColor: "#123456" },
  { id: 2, name: "The Beatles", sortName: "Beatles, The", albumCount: 3, trackCount: 40, artAlbumId: 22, hasArt: true },
  { id: 3, name: "Nobody", sortName: "Nobody", albumCount: 1, trackCount: 5, artAlbumId: null, hasArt: false },
];

const ALBUMS = [
  { id: 11, title: "Moon Safari", year: 1998, artistId: 1, artistName: "Air", artistSortName: "Air", hasArt: true },
  { id: 22, title: "Abbey Road", year: 1969, artistId: 2, artistName: "The Beatles", artistSortName: "Beatles, The", hasArt: true },
];

function renderPage(search = "") {
  return render(
    <MemoryRouter initialEntries={[`/music${search}`]}>
      <MusicPage userData={{ hasPassword: true }} />
    </MemoryRouter>
  );
}

beforeEach(() => {
  api.getMusicAlbums.mockImplementation(() => ok({ total: ALBUMS.length, items: ALBUMS }));
  api.getMusicArtists.mockImplementation(() => ok(ARTISTS));
  api.getMyMusicPlaylists.mockImplementation(() => ok([]));
  api.searchMusicTracks.mockImplementation(() => ok({ tracks: [] }));
  api.getMusicArtist.mockImplementation(() => ok({ id: 1, name: "Air", albums: [], looseTracks: [] }));
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe("MusicPage browse", () => {
  it("lands on Artists, not Albums — no ?view needed", async () => {
    const { container } = renderPage();
    await screen.findByText("Artists");
    expect(container.querySelector(".music-artist-grid")).toBeTruthy();
    expect(container.querySelector(".music-album-grid")).toBeNull();
  });

  it("gives each artist their first-with-art album's cover, and falls back to initials without one", async () => {
    const { container } = renderPage();
    const cards = await waitFor(() => {
      const found = container.querySelectorAll(".music-artist-card");
      expect(found).toHaveLength(3);
      return found;
    });

    // Air → album 11's art; The Beatles → album 22's. (Queried by tag: the cover is decorative
    // alt="" — no accessible name to match a role on.)
    expect(cards[0].querySelector("img").getAttribute("src")).toContain("id=11");
    expect(cards[1].querySelector("img").getAttribute("src")).toContain("id=22");
    // No album with art anywhere: the initials tile, not a broken image.
    expect(cards[2].querySelector("img")).toBeNull();
    expect(within(cards[2]).getByText("N")).toBeTruthy();
  });

  it("still renders the album grid at ?view=albums", async () => {
    const { container } = renderPage("?view=albums");
    await screen.findByText("Albums");
    expect(container.querySelector(".music-album-grid")).toBeTruthy();
    expect(container.querySelector(".music-artist-grid")).toBeNull();
  });

  it("seeks the grid with the A–Z strip instead of paging it", async () => {
    const { container } = renderPage();
    await waitFor(() => expect(container.querySelectorAll(".music-artist-card")).toHaveLength(3));

    // Buckets come from the SORT name: "The Beatles" is filed under B.
    const b = screen.getByRole("button", { name: "B" });
    expect(b.disabled).toBe(false);
    fireEvent.click(b);

    // Re-anchored at B — Air is behind us now, and the list runs on from there.
    await waitFor(() => {
      const names = [...container.querySelectorAll(".music-artist-card-name")].map((n) => n.textContent);
      expect(names).toEqual(["The Beatles", "Nobody"]);
    });

    // A letter no artist starts with isn't a place you can seek to.
    expect(screen.getByRole("button", { name: "Z" }).disabled).toBe(true);
  });
});
