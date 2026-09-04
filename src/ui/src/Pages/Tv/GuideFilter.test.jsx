import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";

const at = (h) => new Date(Date.UTC(2030, 0, 1, h)).toISOString();

const CHANNELS = [
  { id: 1, name: "Late Night Noir", category: "Movies" },
  { id: 2, name: "Saturday Cartoons", category: "Kids" },
  { id: 3, name: "Deep Cuts", category: "Movies" },
];

const GRID = {
  serverNowUtc: at(20),
  hours: 6,
  lookbackMinutes: 30,
  items: [
    { id: 1, items: [{ title: "Out of the Past", startUtc: at(20), endUtc: at(22) }] },
    { id: 2, items: [{ title: "Duck Amuck", startUtc: at(20), endUtc: at(21) }] },
    { id: 3, items: [{ title: "Gilda", startUtc: at(20), endUtc: at(22) }] },
  ],
};

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getGuideGrid: () => Promise.resolve({ ok: true, json: () => Promise.resolve(GRID) }),
    getPosterThumbnail: (id) => `/ImageThumb?id=${id}`,
  },
}));
vi.mock("../../preloadImages", () => ({ preloadImages: () => {} }));

function renderGuide(props = {}) {
  return render(
    <MemoryRouter>
      <ChannelGrid open channels={CHANNELS} currentChannelId={null} onPick={() => {}} onClose={() => {}} {...props} />
    </MemoryRouter>,
  );
}

/**
 * The guide's row filter (the section bar's search box + its Favourites pill). Both were declared
 * long before anything read them: the TV section has carried a `searchPlaceholder` with nothing
 * portalled into the slot, and ♥ was storable per user with no consumer inside the guide.
 */
describe("Tv/ChannelGrid — the guide filter", () => {
  it("draws every channel when nothing narrows it", async () => {
    renderGuide();
    await waitFor(() => expect(screen.getByText("Late Night Noir")).toBeTruthy());
    expect(screen.getByText("Saturday Cartoons")).toBeTruthy();
    expect(screen.getByText("Deep Cuts")).toBeTruthy();
  });

  it("matches the search against a channel's own name", async () => {
    renderGuide({ query: "cartoons" });
    await waitFor(() => expect(screen.getByText("Saturday Cartoons")).toBeTruthy());
    expect(screen.queryByText("Late Night Noir")).toBeNull();
  });

  it("matches the search against the PROGRAMMES in the window, not just the channel", async () => {
    renderGuide({ query: "gilda" });
    await waitFor(() => expect(screen.getByText("Deep Cuts")).toBeTruthy());
    expect(screen.queryByText("Saturday Cartoons")).toBeNull();
  });

  it("keeps each surviving row's channel NUMBER, so the tune hotkeys still agree", async () => {
    renderGuide({ query: "deep" });
    await waitFor(() => expect(screen.getByText("Deep Cuts")).toBeTruthy());
    expect(screen.getByText("3")).toBeTruthy(); // third in the lineup, not renumbered to 1
  });

  it("narrows to favourites, and an empty favourites set says so rather than showing everything", async () => {
    const { unmount } = renderGuide({ favoriteIds: new Set([2]) });
    await waitFor(() => expect(screen.getByText("Saturday Cartoons")).toBeTruthy());
    expect(screen.queryByText("Deep Cuts")).toBeNull();
    unmount();

    renderGuide({ favoriteIds: new Set() });
    await waitFor(() => expect(screen.getByText(/No favourite channels yet/)).toBeTruthy());
    expect(screen.queryByText("Late Night Noir")).toBeNull();
  });

  it("says so when a search matches nothing", async () => {
    renderGuide({ query: "zzzz" });
    await waitFor(() => expect(screen.getByText(/No channel or programme matches that/)).toBeTruthy());
  });

  it("stays quiet while the caller's channel list is still in flight", async () => {
    // With no filter, the filtered set IS the channel list — and that starts empty, so a naive
    // "nothing survived" test would flash "No channel or programme matches that" on every load.
    renderGuide({ channels: [] });
    await waitFor(() => expect(screen.getByText("Channel Guide")).toBeTruthy());
    expect(screen.queryByText(/No channel or programme matches that/)).toBeNull();
  });
});
