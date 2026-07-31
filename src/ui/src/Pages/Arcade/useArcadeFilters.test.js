import { render, cleanup, waitFor } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

const getArcadeFilters = vi.fn();
vi.mock("../../MovieAPI", () => ({ MovieAPI: { getArcadeFilters: (...a) => getArcadeFilters(...a) } }));

const useArcadeFilters = (await import("./useArcadeFilters")).default;

global.IS_REACT_ACT_ENVIRONMENT = true;

const ok = (body) => Promise.resolve({ ok: true, json: () => Promise.resolve(body) });
const FACETS = { total: 9, systems: [{ value: "snes", count: 9 }] };

function Probe({ filters, onFacets }) {
  onFacets(useArcadeFilters(filters));
  return null;
}

beforeEach(() => getArcadeFilters.mockReset());
afterEach(cleanup);

// The cache is module-level and deliberately lives as long as the session, so it is shared BETWEEN
// these tests too. Each test therefore uses its own system code as a scope, which keeps them
// independent without needing a cache-reset hatch that production code would never call.

describe("useArcadeFilters", () => {
  it("serves two consumers of the same scope from ONE request", async () => {
    // This is the whole reason the hook exists: the navbar rail and the console carousel both want
    // the identical facets, and the facet query counts distinct cards across the catalog.
    getArcadeFilters.mockImplementation(() => ok(FACETS));
    const seen = [];
    const filters = { system: "", genre: "" };

    render(<><Probe filters={filters} onFacets={(f) => seen.push(f)} />
      <Probe filters={filters} onFacets={(f) => seen.push(f)} /></>);

    await waitFor(() => expect(seen.at(-1)).toEqual(FACETS));
    expect(getArcadeFilters).toHaveBeenCalledTimes(1);
  });

  it("refetches when the scope actually changes", async () => {
    getArcadeFilters.mockImplementation(() => ok(FACETS));
    const { rerender } = render(<Probe filters={{ system: "snes" }} onFacets={() => {}} />);
    await waitFor(() => expect(getArcadeFilters).toHaveBeenCalledTimes(1));

    rerender(<Probe filters={{ system: "snes,genesis" }} onFacets={() => {}} />);
    await waitFor(() => expect(getArcadeFilters).toHaveBeenCalledTimes(2));
  });

  it("ignores sort and paging, which don't change what's available", async () => {
    getArcadeFilters.mockImplementation(() => ok(FACETS));
    const { rerender } = render(<Probe filters={{ system: "vectrex", sort: "rating" }} onFacets={() => {}} />);
    await waitFor(() => expect(getArcadeFilters).toHaveBeenCalledTimes(1));

    // A pager click must not re-run the facet query.
    rerender(<Probe filters={{ system: "vectrex", sort: "year", skip: 120 }} onFacets={() => {}} />);
    await new Promise((r) => setTimeout(r, 0));
    expect(getArcadeFilters).toHaveBeenCalledTimes(1);
  });

  it("does not cache a failure — a flaky response must not leave the lobby facet-less", async () => {
    getArcadeFilters.mockImplementationOnce(() => Promise.reject(new Error("network")));
    const { unmount } = render(<Probe filters={{ system: "gb" }} onFacets={() => {}} />);
    await waitFor(() => expect(getArcadeFilters).toHaveBeenCalledTimes(1));
    unmount();

    getArcadeFilters.mockImplementation(() => ok(FACETS));
    const seen = [];
    render(<Probe filters={{ system: "gb" }} onFacets={(f) => seen.push(f)} />);
    await waitFor(() => expect(seen.at(-1)).toEqual(FACETS));
    expect(getArcadeFilters).toHaveBeenCalledTimes(2);
  });
});
