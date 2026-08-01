import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

import ConsoleCarousel from "./ConsoleCarousel";
import { parseSystems, serializeSystems, toggleSystem } from "./arcadeSystemFilter";
import { SYSTEM_LABEL, SYSTEM_RELEASED, EVERGREEN_SYSTEMS, byConsoleAge } from "./arcadeSystems";

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const systems = [
  { value: "snes", count: 2223 },
  { value: "genesis", count: 1232 },
  { value: "n64", count: 525 },
];

afterEach(cleanup);

describe("arcadeSystemFilter", () => {
  it("reads a legacy single-value ?system= as a one-element selection", () => {
    // Links minted before the filter went multi-select must keep meaning what they meant.
    expect(parseSystems("?system=nes")).toEqual(["nes"]);
  });

  it("round-trips a multi-system selection through the URL", () => {
    expect(parseSystems("?system=snes,genesis")).toEqual(["snes", "genesis"]);
    expect(serializeSystems(["snes", "genesis"])).toBe("snes,genesis");
  });

  it("treats an absent or empty param as 'all systems', not as a selection of nothing", () => {
    expect(parseSystems("")).toEqual([]);
    expect(parseSystems("?system=")).toEqual([]);
    expect(serializeSystems([])).toBe("");
  });

  it("tolerates the whitespace and casing a hand-edited URL can carry", () => {
    expect(parseSystems("?system=SNES, Genesis ,,")).toEqual(["snes", "genesis"]);
  });

  it("toggles without reordering, so tiles never reshuffle under the cursor", () => {
    expect(toggleSystem(["snes"], "genesis")).toEqual(["snes", "genesis"]);
    expect(toggleSystem(["snes", "genesis", "n64"], "genesis")).toEqual(["snes", "n64"]);
  });
});

describe("console release dates", () => {
  // A system that reaches the shelf without a date sorts to the far end, where nobody will look for
  // it. Anything we bothered to name is something we ship, so it needs one — unless it's evergreen,
  // which is a claim about having no single release date rather than about not knowing it.
  it("dates every system the lobby can name", () => {
    const undated = Object.keys(SYSTEM_LABEL).filter((s) => !EVERGREEN_SYSTEMS.has(s) && !SYSTEM_RELEASED[s]);
    expect(undated).toEqual([]);
  });

  it("orders newest to oldest across the generations", () => {
    const shelf = ["nes", "ps2", "switch", "gb", "arcade", "n64"].sort(byConsoleAge);
    expect(shelf).toEqual(["switch", "ps2", "n64", "gb", "nes", "arcade"]);
  });

  // PC heads the shelf: it isn't a 1981 machine we emulate, it's the current one. Dating it by the
  // IBM PC would file the newest platform we stream between the Atari 7800 and the Intellivision.
  it("puts the evergreen platforms ahead of the newest console", () => {
    expect(["nes", "switch", "pc", "arcade"].sort(byConsoleAge)).toEqual(["pc", "switch", "nes", "arcade"]);
  });
});

