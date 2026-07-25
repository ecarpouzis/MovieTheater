import { render, screen, within, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

import GameCard from "./GameCard";
import GameModal from "./GameModal";
import GameCover, { coverBox } from "./GameCover";
import LiveRooms from "./LiveRooms";
import RecentlyPlayed from "./RecentlyPlayed";
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
    render(<GameCard game={game({ genres: "Action; Adventure" })} onOpen={vi.fn()} />);
    expect(screen.getByText("Action")).toBeTruthy();
    expect(screen.queryByText("Action; Adventure")).toBeNull();

    cleanup();
    render(<GameCard game={game({ genres: "Shooter, Tactical, Adventure" })} onOpen={vi.fn()} />);
    expect(screen.getByText("Shooter")).toBeTruthy();
  });

  it("puts every tag on ONE line", () => {
    const { container } = render(<GameCard game={game()} onOpen={vi.fn()} />);
    const lines = container.querySelectorAll(".arcade-tags");
    expect(lines).toHaveLength(1);
    expect(within(lines[0]).getByText("Nintendo 64")).toBeTruthy();
    expect(within(lines[0]).getByText("4P")).toBeTruthy();
    expect(within(lines[0]).getByText("USA")).toBeTruthy();
    expect(within(lines[0]).getByText("Shooter")).toBeTruthy();
  });

  it("keeps the tag line even when a game has no region or genre", () => {
    const bare = game({ genres: null, versions: [{ id: 7, label: "x", region: "Unknown", maxPlayers: 1 }] });
    const { container } = render(<GameCard game={bare} onOpen={vi.fn()} />);
    expect(container.querySelectorAll(".arcade-tags")).toHaveLength(1);
    expect(screen.queryByText("Unknown")).toBeNull();
  });

  // The card is now a pure display tile: no version picker, no cheats, no Start button, no My saves —
  // all of that moved to the modal. Clicking anywhere on the card opens it.
  it("carries no launch controls — they moved to the modal", () => {
    const { container } = render(<GameCard game={game()} onOpen={vi.fn()} />);
    expect(container.querySelector(".arcade-card__controls")).toBeNull();
    expect(container.querySelector(".arcade-card__actions")).toBeNull();
    expect(container.querySelector(".arcade-chip--cheats")).toBeNull();
    expect(screen.queryByText(/Start room/)).toBeNull();
    expect(screen.queryByText("My saves")).toBeNull();
  });

  it("opens the modal with its game when the card is clicked", () => {
    const onOpen = vi.fn();
    const g = game();
    const { container } = render(<GameCard game={g} onOpen={onOpen} />);
    fireEvent.click(container.querySelector(".arcade-card"));
    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen).toHaveBeenCalledWith(g);
  });

  it("opens the modal on keyboard activation (Enter), since the card is a button", () => {
    const onOpen = vi.fn();
    const { container } = render(<GameCard game={game()} onOpen={onOpen} />);
    fireEvent.keyDown(container.querySelector(".arcade-card"), { key: "Enter" });
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  // The space the launch controls left is filled by the summary and a year · studio foot line, so the
  // details column reaches the bottom of the art instead of trailing off under a 2-line summary.
  it("closes the card with year · studio, and flags a multi-version card", () => {
    const { container, rerender } = render(
      <GameCard game={game({ year: 1997, developer: "Rare", versionCount: 3 })} onOpen={vi.fn()} />);
    expect(container.querySelector(".arcade-card__credit").textContent).toBe("1997 · Rare");
    expect(container.querySelector(".arcade-card__versions").textContent).toContain("3 versions");

    // Publisher stands in when there's no developer; a single-version card says nothing about versions.
    rerender(<GameCard game={game({ year: null, developer: null, publisher: "Nintendo" })} onOpen={vi.fn()} />);
    expect(container.querySelector(".arcade-card__credit").textContent).toBe("Nintendo");
    expect(container.querySelector(".arcade-card__versions")).toBeNull();
  });

  it("shows the rating badge only when the game is rated", () => {
    const { container, rerender } = render(<GameCard game={game()} onOpen={vi.fn()} />);
    expect(container.querySelector(".arcade-card__rating").textContent).toContain("95");
    rerender(<GameCard game={game({ rating: null })} onOpen={vi.fn()} />);
    expect(container.querySelector(".arcade-card__rating")).toBeNull();
  });
});

