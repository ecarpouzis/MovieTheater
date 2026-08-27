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
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
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
    await waitFor(() => expect(document.querySelector(".music-album-grid")).toBeTruthy());
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
    await waitFor(() => expect(container.querySelectorAll(".music-artist-card")).toHaveLength(3));

    // A letter no artist starts with isn't a place you can seek to.
    expect(screen.getByRole("button", { name: "Z" }).disabled).toBe(true);
  });

  // ⚠ THE regression. This test used to assert the opposite — that clicking B left `["The Beatles",
  // "Nobody"]` rendered — because the jump re-anchored the list at the letter's offset and threw
  // away everything before it. That is what Eric hit on 2026-08-13: "I tapped J and I couldn't get
  // back to the artists before J". There was nothing above to scroll to, because they had stopped
  // existing. The catalog is already in the browser, so the list now stays WHOLE and the jump is a
  // scroll (useGridWindow's scrollToIndex).
  it("keeps everything BEFORE the letter you tapped, so you can scroll back up to it", async () => {
    const { container } = renderPage();
    await waitFor(() => expect(container.querySelectorAll(".music-artist-card")).toHaveLength(3));

    fireEvent.click(screen.getByRole("button", { name: "B" }));

    await waitFor(() => {
      const names = [...container.querySelectorAll(".music-artist-card-name")].map((n) => n.textContent);
      // Air is still there, still FIRST — above the letter that was tapped, which is the whole point.
      expect(names).toEqual(["Air", "The Beatles", "Nobody"]);
    });

    // …and jumping backwards is not a special case either: the list is untouched either way.
    fireEvent.click(screen.getByRole("button", { name: "A" }));
    await waitFor(() => {
      const names = [...container.querySelectorAll(".music-artist-card-name")].map((n) => n.textContent);
      expect(names).toEqual(["Air", "The Beatles", "Nobody"]);
    });
  });

  it("still holds the whole catalog after a jump, not the tail of it", async () => {
    // The grid used to be re-sliced by a jump — three artists became two after tapping B. (The
    // heading's count moved to the rail in R9 S1; the grid itself is the witness now.)
    const { container } = renderPage();
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "B" }));
    await waitFor(() => expect(container.querySelectorAll(".music-artist-card")).toHaveLength(3));
  });
});

// ── The shelves (MusicArtist.Kind) ──────────────────────────────────────────
// Comedy and audiobooks sat in the artist grid between Garbage and Orbital — 22 George Carlin
// records and the Ender novels in the middle of browsing for music. The exclusion is the SERVER's
// default (no ?kind= means the untagged rows), so what this page has to get right is: ask for
// nothing by default, ask for the shelf when the URL names one, and never filter client-side —
// holding all 813 artists in order to hide 42 of them would put the excluded material one stale
// filter away from the grid it was excluded from.
describe("MusicPage shelves", () => {
  it("asks for no kind at all on the library — the default is the server's", async () => {
    renderPage();
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
    expect(api.getMusicArtists).toHaveBeenCalledWith("");
    expect(api.getMusicAlbums).toHaveBeenCalledWith("");
  });

  it("fetches the shelf rather than filtering the library down to it", async () => {
    renderPage("?kind=comedy");
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
    expect(api.getMusicArtists).toHaveBeenCalledWith("comedy");
    expect(api.getMusicAlbums).toHaveBeenCalledWith("comedy");
  });

  it("asks for the shelf both ways round (the heading moved to the bar in R9 S1)", async () => {
    renderPage("?kind=audiobook");
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
    cleanup();
    renderPage("?kind=audiobook&view=albums");
    await waitFor(() => expect(document.querySelector(".music-album-grid")).toBeTruthy());
  });

  it("scopes song search to the shelf you are standing on", async () => {
    api.searchMusicTracks.mockImplementation(() => ok({ tracks: [] }));
    renderPage("?kind=comedy&q=airplane");
    await waitFor(() => expect(api.searchMusicTracks).toHaveBeenCalledWith("airplane", "comedy"));
  });

  it("treats a shelf it doesn't know as the library, not as an empty page", async () => {
    renderPage("?kind=polka");
    await waitFor(() => expect(document.querySelector(".music-artist-grid")).toBeTruthy());
    expect(api.getMusicArtists).toHaveBeenCalledWith("");
  });
});
