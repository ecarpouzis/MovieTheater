import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import ArcadeNavContent from "./ArcadeNavContent";

vi.mock("../Pages/Arcade/ArcadeSiderRail", () => ({ default: () => <div data-testid="facet-rail" /> }));
vi.mock("./navShared", async (orig) => ({
  ...(await orig()),
  NavUserBlock: () => <div data-testid="user-block" />,
}));

const USER = { username: "eric", hasPassword: true };

function renderRail(path, userData = USER) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <ArcadeNavContent userData={userData} onUserLoggedIn={() => {}} setSettingsModalOpen={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/**
 * Saves and Trophies are member surfaces — both endpoints behind them are scoped to the signed-in
 * user — but the only way to either used to be a small button on the lobby's bar and a card on the
 * ADMIN shell. They belong in the section rail, where the movies rail keeps Seen · Want · Rate.
 */
describe("NavBar/ArcadeNavContent", () => {
  it("lists the viewer's own two surfaces above the facet rail", () => {
    renderRail("/arcade");
    expect(screen.getByRole("button", { name: /my saves/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /trophies/i })).toBeTruthy();
    expect(screen.getByTestId("facet-rail")).toBeTruthy();
  });

  it("offers neither to a signed-out visitor", () => {
    renderRail("/arcade", null);
    expect(screen.queryByRole("button", { name: /my saves/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /trophies/i })).toBeNull();
  });

  it("marks the row you are on", () => {
    renderRail("/arcade/saves");
    expect(screen.getByRole("button", { name: /my saves/i }).getAttribute("aria-current")).toBe("page");
    expect(screen.getByRole("button", { name: /trophies/i }).getAttribute("aria-current")).toBeNull();
  });

  it("keeps the lobby's facet rail off the pages that are not the lobby", () => {
    renderRail("/arcade/trophies");
    expect(screen.queryByTestId("facet-rail")).toBeNull();
    expect(screen.getByRole("button", { name: /trophies/i }).getAttribute("aria-current")).toBe("page");
  });
});