describe("ConsoleCarousel", () => {
  it("adds a console to the selection rather than replacing it", () => {
    const onToggle = vi.fn();
    render(<ConsoleCarousel systems={systems} selected={["snes"]} onToggle={onToggle} onClear={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: /Genesis/ }));
    expect(onToggle).toHaveBeenCalledWith("genesis");
  });

  it("marks the picked consoles pressed and leaves the rest unpressed", () => {
    render(<ConsoleCarousel systems={systems} selected={["snes", "n64"]} onToggle={vi.fn()} onClear={vi.fn()} />);

    expect(screen.getByRole("button", { name: /SNES/ }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: /Genesis/ }).getAttribute("aria-pressed")).toBe("false");
  });

  it("summarises the picked consoles, and offers Clear only once something is picked", () => {
    const { rerender } = render(
      <ConsoleCarousel systems={systems} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    expect(screen.getByText("3 systems · newest first")).toBeTruthy();
    expect(screen.queryByText("Clear consoles")).toBeNull();

    rerender(<ConsoleCarousel systems={systems} selected={["snes", "n64"]} onToggle={vi.fn()} onClear={vi.fn()} />);
    // 2223 + 525 — the count comes from the facets, so it survives the grid still loading.
    expect(screen.getByText("2 selected · 2,748 games")).toBeTruthy();
    expect(screen.getByText("Clear consoles")).toBeTruthy();
  });

  it("renders nothing at all before the facets arrive", () => {
    // The lobby renders this above the grid on first paint; an undefined facet list must not throw.
    const { container } = render(
      <ConsoleCarousel systems={undefined} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    expect(container.textContent).toBe("");
  });

  it("still gives a usable button for a system whose tile art hasn't been built yet", () => {
    render(<ConsoleCarousel systems={[{ value: "bogus", count: 3 }]} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    // No committed tile for "bogus" → the label stands in for the art instead of a broken image.
    expect(screen.getByRole("button", { name: /BOGUS/ }).querySelector("img")).toBeNull();
  });

  // Newest hardware first, so scrolling right walks backwards through the generations. Catalog size
  // is deliberately NOT the order: nobody can guess where the Dreamcast sits in a popularity ranking.
  it("shelves the consoles newest-first, whatever their catalog size", () => {
    const { container } = render(
      <ConsoleCarousel systems={systems} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    const order = [...container.querySelectorAll(".arcade-console__name")].map((n) => n.textContent);
    expect(order).toEqual(["Nintendo 64", "SNES", "Genesis"]);
  });

  // A system we have no release date for must not silently take pride of place at the head of the
  // shelf — an unknown is not a new console.
  it("puts an undated system last rather than first", () => {
    const { container } = render(
      <ConsoleCarousel systems={[{ value: "bogus", count: 3 }, ...systems]} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    const order = [...container.querySelectorAll(".arcade-console__name")].map((n) => n.textContent);
    expect(order.at(-1)).toBe("BOGUS");
  });
});

describe("ConsoleCarousel — the streamed lane", () => {
  const withStreamed = [...systems, { value: "switch", count: 3 }, { value: "pc", count: 1 }];

  beforeEach(() => { try { localStorage.clear(); } catch { /* private mode */ } });

  it("hides the streamed systems until asked, and says how many are behind the box", () => {
    const { container } = render(
      <ConsoleCarousel systems={withStreamed} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);

    expect(screen.queryByRole("button", { name: /Switch/ })).toBeNull();
    expect(container.querySelectorAll(".arcade-console")).toHaveLength(3);

    const box = screen.getByLabelText(/Show streamed systems \(2\)/);
    expect(box.checked).toBe(false);
    fireEvent.click(box);

    // Shown, and in shelf order: PC (evergreen) heads it, then hardware newest-first.
    const order = [...container.querySelectorAll(".arcade-console__name")].map((n) => n.textContent);
    expect(order).toEqual(["PC", "Switch", "Nintendo 64", "SNES", "Genesis"]);
  });

  // A filter you can see the effect of but can't switch off is a trap, and ?system=switch is
  // bookmarkable — so a SELECTED streamed system stays on the shelf whatever the checkbox says.
  it("keeps a streamed system visible while it is selected", () => {
    render(<ConsoleCarousel systems={withStreamed} selected={["switch"]} onToggle={vi.fn()} onClear={vi.fn()} />);
    expect(screen.getByRole("button", { name: /Switch/ }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.queryByRole("button", { name: /^PC/ })).toBeNull();
  });

  it("offers no checkbox at all when there is nothing streamed to show", () => {
    render(<ConsoleCarousel systems={systems} selected={[]} onToggle={vi.fn()} onClear={vi.fn()} />);
    expect(screen.queryByText(/Show streamed systems/)).toBeNull();
  });
});
