import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import GuideDetail from "./GuideDetail";

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getPosterThumbnail: (id, v, kind) => `/ImageThumb?id=${id}&v=${v}&kind=${kind}`,
    setFavoriteChannels: vi.fn(() => Promise.resolve({ ok: true })),
  },
}));
// The preview column is exercised in GuidePreview.test.jsx; here it is a stub that records its props.
vi.mock("./GuidePreview", () => ({
  default: (props) => <div data-testid="preview" data-armed={String(props.armed)} data-live={String(props.live)} />,
}));

/**
 * R9 S1c: the guide's click-a-show panel — description, ▶ Watch on the channel (tunes it), Open
 * title (the movie sheet on the landing), ♥ the channel (optimistic through setUserData), up next.
 * Guide v2 (2026-09-04): the meta line, ▶ Tune in / ↺ Start over (the channel restart vote, carried
 * to the room as ?restart=1), and the preview column on desktop.
 */
const channel = { id: 7, name: "Late Night Noir" };
const at = (h, m = 0) => new Date(Date.UTC(2030, 0, 1, h, m)).toISOString();
const NOW = Date.parse(at(20, 30));
const program = { title: "Out of the Past", plot: "A private eye escapes his past.", startUtc: at(20), endUtc: at(22), posterId: 41, posterVersion: 2, kind: "movie", year: 1947, rating: "Approved", imdbRating: 8.0, genre: "Film-Noir", playableId: 900 };
const laura = { title: "Laura", startUtc: at(22), endUtc: at(23), posterId: 42, posterVersion: 1, kind: "movie", playableId: 901 };
const rowItems = [program, laura, { title: "Gilda", startUtc: at(23), endUtc: at(24), posterId: 0, kind: "movie" }];

function renderPanel({ userData = { favoriteChannels: [] }, prog = program, row = { viewers: 0, paused: false }, previewArmed = false } = {}) {
  const setUserData = vi.fn();
  const onClose = vi.fn();
  const onArmPreview = vi.fn();
  render(
    <MemoryRouter initialEntries={["/channels"]}>
      <GuideDetail channel={channel} program={prog} rowItems={rowItems} row={row} userData={userData} setUserData={setUserData} onClose={onClose} previewArmed={previewArmed} onArmPreview={onArmPreview} nowMs={NOW} />
      <Route path="*" render={({ location }) => <div data-testid="where">{location.pathname}{location.search}</div>} />
    </MemoryRouter>,
  );
  return { setUserData, onClose, onArmPreview };
}

describe("Tv/GuideDetail", () => {
  it("shows the show, its slot on the channel, the meta line, the plot and what's up next", () => {
    renderPanel();
    expect(screen.getByRole("heading", { name: "Out of the Past" })).toBeTruthy();
    expect(screen.getByText("Late Night Noir").className).toBe("guide-detail__channel");
    expect(screen.getByText("now")).toBeTruthy();
    // The meta line: year · certificate (a boxed tag) · slot length · IMDb score · genre.
    expect(screen.getByText("1947")).toBeTruthy();
    expect(screen.getByText("Approved").className).toBe("guide-detail__tag");
    expect(screen.getByText("2h")).toBeTruthy();
    expect(screen.getByText("IMDb").parentElement.textContent).toBe("IMDb 8.0");
    expect(screen.getByText("Film-Noir")).toBeTruthy();
    expect(screen.getByText("A private eye escapes his past.")).toBeTruthy();
    expect(screen.getByText("Up next")).toBeTruthy();
    expect(screen.getByTitle("Laura")).toBeTruthy();
    expect(screen.getByTitle("Gilda")).toBeTruthy();
  });

  it("headlines an episode by its series, with S/E and the episode title in the meta line", () => {
    renderPanel({ prog: { ...program, title: "George Lopez – S03E09 Fishing Cubans", seriesTitle: "George Lopez", episodeTitle: "Fishing Cubans", season: 3, episode: 9, year: 2002, rating: "TV-PG", imdbRating: null, genre: "Comedy", startUtc: at(20), endUtc: at(20, 30) } });
    expect(screen.getByRole("heading", { name: "George Lopez" })).toBeTruthy();
    expect(screen.getByText("S03 E09")).toBeTruthy();
    expect(screen.getByText("Fishing Cubans")).toBeTruthy();
    expect(screen.getByText("TV-PG").className).toBe("guide-detail__tag");
    expect(screen.getByText("30 min")).toBeTruthy();
    expect(screen.getByText("Comedy")).toBeTruthy();
  });

  it("▶ Tune in joins the channel and Open title opens the movie sheet on the landing", () => {
    renderPanel();
    fireEvent.click(screen.getByRole("button", { name: /open title/i }));
    expect(screen.getByTestId("where").textContent).toBe("/?title=movie:41");
    fireEvent.click(screen.getByRole("button", { name: /tune in/i }));
    expect(screen.getByTestId("where").textContent).toBe("/tv/7");
  });

  it("↺ Start over hands the room a restart intent; with others watching it is a vote", () => {
    renderPanel();
    const btn = screen.getByRole("button", { name: /start over/i });
    expect(btn.textContent.trim()).toBe("Start over");
    fireEvent.click(btn);
    expect(screen.getByTestId("where").textContent).toBe("/tv/7?restart=1");
  });

  it("names the vote when the channel already has viewers", () => {
    renderPanel({ row: { viewers: 2, paused: false } });
    expect(screen.getByRole("button", { name: /vote to start over · 2 watching/i })).toBeTruthy();
  });

  it("offers no Start over for a programme that is not airing now", () => {
    renderPanel({ prog: laura });
    expect(screen.queryByRole("button", { name: /start over/i })).toBeNull();
    expect(screen.getByRole("button", { name: /tune in/i })).toBeTruthy();
  });

  it("offers no Start over on a frozen channel, and says the channel is paused", () => {
    renderPanel({ row: { viewers: 1, paused: true } });
    expect(screen.queryByRole("button", { name: /start over/i })).toBeNull();
    expect(screen.getByText("paused")).toBeTruthy();
  });

  it("mounts the preview column with the panel's arming state", () => {
    renderPanel({ previewArmed: true });
    const preview = screen.getByTestId("preview");
    expect(preview.dataset.armed).toBe("true");
    expect(preview.dataset.live).toBe("true");
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
