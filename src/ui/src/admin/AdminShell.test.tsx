import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, useLocation } from "react-router-dom";
import AdminShell, { adminTabHref, readAdminTab, visibleTabs, type AdminTabDef } from "./AdminShell";
import { AdminStats, NeedsAttention, attentionRows } from "./AdminOverview";

// The site's one operator shell (R9 S6): tabs by `?tab=`, a removed tab is GONE not disabled, only
// the active body mounts, a non-admin gets the plate, and an Overview row links to the tab that
// fixes it.

global.IS_REACT_ACT_ENVIRONMENT = true;
(global as unknown as { matchMedia: unknown }).matchMedia = (global as unknown as { matchMedia?: unknown }).matchMedia || ((q: string) => ({
  matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));
(global as unknown as { ResizeObserver: unknown }).ResizeObserver = (global as unknown as { ResizeObserver?: unknown }).ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

afterEach(cleanup);

let seen = "";
function Spy() {
  const l = useLocation();
  seen = `${l.pathname}${l.search}`;
  return null;
}

const TABS: AdminTabDef[] = [
  { key: "overview", label: "Overview", render: () => <div>OVERVIEW BODY</div> },
  { key: "users", label: "Users", render: () => <div>USERS BODY</div> },
  { key: "secret", label: "Secret", render: () => <div>SECRET BODY</div>, when: false },
];

function mount(url: string, tabs = TABS, allowed = true) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Spy />
      <Route path="/movies/admin">
        <AdminShell section="movies" eyebrow="Movie administration" tabs={tabs} allowed={allowed} />
      </Route>
    </MemoryRouter>
  );
}

describe("admin/AdminShell", () => {
  it("lands on the first tab, mounts only its body, and hides a tab whose `when` is false", () => {
    mount("/movies/admin");
    expect(screen.getByRole("tab", { name: "Overview" })).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Users" })).toBeTruthy();
    expect(screen.queryByRole("tab", { name: "Secret" })).toBeNull();
    expect(screen.getByText("OVERVIEW BODY")).toBeTruthy();
    expect(screen.queryByText("USERS BODY")).toBeNull();
  });

  it("reads the tab from `?tab=` and writes it back on a click", () => {
    mount("/movies/admin?tab=users");
    expect(screen.getByText("USERS BODY")).toBeTruthy();
    fireEvent.click(screen.getByRole("tab", { name: "Overview" }));
    expect(seen).toBe("/movies/admin?tab=overview");
    expect(screen.getByText("OVERVIEW BODY")).toBeTruthy();
  });

  it("falls back to the first visible tab when `?tab=` names one that is not there", () => {
    mount("/movies/admin?tab=secret");
    expect(screen.getByText("OVERVIEW BODY")).toBeTruthy();
    expect(readAdminTab("?tab=secret", TABS)).toBe("overview");
    expect(readAdminTab("?tab=users", TABS)).toBe("users");
    expect(visibleTabs(TABS).map((t) => t.key)).toEqual(["overview", "users"]);
    expect(adminTabHref("/movies/admin", "users")).toBe("/movies/admin?tab=users");
  });

  it("refuses a member with a plate instead of the tabs (the API is the real gate)", () => {
    mount("/movies/admin", TABS, false);
    expect(screen.queryByRole("tab", { name: "Overview" })).toBeNull();
    expect(screen.getByText("Administrators only")).toBeTruthy();
  });
});

describe("admin/AdminOverview — the report", () => {
  it("shows only rows that need attention, and an unknown count says so", () => {
    expect(attentionRows([
      { key: "a", label: "clear", count: 0 },
      { key: "b", label: "pending", count: 3 },
      { key: "c", label: "no source", count: null },
      { key: "d", label: "standing", count: 0, always: true },
    ]).map((r) => r.key)).toEqual(["b", "c", "d"]);
  });

  it("a row names the tab that fixes it and navigates there", () => {
    render(
      <MemoryRouter initialEntries={["/movies/admin?tab=overview"]}>
        <Spy />
        <Route path="/movies/admin">
          <>
            <AdminStats stats={[{ label: "Titles", value: 12 }]} />
            <NeedsAttention basePath="/movies/admin" rows={[{ key: "p", label: "Pending review", count: 4, tab: "review-ingest" }]} />
          </>
        </Route>
      </MemoryRouter>
    );
    fireEvent.click(screen.getByText("Pending review"));
    expect(seen).toBe("/movies/admin?tab=review-ingest");
  });

  it("says so when nothing is wrong", () => {
    render(
      <MemoryRouter initialEntries={["/movies/admin"]}>
        <NeedsAttention basePath="/movies/admin" rows={[{ key: "p", label: "Pending review", count: 0 }]} />
      </MemoryRouter>
    );
    expect(screen.getByText("Nothing needs attention.")).toBeTruthy();
  });
});
