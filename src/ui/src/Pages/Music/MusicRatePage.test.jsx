import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import MusicRatePage from "./MusicRatePage";

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const { api } = vi.hoisted(() => ({
  api: {
    getMusicAlbums: vi.fn(),
    getMusicArtists: vi.fn(),
    getMusicRating: vi.fn(),
    setMusicRatings: vi.fn(),
    getMusicAlbumArt: (id) => `/MusicAlbumArt?id=${id}`,
    getMusicAlbumArtThumb: (id) => `/MusicAlbumArtThumb?id=${id}`,
  },
}));
vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));

const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

const ALBUMS = [
  { id: 11, title: "Moon Safari", year: 1998, artistId: 1, artistName: "Air", hasArt: true, genres: ["Electronic"] },
  { id: 12, title: "Talkie Walkie", year: 2004, artistId: 1, artistName: "Air", hasArt: false },
  { id: 22, title: "Abbey Road", year: 1969, artistId: 2, artistName: "The Beatles", hasArt: true },
];

function renderPage(userData = { hasPassword: true }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={["/music/rate"]}>
        <MusicRatePage userData={userData} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  localStorage.clear();
  api.getMusicAlbums.mockImplementation(() => ok({ total: ALBUMS.length, items: ALBUMS }));
  api.getMusicArtists.mockImplementation(() => ok([]));
  // 0 is a REAL score: Talkie Walkie is rated, and must not read as unrated anywhere on this page.
  api.getMusicRating.mockImplementation(() => ok({ ratings: [{ albumId: 12, score: 0 }] }));
  api.setMusicRatings.mockImplementation(() => ok({ updated: 1, skipped: 0, deleted: 0 }));
});

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe("MusicRatePage — the member surface for scoring records (R9 S10)", () => {
  it("lists the shelf with the viewer's own scores, and counts a 0 as RATED", async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText("Moon Safari")).toBeInTheDocument());
    // One of three is rated — and it is the one scored 0.
    expect(screen.getByText("1 of 3 rated")).toBeInTheDocument();
    const rows = document.querySelectorAll(".music-rate-row");
    expect(rows).toHaveLength(3);
    expect(rows[1].className).toContain("music-rate-row--rated");
    expect(rows[0].className).not.toContain("music-rate-row--rated");
    // An unrated record shows an em dash, never a 0 — the two are different things.
    expect(rows[0].querySelector(".music-rate-score")?.textContent).toBe("—");
    expect(rows[1].querySelector(".music-rate-score")?.textContent).toBe("0");
  });

  it("the search narrows the list, and 'Not rated' hides the record scored 0", async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText("Moon Safari")).toBeInTheDocument());

    fireEvent.change(document.querySelector(".music-rate-search input"), { target: { value: "abbey" } });
    await waitFor(() => expect(document.querySelectorAll(".music-rate-row")).toHaveLength(1));
    expect(screen.getByText("Abbey Road")).toBeInTheDocument();

    fireEvent.change(document.querySelector(".music-rate-search input"), { target: { value: "" } });
    fireEvent.click(screen.getByText("Not rated"));
    await waitFor(() => expect(document.querySelectorAll(".music-rate-row")).toHaveLength(2));
    expect(screen.queryByText("Talkie Walkie")).not.toBeInTheDocument();
  });

  it("clearing a rating sends null — unrated is the ABSENCE of a row, not a zero", async () => {
    renderPage();
    await waitFor(() => expect(screen.getByText("Talkie Walkie")).toBeInTheDocument());
    const rated = document.querySelectorAll(".music-rate-row")[1];
    fireEvent.click(rated.querySelector(".music-rate-clear"));

    await waitFor(() => expect(api.setMusicRatings).toHaveBeenCalled());
    expect(api.setMusicRatings).toHaveBeenCalledWith([{ albumId: 12, value: null }]);
    // It leaves the rated count at once, without waiting for the round trip.
    await waitFor(() => expect(screen.getByText("0 of 3 rated")).toBeInTheDocument());
  });

  it("refuses without a password session rather than showing an empty shelf", () => {
    renderPage({ hasPassword: false });
    expect(screen.getByRole("alert").textContent).toContain("password-verified session");
    expect(api.getMusicRating).not.toHaveBeenCalled();
    expect(api.getMusicAlbums).not.toHaveBeenCalled();
  });
});
