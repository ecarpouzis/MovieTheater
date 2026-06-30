import { render, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// dnd-kit measures droppable nodes via ResizeObserver, which happy-dom lacks.
global.ResizeObserver =
  global.ResizeObserver ||
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getTitlesByIds: vi.fn(),
    getMiscByIds: vi.fn(() => Promise.resolve({ json: () => Promise.resolve([]) })),
    getPosterThumbnail: () => "",
    setRatings: vi.fn(() => Promise.resolve({ json: () => Promise.resolve({ success: true }) })),
    setUserSetting: vi.fn(() => Promise.resolve({ json: () => Promise.resolve({ success: true }) })),
  },
}));

import RatePage from "./RatePage";
import { MovieAPI } from "../../MovieAPI";

const cards = [
  { id: 1, kind: "movie", title: "Alpha", simpleTitle: "Alpha", posterVersion: 1, releaseDate: "2000-01-01" },
  { id: 2, kind: "movie", title: "Beta", simpleTitle: "Beta", posterVersion: 1, releaseDate: "2001-01-01" },
  { id: 3, kind: "movie", title: "Gamma", simpleTitle: "Gamma", posterVersion: 1, releaseDate: "2002-01-01" },
];

const userData = {
  username: "tester",
  moviesSeen: [1, 2, 3],
  ratings: { "movie:1": 80, "movie:2": 50 }, // movie 3 watched but unrated
  ratingAnchors: [],
};

function renderRate(ud = userData) {
  return render(
    <MemoryRouter initialEntries={["/rate"]}>
      <RatePage userData={ud} setUserData={() => {}} />
    </MemoryRouter>
  );
}

describe("RatePage", () => {
  beforeEach(() => {
    MovieAPI.getTitlesByIds.mockResolvedValue({ json: () => Promise.resolve(cards) });
  });
  afterEach(() => vi.clearAllMocks());

  it("ranks rated titles best→worst and sends unrated ones to the tray", async () => {
    const { container } = renderRate();
    await waitFor(() => expect(container.querySelector(".rate-list")).toBeTruthy());

    const titles = [...container.querySelectorAll(".rate-bar-title")].map((n) => n.textContent);
    expect(titles).toEqual(["Alpha", "Beta"]); // 80 ranks above 50

    // No anchors → two movies spread evenly in (0,100): 67 and 33.
    const sc = [...container.querySelectorAll(".rate-bar-score")].map((n) => n.textContent);
    expect(sc).toEqual(["67", "33"]);

    // The watched-but-unrated title lands in the tray.
    const tray = container.querySelector(".rate-tray");
    expect(tray).toBeTruthy();
    expect(within(tray).getByText("Gamma")).toBeTruthy();
  });

  it("does not write anything on initial load (baseline equals the computed layout)", async () => {
    const { container } = renderRate();
    await waitFor(() => expect(container.querySelector(".rate-list")).toBeTruthy());
    await new Promise((r) => setTimeout(r, 1000)); // longer than the autosave debounce
    expect(MovieAPI.setRatings).not.toHaveBeenCalled();
  });
});