describe("GameModal", () => {
  const ps2 = (over = {}) => game({
    key: "ps2|God of War", title: "God of War", system: "ps2", versionCount: 1,
    versions: [{ id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 2, defaultCheats: ["c500"] }],
    ...over,
  });

  const renderModal = (g, props = {}) =>
    render(<GameModal game={g} onStart={props.onStart || vi.fn()} onManageSaves={props.onManageSaves || vi.fn()}
      onClose={vi.fn()} creating={0} initialVersionId={props.initialVersionId} />);

  // A "Recently played" tile hands over the version its save belongs to; the modal opens on that one
  // instead of the card default. An id that isn't one of this card's versions is ignored.
  it("honours the version a recently-played tile opened it on", () => {
    const onStart = vi.fn();
    const twoVersions = game({
      versionCount: 2,
      versions: [{ id: 7, label: "USA", region: "USA" }, { id: 9, label: "Japan", region: "Japan" }],
    });
    renderModal(twoVersions, { onStart, initialVersionId: 9 });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(9, "007 - GoldenEye", [], "", "", "");

    cleanup();
    renderModal(twoVersions, { onStart, initialVersionId: 12345 });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenLastCalledWith(7, "007 - GoldenEye", [], "", "", "");
  });

  it("renders the Start room button and the My saves link", () => {
    renderModal(game());
    expect(screen.getByText(/Start room/)).toBeTruthy();
    expect(screen.getByText(/My saves/)).toBeTruthy();
  });

  // A single-version, no-cheat, no-scheme game has nothing to configure, so the controls block is absent.
  it("omits the controls block for a plain single-version game with no cheats", () => {
    renderModal(game());
    expect(document.querySelector(".agm-controls")).toBeNull();
  });

  it("starts a room with the selected version id (plus hwContext + controllerScheme slots)", () => {
    const onStart = vi.fn();
    renderModal(game(), { onStart });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledTimes(1);
    // A plain game: no hw toggle ("") and no controller scheme ("").
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye", [], "", "", "");
  });

  it("opens the saves manager for the selected version, with its title", () => {
    const onManageSaves = vi.fn();
    const onStart = vi.fn();
    renderModal(game(), { onManageSaves, onStart });
    fireEvent.click(screen.getByText(/My saves/));
    expect(onManageSaves).toHaveBeenCalledWith(7, "007 - GoldenEye");
    expect(onStart).not.toHaveBeenCalled();
  });

  it("shows the cheat picker when the version has cheats, and hides it otherwise", () => {
    const { rerender } = renderModal(ps2());
    expect(document.querySelector(".agm-cheat-select")).toBeTruthy();
    rerender(<GameModal game={game()} onStart={vi.fn()} onManageSaves={vi.fn()} onClose={vi.fn()} creating={0} />);
    expect(document.querySelector(".agm-cheat-select")).toBeNull();
  });

  // Cheats are codes-only now, with nothing pre-selected: a player who never opens the picker launches
  // with no cheats. The PS2 widescreen patch is per-game config applied SERVER-side at Start (not shipped
  // as a card default), so an untouched launch sends an empty cheat list.
  it("launches with no cheats when the picker was never opened", () => {
    const onStart = vi.fn();
    renderModal(ps2(), { onStart });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(11, "God of War", [], "", "", "");
  });

  // Cheat ids belong to one ROM. Carrying a selection across a version switch would send a USA code to a
  // Japanese dump — a memory poke at the wrong address. The selection clears on a version change.
  it("clears the cheat selection when the version changes", () => {
    const onStart = vi.fn();
    const twoVersions = ps2({
      versionCount: 2,
      versions: [
        { id: 11, label: "USA", region: "USA", maxPlayers: 1, cheatCount: 2 },
        { id: 12, label: "Japan", region: "Japan", maxPlayers: 1, cheatCount: 1 },
      ],
    });
    const { rerender } = renderModal(twoVersions, { onStart });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenLastCalledWith(11, "God of War", [], "", "", "");

    // Simulate a filter changing the default version (same path that re-keys `sel`).
    const jpFirst = { ...twoVersions, versions: [twoVersions.versions[1], twoVersions.versions[0]] };
    rerender(<GameModal game={jpFirst} onStart={onStart} onManageSaves={vi.fn()} onClose={vi.fn()} creating={0} />);
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenLastCalledWith(12, "God of War", [], "", "", "");
  });

  // The play button now defaults to "" (auto) so the server applies the game's configured renderer or the
  // system default; Force Vulkan / Start GL Core in the menu are the per-launch overrides.
  it("sends auto renderer on the default start for a hw-toggle system", () => {
    const onStart = vi.fn();
    renderModal(game({ supportsHwToggle: true }), { onStart });
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye", [], "", "", "");
  });

  // The Wii GameCube/Nunchuk picker is offered on every Wii title now; the server hands each game its
  // default via defaultControllerScheme, and an untouched Start launches on it.
  it("defaults a GC-native Wii title to the GameCube scheme", () => {
    const onStart = vi.fn();
    renderModal(game({ supportsControllerScheme: true, defaultControllerScheme: "gc" }), { onStart });
    expect(document.querySelector(".agm-controls")).toBeTruthy();
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye", [], "", "gc", "");
  });

  it("defaults a normal Wii title to Wiimote+Nunchuk", () => {
    const onStart = vi.fn();
    renderModal(game({ supportsControllerScheme: true, defaultControllerScheme: "wiimote" }), { onStart });
    expect(document.querySelector(".agm-controls")).toBeTruthy();
    fireEvent.click(screen.getByText(/Start room/));
    expect(onStart).toHaveBeenCalledWith(7, "007 - GoldenEye", [], "", "wiimote", "");
  });
});

