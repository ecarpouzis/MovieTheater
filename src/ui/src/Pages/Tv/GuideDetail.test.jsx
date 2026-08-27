import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import GuideDetail from "./GuideDetail";

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPosterThumbnail: (id, v, kind) => `/ImageThumb?id=${id}&v=${v}&kind=${kind}`,
    setFavoriteChannels: vi.fn(() => Promise.resolve({ ok: true })),
  },
}));

/**
 * R9 S1c: the guide's click-a-show panel — description, ▶ Watch on the channel (tunes it), Open
 * title (the movie sheet on the landing), ♥ the channel (optimistic through setUserData), up next.
 */
const channel = { id: 7, name: "Late Night Noir" };
const at = (h) => new Date(Date.UTC(2030, 0, 1, h)).toISOString();
const program = { title: "Out of the Past", plot: "A private eye escapes his past.", startUtc: at(20), endUtc: at(22), posterId: 41, posterVersion: 2, kind: "movie" };
const rowItems = [program, { title: "Laura", startUtc: at(22), endUtc: at(23), posterId: 42, posterVersion: 1, kind: "movie" }, { title: "Gilda", startUtc: at(23), endUtc: at(24), posterId: 0, kind: "movie" }];

function renderPanel(userData = { favoriteChannels: [] }) {
  const setUserData = vi.fn();
  const onClose = vi.fn();
  render(
    <MemoryRouter initialEntries={["/channels"]}>
      <GuideDetail channel={channel} program={program} rowItems={rowItems} userData={userData} setUserData={setUserData} onClose={onClose} />
      <Route path="*" render={({ location }) => <div data-testid="where">{location.pathname}{location.search}</div>} />
    </MemoryRouter>,
  );
  return { setUserData, onClose };
}

describe("Tv/GuideDetail", () => {
  it("shows the show, its slot on the channel, the plot and what's up next", () => {
    renderPanel();
    expect(screen.getByRole("heading", { name: "Out of the Past" })).toBeTruthy();
    expect(screen.getByText(/Late Night Noir ·/)).toBeTruthy();
    expect(screen.getByText("A private eye escapes his past.")).toBeTruthy();
    expect(screen.getByText("Up next")).toBeTruthy();
    expect(screen.getByTitle("Laura")).toBeTruthy();
    expect(screen.getByTitle("Gilda")).toBeTruthy();
  });

  it("▶ Watch tunes the channel and Open title opens the movie sheet on the landing", () => {
    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: /open title/i }));
    expect(screen.getByTestId("where").textContent).toBe("/?title=movie:41");
    fireEvent.click(screen.getByRole("button", { name: /watch on late night noir/i }));
    expect(screen.getByTestId("where").textContent).toBe("/tv/7");
  });

  it("♥ toggles the channel as a favourite optimistically, and × closes", () => {
    const { setUserData, onClose } = renderPanel();
    fireEvent.click(screen.getByRole("button", { name: /favourite channel/i }));
    expect(setUserData).toHaveBeenCalledTimes(1);
    const next = setUserData.mock.calls[0][0]({ favoriteChannels: [] });
    expect(next.favoriteChannels).toEqual([7]);
    fireEvent.click(screen.getByRole("button", { name: "Close details" }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
