import { render, screen, within, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import GameCard from "./GameCard";
import GameCover, { coverBox } from "./GameCover";
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

  it("puts every tag on ONE line now that the version picker moved to the footer", () => {
    const { container } = render(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    const lines = container.querySelectorAll(".arcade-tags");
    expect(lines).toHaveLength(1);
    expect(within(lines[0]).getByText("Nintendo 64")).toBeTruthy();
    expect(within(lines[0]).getByText("4P")).toBeTruthy();
    expect(within(lines[0]).getByText("USA")).toBeTruthy();
    expect(within(lines[0]).getByText("Shooter")).toBeTruthy();
  });

  it("keeps the tag line even when a game has no region or genre", () => {
    const bare = game({ genres: null, versions: [{ id: 7, label: "x", region: "Unknown", maxPlayers: 1 }] });
    const { container } = render(<GameCard game={bare} onStart={vi.fn()} creating={0} />);
    expect(container.querySelectorAll(".arcade-tags")).toHaveLength(1);
    expect(screen.queryByText("Unknown")).toBeNull();
  });

  it("omits the controls row entirely for a single-version game with no cheats", () => {
    const { container } = render(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-card__controls")).toBeNull();
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
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye", []);
  });

  it("opens the saves manager for the selected version, not the card", () => {
    const onManageSaves = vi.fn();
    const onStart = vi.fn();
    render(<GameCard game={game()} onStart={onStart} onManageSaves={onManageSaves} creating={0} />);
    fireEvent.click(screen.getByText("My saves"));
    expect(onManageSaves).toHaveBeenCalledWith(7);
    expect(onStart).not.toHaveBeenCalled();
  });

  // ── Cheats (docs/arcade-cheats.md) ──────────────────────────────────────────────────────────────
  const ps2 = (over = {}) => game({
    key: "ps2|God of War", title: "God of War", system: "ps2", versionCount: 1,
    versions: [{ id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 2, defaultCheats: ["c500"] }],
    ...over,
  });

  it("shows the cheat picker with a count, and hides it when a version has no cheats", () => {
    const { container, rerender } = render(<GameCard game={ps2()} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-chip--cheats")).toBeTruthy();
    // 1 of the version's 2 cheats is on by default (the widescreen patch).
    expect(screen.getByText(/1 of 2/)).toBeTruthy();

    rerender(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-chip--cheats")).toBeNull();
  });

  // The collapsed chip has to answer BOTH questions — how many cheats this version has, and how many are
  // on. It used to answer only one at a time: the available count as a placeholder, replaced by the
  // selected count once you picked one, so "⚡ 2 cheats" looked like a game with two cheats.
  it("shows the AVAILABLE count when no cheat is on", () => {
    const noDefaults = ps2({
      versions: [{ id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 28, defaultCheats: [] }],
    });
    const { container } = render(<GameCard game={noDefaults} onStart={vi.fn()} creating={0} />);
    expect(screen.getByText(/28 cheats/)).toBeTruthy();
    expect(container.querySelector(".arcade-chip--cheats-on")).toBeNull();
    expect(container.querySelector(".arcade-chip--cheats").title).toBe("28 cheats available for this version");
  });

  it("shows selected AND available once a cheat is on, and fills the chip in", () => {
    const withDefaults = ps2({
      versions: [{ id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 28, defaultCheats: ["c1", "c2"] }],
    });
    const { container } = render(<GameCard game={withDefaults} onStart={vi.fn()} creating={0} />);
    expect(screen.getByText(/2 of 28/)).toBeTruthy();
    expect(container.querySelector(".arcade-chip--cheats-on")).toBeTruthy();
    expect(container.querySelector(".arcade-chip--cheats").title).toBe("2 of 28 cheats on — click to change");
  });

  it("singularizes a lone cheat", () => {
    const one = ps2({
      versions: [{ id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 1, defaultCheats: [] }],
    });
    const { container } = render(<GameCard game={one} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-chip--cheats").title).toBe("1 cheat available for this version");
  });

  // The whole point of shipping defaultCheats with the card: a player who never opens the picker still
  // gets the widescreen patch. If this regresses, PS2 rooms silently launch in 4:3.
  it("launches with the version's default cheats even though the picker was never opened", () => {
    const onStart = vi.fn();
    render(<GameCard game={ps2()} onStart={onStart} creating={0} />);
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(11, "God of War", ["c500"]);
  });

  // Cheat ids belong to one ROM. Carrying a selection across a version switch would send a USA code to a
  // Japanese dump — a memory poke at the wrong address, not a clean no-op.
  it("resets the cheat selection to the new version's defaults when the version changes", () => {
    const onStart = vi.fn();
    const twoVersions = ps2({
      versionCount: 2,
      versions: [
        { id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 2, defaultCheats: ["c500"] },
        { id: 12, label: "Japan", region: "Japan", maxPlayers: 1, cheatCount: 1, defaultCheats: [] },
      ],
    });
    const { rerender } = render(<GameCard game={twoVersions} onStart={onStart} creating={0} />);
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenLastCalledWith(11, "God of War", ["c500"]);

    // Simulate the filter changing the card's default version (the same path that re-keys `sel`).
    const jpFirst = { ...twoVersions, versions: [twoVersions.versions[1], twoVersions.versions[0]] };
    rerender(<GameCard game={jpFirst} onStart={onStart} creating={0} />);
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenLastCalledWith(12, "God of War", []);
  });

  it("shows the rating badge only when the game is rated", () => {
    const { container, rerender } = render(<GameCard game={game()} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-card__rating").textContent).toContain("95");
    rerender(<GameCard game={game({ rating: null })} onStart={vi.fn()} creating={0} />);
    expect(container.querySelector(".arcade-card__rating")).toBeNull();
  });
});

describe("coverBox — the art's exact, bounded box", () => {
  it("gives a portrait cover the full height; the width follows its shape", () => {
    expect(coverBox(3 / 4, 168, 150)).toEqual({ width: "126px", height: "168px" });
  });

  it("width-limits a landscape cover and brings its HEIGHT down to keep the aspect exact", () => {
    // 4:3 at 168 tall would be 224 wide — wider than the details column can spare. Capped at 150, the
    // height must come down to 113 (150 ÷ 4/3) or the art would be stretched.
    expect(coverBox(4 / 3, 168, 150)).toEqual({ width: "150px", height: "113px" });
  });

  it("takes the full height when no cap is given (the Live-rooms thumbnail)", () => {
    expect(coverBox(4 / 3, 64)).toEqual({ width: "85px", height: "64px" });
  });

  it("falls back to the jewel-case shape when the aspect isn't known yet", () => {
    expect(coverBox(null, 100)).toEqual({ width: "75px", height: "100px" });
    expect(coverBox(0, 100)).toEqual({ width: "75px", height: "100px" });
  });
});

describe("GameCover — natural aspect on a shared height", () => {
  beforeEach(() => { window.sessionStorage.clear(); });

  it("pins the height and adopts the cover's measured aspect ratio", () => {
    const { container } = render(<GameCover game={game()} height={168} maxWidth={150} />);
    const box = container.querySelector(".arcade-cover");
    // Unmeasured: reserves the 3:4 jewel-case shape at the full height.
    expect(box.style.height).toBe("168px");
    expect(box.style.width).toBe("126px");

    // A 4:3 landscape box reports its natural size on load → the tile snaps to its true shape (never
    // cropped, never stretched), width-limited, so its height comes down to match.
    const img = container.querySelector(".arcade-cover__img");
    Object.defineProperty(img, "naturalWidth", { value: 640 });
    Object.defineProperty(img, "naturalHeight", { value: 480 });
    fireEvent.load(img);
    expect(box.style.width).toBe("150px");
    expect(box.style.height).toBe("113px");
  });

  it("never lets the art size itself from the card (the bug that blew the cards apart)", () => {
    // Every dimension is an absolute px value computed from the constants — no percentages, which
    // inside an indefinite-height flex item resolve to the image's intrinsic size and make the art
    // and the card each take their height from the other.
    const { container } = render(<GameCover game={game()} height={168} maxWidth={150} />);
    const box = container.querySelector(".arcade-cover");
    expect(box.style.height).toMatch(/^\d+px$/);
    expect(box.style.width).toMatch(/^\d+px$/);
  });

  it("remembers a measured aspect so the tile never re-settles", () => {
    const { container, unmount } = render(<GameCover game={game()} height={140} />);
    const img = container.querySelector(".arcade-cover__img");
    Object.defineProperty(img, "naturalWidth", { value: 500 });
    Object.defineProperty(img, "naturalHeight", { value: 700 });
    fireEvent.load(img);
    unmount();

    const { container: second } = render(<GameCover game={game()} height={140} />);
    expect(second.querySelector(".arcade-cover").style.width).toBe("100px"); // 140 × 5/7
  });

  it("ignores a broken decode rather than pinning a 0-width tile", () => {
    expect(rememberCoverAspect(999, 0, 0)).toBeNull();
    expect(getCoverAspect(999)).toBeNull();
  });

  it("falls back to a labelled placeholder for a system with no box art", () => {
    const { container } = render(<GameCover game={game({ system: "naomi", hasBoxArt: false })} height={168} maxWidth={150} />);
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
