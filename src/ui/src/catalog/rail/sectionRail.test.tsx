import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, renderHook, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter, Route } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { FacetSpec } from "./facetSpec";
import SectionSiderRail from "./SectionSiderRail";
import sectionRailSurfaces from "./sectionRailSurfaces";
import useSectionRail from "./useSectionRail";

// The pieces every section's rail is now made of: `useSectionRail` (the URL state + the option
// lists + the saved searches + "is the view grouped"), `SectionSiderRail` (the sider column — which
// on a phone is what the nav drawer draws) and `sectionRailSurfaces` (the page's chips + the bar's
// search). The phone Filters pill and the full-page sheet were deleted on 2026-08-28: the drawer
// holds the rail, so the pill offered the same options a second time.

const spec: FacetSpec = {
  identity: "test:1",
  noun: "things",
  text: true,
  facets: [{ key: "genre", token: "genre", label: "Genre", one: "Genre", valueType: "string", defaultOpen: true }],
  loadFacets: async () => ({ genre: [{ value: "Crime", label: "Crime", count: 7 }] }),
};

let isMobile = false;
vi.mock("../../hooks/useIsMobile", () => ({ default: () => isMobile }));

function wrapperFor(entry: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <Route path="*">{children}</Route>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

beforeEach(() => { isMobile = false; window.localStorage.clear(); });
afterEach(() => { vi.clearAllMocks(); });

describe("useSectionRail", () => {
  it("reads the URL into a state, counts the active filters and loads the spec's options", async () => {
    const { result } = renderHook(() => useSectionRail("test", spec), { wrapper: wrapperFor("/x?f=genre:Crime&q=heat") });
    expect(result.current.state.include.genre).toEqual(["Crime"]);
    expect(result.current.state.q).toBe("heat");
    expect(result.current.activeCount).toBe(2);
    await waitFor(() => expect(result.current.facets.data?.genre).toHaveLength(1));
  });

  it("does not load the options when the section says they do not apply", () => {
    const loadFacets = vi.fn(spec.loadFacets);
    const { result } = renderHook(() => useSectionRail("test", { ...spec, loadFacets }, { facetsEnabled: false }), { wrapper: wrapperFor("/x") });
    expect(loadFacets).not.toHaveBeenCalled();
    expect(result.current.facets.data).toBeUndefined();
  });

  it("saveCurrent stores the whole query string minus the section's entity params", () => {
    const { result } = renderHook(() => useSectionRail("test", spec, { entityParams: ["title"] }), { wrapper: wrapperFor("/x?f=genre:Crime&title=movie:12&view=wall") });
    act(() => { result.current.saveCurrent("Crime wall"); });
    expect(JSON.parse(window.localStorage.getItem("catalog.saved.v1:test")!)).toEqual([
      { id: expect.any(String), name: "Crime wall", search: "?f=genre%3ACrime&view=wall" },
    ]);
  });
});

describe("SectionSiderRail", () => {
  it("draws the rail with the count on its head line", async () => {
    const Harness = () => {
      const rail = useSectionRail("test", spec);
      return <SectionSiderRail rail={rail} total={1234} />;
    };
    const Wrapper = wrapperFor("/x");
    render(<Wrapper><Harness /></Wrapper>);
    await waitFor(() => expect(screen.getByText("1,234 things")).toBeInTheDocument());
    expect(document.querySelector(".bx-rail-on-sider .bx-railbar")).not.toBeNull();
    // On DESKTOP the sider draws no search — the page portals it into the bar's centre slot.
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("on a phone it DOES draw the search — the drawer is the sider and the phone bar has no search box", async () => {
    isMobile = true;
    const Harness = () => {
      const rail = useSectionRail("test", spec);
      return <SectionSiderRail rail={rail} total={7} />;
    };
    const Wrapper = wrapperFor("/x");
    render(<Wrapper><Harness /></Wrapper>);
    const box = screen.getByRole("combobox");
    expect(box).toBeInTheDocument();
    // At the TOP of the rail: under the head line, above the saved views and the facets.
    const rail = document.querySelector(".bx-railbar")!;
    const head = rail.querySelector(".bx-rail-top")!;
    const savedViews = rail.querySelector(".bx-rail-savedwrap")!;
    expect(head.compareDocumentPosition(box) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(savedViews.compareDocumentPosition(box) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy();
    await waitFor(() => expect(screen.getByText("7 things")).toBeInTheDocument());
  });

  it("draws a note instead of the controls when the section hands one in", () => {
    const Harness = () => {
      const rail = useSectionRail("test", spec);
      return <SectionSiderRail rail={rail} note="Browsing folders" />;
    };
    const Wrapper = wrapperFor("/x");
    render(<Wrapper><Harness /></Wrapper>);
    expect(screen.getByText("Browsing folders")).toBeInTheDocument();
    expect(document.querySelector(".bx-railbar")).toBeNull();
  });
});

describe("sectionRailSurfaces", () => {
  const Harness = () => {
    const rail = useSectionRail("test", spec);
    const { chips, surfaces } = sectionRailSurfaces(rail, isMobile, { placeholder: "A thing…" });
    return <div>{chips}{surfaces}</div>;
  };

  it("on desktop: the chips (the bar's search portals into a slot that is absent here)", () => {
    const Wrapper = wrapperFor("/x?f=genre:Crime");
    render(<Wrapper><Harness /></Wrapper>);
    expect(screen.getByText("Crime")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Clear all/ })).toBeInTheDocument();
  });

  it("on a phone: NO Filters pill and NO sheet — the drawer's rail is the section's one filter surface", () => {
    isMobile = true;
    const Wrapper = wrapperFor("/x?f=genre:Crime&q=heat");
    render(<Wrapper><Harness /></Wrapper>);
    expect(screen.queryByRole("button", { name: "Filters" })).toBeNull();
    expect(document.querySelector(".bx-filter-pill")).toBeNull();
    expect(document.querySelector(".bx-railbar-sheet")).toBeNull();
    expect(screen.queryByRole("dialog")).toBeNull();
    // The page still owns the chips over the results.
    expect(screen.getByText("Crime")).toBeInTheDocument();
  });

  it("draws no chips row content when nothing is active", () => {
    const Wrapper = wrapperFor("/x");
    render(<Wrapper><Harness /></Wrapper>);
    expect(document.querySelector(".bx-chips-row")?.childElementCount).toBe(0);
  });
});
