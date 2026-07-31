import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach } from "vitest";

import ConsoleCarousel from "./ConsoleCarousel";
import { parseSystems, serializeSystems, toggleSystem } from "./arcadeSystemFilter";

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
    expect(screen.getByText("3 systems")).toBeTruthy();
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
});
