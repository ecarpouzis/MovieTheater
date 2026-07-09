import { render, screen, within, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import GameCard from "./GameCard";
import GameCover from "./GameCover";
import LiveRooms from "./LiveRooms";
import { getCoverAspect, rememberCoverAspect } from "./coverAspect";

global.IS_REACT_ACT_ENVIRONMENT = true;

// antd's Select measures with matchMedia / ResizeObserver, neither of which happy-dom implements.
global.matchMedia = global.matchMedia || ((query) => ({
  matches: false, media: query, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

// happy-dom normalises `aspect-ratio: 0.75` to the two-value form "0.75 / 1", so read the ratio
// rather than coercing the raw string.
const aspectOf = (el) => {
  const [w, h] = el.style.aspectRatio.split("/").map((n) => Number(n.trim()));
  return h ? w / h : w;
};

const game = (over = {}) => ({
  key: "n64|GoldenEye", title: "007 - GoldenEye", system: "n64", artId: 42, hasBoxArt: true,
  maxPlayers: 4, versionCount: 1, rating: 95, ratingCount: 1292,
  genres: "Shooter", summary: "Bond infiltrates a Soviet chemical weapons facility.",
  versions: [{ id: 7, label: "USA", region: "USA", variant: "Release", maxPlayers: 4 }],
  ...over,
});

afterEach(cleanup);

describe("GameCard", () => {
  it("splits genres on ';' as well as ',' — the DB uses both", () => {
    render(<GameCard game={game({ genres: "Action; Adventure" })} onStart={vi.fn()} creating={0} />);
    expect(screen.getByText("Action")).toBeTruthy();
    expect(screen.queryByText("Action; Adventure")).toBeNull();

    cleanup();
    render(<GameCard game={game({ genres: "Shooter, Tactical, Adventure" })} onStart={vi.fn()} creating={0} />);
    expect(screen.getByText("Shooter")).toBeTruthy();
  });

  it("lays the tags out as two fixed lines: system+players, then region+genre", () => {
    const { container } = render(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    const lines = container.querySelectorAll(".arcade-tags");
    expect(lines).toHaveLength(2);
    expect(within(lines[0]).getByText("Nintendo 64")).toBeTruthy();
    expect(within(lines[0]).getByText("4P")).toBeTruthy();
    expect(within(lines[1]).getByText("USA")).toBeTruthy();
    expect(within(lines[1]).getByText("Shooter")).toBeTruthy();
  });

  it("keeps both tag lines even when a game has no region or genre", () => {
    const bare = game({ genres: null, versions: [{ id: 7, label: "x", region: "Unknown", maxPlayers: 1 }] });
    const { container } = render(<GameCard game={bare} onStart={vi.fn()} creating={0} />);
    expect(container.querySelectorAll(".arcade-tags")).toHaveLength(2);
    expect(screen.queryByText("Unknown")).toBeNull();
  });

  it("renders Start room and My saves as siblings in one actions row", () => {
    const { container } = render(<GameCard game={game()} onStart={vi.fn()} onManageSaves={vi.fn()} creating={0} />);
    const actions = container.querySelector(".arcade-card__actions");
    expect(actions.children).toHaveLength(2);
    expect(within(actions).getByText(/Start room/)).toBeTruthy();
    expect(within(actions).getByText("My saves")).toBeTruthy();
  });

  it("starts a room with the selected version id, and doesn't double-fire from the card", () => {
    const onStart = vi.fn();
    render(<GameCard game={game()} onStart={onStart} creating={0} />);
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledTimes(1);
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye");
  });

  it("opens the saves manager for the selected version, not the card", () => {
    const onManageSaves = vi.fn();
    const onStart = vi.fn();
    render(<GameCard game={game()} onStart={onStart} onManageSaves={onManageSaves} creating={0} />);
    fireEvent.click(screen.getByText("My saves"));
    expect(onManageSaves).toHaveBeenCalledWith(7);
    expect(onStart).not.toHaveBeenCalled();
  });

  it("shows the rating badge only when the game is rated", () => {
    const { container, rerender } = render(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-card__rating").textContent).toContain("95");
    rerender(<GameCard game={game({ rating: null })} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-card__rating")).toBeNull();
  });
});

describe("GameCover — natural aspect on a shared height", () => {
  beforeEach(() => { window.sessionStorage.clear(); });

  it("pins the height and adopts the cover's measured aspect ratio", () => {
    const { container } = render(<GameCover game={game()} height={118} />);
    const box = container.querySelector(".arcade-cover");
    expect(box.style.height).toBe("118px");
    // Unmeasured: reserves the 3:4 jewel-case shape.
    expect(aspectOf(box)).toBeCloseTo(0.75, 3);

    // A 4:3 landscape box reports its natural size on load → the tile snaps to it, never cropping.
    const img = container.querySelector(".arcade-cover__img");
    Object.defineProperty(img, "naturalWidth", { value: 640 });
    Object.defineProperty(img, "naturalHeight", { value: 480 });
    fireEvent.load(img);
    expect(aspectOf(box)).toBeCloseTo(4 / 3, 3);
    expect(box.style.height).toBe("118px");
  });

  it("remembers a measured aspect so the tile never re-settles", () => {
    const { container, unmount } = render(<GameCover game={game()} height={118} />);
    const img = container.querySelector(".arcade-cover__img");
    Object.defineProperty(img, "naturalWidth", { value: 500 });
    Object.defineProperty(img, "naturalHeight", { value: 700 });
    fireEvent.load(img);
    unmount();

    const { container: second } = render(<GameCover game={game()} height={118} />);
    expect(aspectOf(second.querySelector(".arcade-cover"))).toBeCloseTo(5 / 7, 3);
  });

  it("ignores a broken decode rather than pinning a 0-width tile", () => {
    expect(rememberCoverAspect(999, 0, 0)).toBeNull();
    expect(getCoverAspect(999)).toBeNull();
  });

  it("falls back to a labelled placeholder for a system with no box art", () => {
    const { container } = render(<GameCover game={game({ system: "naomi", hasBoxArt: false })} height={118} />);
    expect(container.querySelector(".arcade-cover--empty")).toBeTruthy();
    expect(screen.getByText("007 - GoldenEye")).toBeTruthy();
  });
});

describe("LiveRooms", () => {
  const room = (over = {}) => ({
    roomCode: "ABCD", game: { id: 42, title: "007 - GoldenEye", system: "n64" },
    players: ["Eric"], host: "Eric", seatsFree: 3, maxPlayers: 4, starting: false, ...over,
  });

  it("renders nothing when no room is open", () => {
    const { container } = render(<LiveRooms rooms={[]} onJoin={vi.fn()} />);
    expect(container.firstChild).toBeNull();
  });

  it("draws one seat dot per seat, filled for each player present", () => {
    const { container } = render(<LiveRooms rooms={[room()]} onJoin={vi.fn()} />);
    expect(container.querySelectorAll(".arcade-seat")).toHaveLength(4);
    expect(container.querySelectorAll(".arcade-seat--taken")).toHaveLength(1);
  });

  it("names the host and falls back to the first player when host is absent", () => {
    render(<LiveRooms rooms={[room()]} onJoin={vi.fn()} />);
    expect(screen.getByText("Eric hosting")).toBeTruthy();
    cleanup();
    render(<LiveRooms rooms={[room({ host: undefined, players: ["Ada"] })]} onJoin={vi.fn()} />);
    expect(screen.getByText("Ada hosting")).toBeTruthy();
  });

  it("says 'starting…' instead of a seat count while the room is unbound", () => {
    render(<LiveRooms rooms={[room({ starting: true })]} onJoin={vi.fn()} />);
    expect(screen.getByText(/starting…/)).toBeTruthy();
    expect(screen.queryByText(/seats free/)).toBeNull();
  });

  it("joins by room code", () => {
    const onJoin = vi.fn();
    render(<LiveRooms rooms={[room()]} onJoin={onJoin} />);
    fireEvent.click(screen.getByText("Join room"));
    expect(onJoin).toHaveBeenCalledWith("ABCD");
  });
});
