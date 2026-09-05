import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";
import { MovieAPI } from "../../MovieAPI";

const at = (h, m = 0) => new Date(Date.UTC(2030, 0, 1, h, m)).toISOString();

const CHANNELS = [
  { id: 1, name: "Late Night Noir", category: "Movies" },
  { id: 2, name: "Sitcom Block", category: "Series" },
];

// Now is 20:10. Row 1: a film 19:30–21:00 then another; row 2: half-hour episodes.
const GRID = {
  serverNowUtc: at(20, 10),
  hours: 6,
  lookbackMinutes: 30,
  items: [
    { id: 1, items: [
      { title: "Out of the Past", startUtc: at(19, 30), endUtc: at(21) },
      { title: "Laura", startUtc: at(21), endUtc: at(22, 30) },
    ] },
    { id: 2, items: [
      { title: "Frasier – S04E18 Ham Radio", startUtc: at(20), endUtc: at(20, 30) },
      { title: "Frasier – S04E19", startUtc: at(20, 30), endUtc: at(21) },
      { title: "Cheers – S01E01", startUtc: at(21), endUtc: at(21, 30) },
    ] },
  ],
};

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getGuideGrid: vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve(GRID) })),
    getPosterThumbnail: (id) => `/ImageThumb?id=${id}`,
  },
}));
vi.mock("../../preloadImages", () => ({ preloadImages: () => {} }));

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date(at(20, 10)));
  MovieAPI.getGuideGrid.mockClear();
});
afterEach(() => vi.useRealTimers());

function renderGuide(props = {}) {
  const onPick = vi.fn();
  const onPickProgram = vi.fn();
  const utils = render(
    <MemoryRouter>
      <ChannelGrid open channels={CHANNELS} currentChannelId={null} onPick={onPick} onClose={() => {}} onPickProgram={onPickProgram} {...props} />
    </MemoryRouter>,
  );
  return { ...utils, onPick, onPickProgram };
}

/**
 * Guide v2.1: the keyboard walks the grid (↑/↓ the same moment on the neighbouring row, ←/→ along the
 * row, Enter tunes), and ‹ Now › step the timeline — › toward the horizon asks the server for more hours.
 */
describe("Tv/ChannelGrid — keyboard and timeline navigation", () => {
  it("↓ lands on the programme airing at the same moment on the row below; → walks the row; Enter tunes", async () => {
    const { onPick, onPickProgram } = renderGuide({ selectedKey: `1:${at(19, 30)}` });
    await waitFor(() => expect(screen.getAllByText("Out of the Past").length).toBeGreaterThan(0));

    fireEvent.keyDown(window, { key: "ArrowDown" });
    expect(onPickProgram).toHaveBeenLastCalledWith(CHANNELS[1], expect.objectContaining({ title: "Frasier – S04E18 Ham Radio" }), expect.any(Array));

    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(onPickProgram).toHaveBeenLastCalledWith(CHANNELS[0], expect.objectContaining({ title: "Laura" }), expect.any(Array));

    fireEvent.keyDown(window, { key: "Enter" });
    expect(onPick).toHaveBeenCalledWith(CHANNELS[0]);
  });

  it("with nothing selected, an arrow lands on the first row's current programme", async () => {
    const { onPickProgram } = renderGuide({ selectedKey: null });
    await waitFor(() => expect(screen.getAllByText("Out of the Past").length).toBeGreaterThan(0));
    fireEvent.keyDown(window, { key: "ArrowDown" });
    expect(onPickProgram).toHaveBeenCalledWith(CHANNELS[0], expect.objectContaining({ title: "Out of the Past" }), expect.any(Array));
  });

  it("leaves the keys alone while a text field has focus, and in the room (no onPickProgram)", async () => {
    const { onPickProgram, unmount } = renderGuide({ selectedKey: `1:${at(19, 30)}` });
    await waitFor(() => expect(screen.getAllByText("Out of the Past").length).toBeGreaterThan(0));
    const input = document.createElement("input");
    document.body.appendChild(input);
    fireEvent.keyDown(input, { key: "ArrowDown" });
    expect(onPickProgram).not.toHaveBeenCalled();
    input.remove();
    unmount();

    const onPick = vi.fn();
    render(<MemoryRouter><ChannelGrid open channels={CHANNELS} currentChannelId={null} onPick={onPick} onClose={() => {}} /></MemoryRouter>);
    await waitFor(() => expect(screen.getAllByText("Out of the Past").length).toBeGreaterThan(0));
    fireEvent.keyDown(window, { key: "Enter" });
    expect(onPick).not.toHaveBeenCalled();
  });

  it("› toward the horizon widens the fetch to 12 hours; Now and ‹ never do", async () => {
    renderGuide();
    await waitFor(() => expect(MovieAPI.getGuideGrid).toHaveBeenCalledWith(6, expect.anything()));
    fireEvent.click(screen.getByRole("button", { name: "Now" }));
    fireEvent.click(screen.getByRole("button", { name: "Earlier" }));
    expect(MovieAPI.getGuideGrid).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Later" }));
    await waitFor(() => expect(MovieAPI.getGuideGrid).toHaveBeenCalledWith(12, expect.anything()));
  });
});
