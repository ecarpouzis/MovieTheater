import { render, screen, waitFor, cleanup } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import MusicSiderRail from "./MusicSiderRail";

// The sider rail renders under a password session only, over the SAME shelf rows the page fetches
// (one shared React-Query resource): the shelf pills (no counts), the artists with their names and
// album counts, the tags, the year range, and the count line naming artists on the landing.

const { api } = vi.hoisted(() => ({
  api: {
    getMusicAlbums: vi.fn(),
    getMusicArtists: vi.fn(),
    getMusicAlbumArt: (id: number) => `/MusicAlbumArt?id=${id}`,
    getMusicAlbumArtThumb: (id: number) => `/MusicAlbumArtThumb?id=${id}`,
  },
}));
vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));

const ok = (body: unknown) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });
const ARTISTS = [
  { id: 1, name: "Air", sortName: "Air", albumCount: 2, trackCount: 20 },
  { id: 2, name: "The Beatles", sortName: "Beatles, The", albumCount: 1, trackCount: 40 },
];
const ALBUMS = [
  { id: 11, title: "Moon Safari", year: 1998, artistId: 1, artistName: "Air", tag: "Live" },
  { id: 12, title: "Talkie Walkie", year: 2004, artistId: 1, artistName: "Air" },
  { id: 22, title: "Abbey Road", year: 1969, artistId: 2, artistName: "The Beatles" },
];

function mount(search: string, userData: { hasPassword: boolean } | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[`/music${search}`]}>
        <MusicSiderRail userData={userData} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  api.getMusicAlbums.mockImplementation(() => ok({ total: ALBUMS.length, items: ALBUMS }));
  api.getMusicArtists.mockImplementation(() => ok(ARTISTS));
});
afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe("MusicSiderRail", () => {
  it("renders nothing without a password session, and asks the server for nothing", () => {
    const { container } = mount("", { hasPassword: false });
    expect(container.firstChild).toBeNull();
    expect(api.getMusicArtists).not.toHaveBeenCalled();
  });

  it("lists Shelf · Artist · Genre · Tag · Years · Rating over the shelf's rows and counts artists on the landing", async () => {
    mount("", { hasPassword: true });
    await waitFor(() => expect(screen.getByText("2 artists")).toBeInTheDocument());
    // R9 S10 added Genre (a dynamic long tail over the shelf rows) and the Rating floor.
    expect(Array.from(document.querySelectorAll(".bx-rsec-title")).map((e) => e.textContent)).toEqual(["Shelf", "Artist", "Genre", "Tag", "Years", "Rating"]);
    expect(api.getMusicArtists).toHaveBeenCalledWith("");
    // The shelf pills carry no counts; the artist rows do. (The option lists load a tick after the rows.)
    await waitFor(() => expect(screen.getByText("Comedy")).toBeInTheDocument());
    expect(screen.getByText("Audiobooks")).toBeInTheDocument();
    expect(screen.getByText("Air").parentElement?.querySelector(".bx-opt-count")?.textContent).toBe("2");
    expect(screen.getByText("Comedy").parentElement?.querySelector(".bx-opt-count")).toBeNull();
  });

  it("counts albums on the every-album grid and fetches the named shelf", async () => {
    mount("?items=items&f=kind:comedy&f=artist:1", { hasPassword: true });
    await waitFor(() => expect(screen.getByText("2 comedy albums")).toBeInTheDocument());
    expect(api.getMusicArtists).toHaveBeenCalledWith("comedy");
    expect(api.getMusicAlbums).toHaveBeenCalledWith("comedy");
  });
});