describe("RecentlyPlayed", () => {
  const row = (over = {}) => ({
    game: game({ key: "n64|GoldenEye", versions: [{ id: 7, label: "USA" }, { id: 8, label: "Japan" }] }),
    lastPlayedUtc: new Date(Date.now() - 3 * 3600 * 1000).toISOString(),
    saveCount: 2,
    playedVersionId: 8,
    ...over,
  });

  it("renders nothing when the player has no history", () => {
    const { container } = render(<RecentlyPlayed rows={[]} onOpen={vi.fn()} />);
    expect(container.firstChild).toBeNull();
  });

  // The tile is a pure display tile now, exactly like a grid card: Continue and My saves duplicated the
  // modal (and skipped its version/cheat/renderer choices), so they're gone.
  it("carries no Continue or My saves button", () => {
    const { container } = render(<RecentlyPlayed rows={[row()]} onOpen={vi.fn()} />);
    expect(container.querySelector("button")).toBeNull();
    expect(screen.queryByText(/Continue/)).toBeNull();
    expect(screen.queryByText(/My saves/)).toBeNull();
    expect(screen.getByText("3h ago")).toBeTruthy();
  });

  // Saves are keyed on the ROM row, so the tile hands the modal the version whose save it advertised —
  // otherwise Start would look for a save on the card's default version and find none.
  it("opens the game modal on the version the save belongs to", () => {
    const onOpen = vi.fn();
    const r = row();
    const { container } = render(<RecentlyPlayed rows={[r]} onOpen={onOpen} />);
    fireEvent.click(container.querySelector(".arcade-recent__card"));
    expect(onOpen).toHaveBeenCalledWith(r.game, 8);
  });

  it("opens on keyboard activation, since the tile is a button", () => {
    const onOpen = vi.fn();
    const { container } = render(<RecentlyPlayed rows={[row()]} onOpen={onOpen} />);
    fireEvent.keyDown(container.querySelector(".arcade-recent__card"), { key: "Enter" });
    expect(onOpen).toHaveBeenCalledTimes(1);
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
