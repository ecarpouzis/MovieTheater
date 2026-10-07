import { render, cleanup, act, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// /requests — the communal "please add this" queue. What these pin:
//   * a guest sees the compose card disabled with a sign-in line and no list;
//   * a member sees every user's rows in the table, their own name lit, a vote they can cast on
//     someone else's row and a Withdraw on their own;
//   * an admin sees the matcher's proposal with Confirm / Not it, the attention banner, and
//     Added / Decline on open rows — a member sees none of those verbs;
//   * filing a request posts the section + title and prepends the server's row.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));

const json = (body, status = 200) => Promise.resolve({ ok: status < 400, status, json: () => Promise.resolve(body) });

let rows = [];
const api = {
  getRequests: vi.fn(() => json({ requests: rows, totalCount: rows.length, nextBeforeId: null })),
  getRequestsSummary: vi.fn(() => json({ open: rows.length, needsConfirmation: rows.filter((r) => r.match).length, mine: 0, bySection: {}, canResolve: false })),
  createRequest: vi.fn(),
  voteRequest: vi.fn(),
  setRequestStatus: vi.fn(),
  confirmRequestMatch: vi.fn(),
  dismissRequestMatch: vi.fn(),
  sweepRequests: vi.fn(),
  deleteRequest: vi.fn(),
};
vi.mock("../../MovieAPI", () => ({ MovieAPI: api }));
vi.mock("../../hooks/useIsMobile", () => ({ default: () => false }));
// antd's static `message` renders on a timer after the call and logs its "static function" notice
// when it does — in a test that lands AFTER teardown, which vitest reports as an unhandled error.
// The toasts are not what these tests pin, so they are stubbed.
vi.mock("antd", async (importOriginal) => {
  const antd = await importOriginal();
  return { ...antd, message: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), info: vi.fn() } };
});

const RequestsPage = (await import("./RequestsPage")).default;

const settle = () => act(async () => { await Promise.resolve(); await Promise.resolve(); });

const DUNE = {
  id: 1, createdUtc: new Date().toISOString(), section: "movies", title: "Dune", year: 1984, detail: null, link: null, notes: null,
  status: "open", requestedBy: { userId: 2, username: "sam" }, votes: 2, votedByMe: false, match: null, fulfilled: null,
};
const CATAN = {
  id: 2, createdUtc: new Date(Date.now() - 86400000 * 3).toISOString(), section: "boardgames", title: "Catan: Seafarers", year: null, detail: "Teuber", link: "https://boardgamegeek.com/x", notes: "the 5-6 player one too",
  status: "open", requestedBy: { userId: 1, username: "eric" }, votes: 0, votedByMe: false,
  match: { kind: "boardgame", id: 40, title: "Catan: Seafarers (1997)", foundUtc: new Date().toISOString() }, fulfilled: null,
};

function renderPage(userData, path = "/requests") {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <RequestsPage userData={userData} />
    </MemoryRouter>,
  );
}

beforeEach(() => { rows = [DUNE, CATAN]; Object.values(api).forEach((f) => f.mockClear()); });
afterEach(cleanup);

describe("the request queue", () => {
  it("asks a guest to sign in and fetches nothing", async () => {
    renderPage(null);
    await settle();
    expect(screen.getByText(/sign in \(the box in the rail\)/i)).toBeTruthy();
    expect(screen.getByText(/sign in to see the list/i)).toBeTruthy();
    expect(api.getRequests).not.toHaveBeenCalled();
    expect(screen.getByPlaceholderText(/movie or show title/i).disabled).toBe(true);
  });

  it("shows every user's requests, lights the viewer's own, and offers the right verbs to a member", async () => {
    renderPage({ username: "Eric", isAdmin: false });
    await settle();
    expect(api.getRequests).toHaveBeenCalledWith(expect.objectContaining({ status: "open" }));
    const table = document.querySelector(".rq-table");
    expect(table).toBeTruthy();
    expect(within(table).getByText("Dune")).toBeTruthy();
    expect(within(table).getByText("Catan: Seafarers")).toBeTruthy();
    // The viewer's own name is lit (case-insensitively — /API/Me carries the name, not an id).
    expect(document.querySelector(".rq-by--me").textContent).toBe("eric");
    // Their own row gets Withdraw; nobody's row gets the admin verbs; the proposal is shown but not actionable.
    expect(within(table).getByRole("button", { name: /withdraw/i })).toBeTruthy();
    expect(within(table).queryByRole("button", { name: /^added$/i })).toBeNull();
    expect(within(table).queryByRole("button", { name: /decline/i })).toBeNull();
    expect(within(table).getByText(/looks like it's in/i)).toBeTruthy();
    expect(within(table).queryByRole("button", { name: /confirm, it's added/i })).toBeNull();
    expect(document.querySelector(".rq-banner")).toBeNull();
  });

  it("casts a vote on someone else's row and reflects the server's tally", async () => {
    api.voteRequest.mockImplementation((id) => json({ id, votes: 3, votedByMe: true }));
    renderPage({ username: "eric" });
    await settle();
    const votes = document.querySelectorAll(".rq-vote");
    const duneVote = Array.from(votes).find((b) => !b.disabled);
    expect(duneVote).toBeTruthy();
    expect(duneVote.querySelector(".rq-vote__n").textContent).toBe("2");
    await act(async () => { fireEvent.click(duneVote); });
    await settle();
    expect(api.voteRequest).toHaveBeenCalledWith(1);
    expect(duneVote.querySelector(".rq-vote__n").textContent).toBe("3");
    expect(duneVote.classList.contains("on")).toBe(true);
  });

  it("gives an admin the banner, Confirm / Not it on a proposal, and Added / Decline on open rows", async () => {
    // The banner reads the SERVER's count; a confirm drops it, and the page re-reads the summary.
    let needs = 1;
    api.getRequestsSummary.mockImplementation(() => json({ open: 2, needsConfirmation: needs, mine: 1, bySection: {}, canResolve: true }));
    api.confirmRequestMatch.mockImplementation(() => { needs = 0; return json({ ...CATAN, status: "fulfilled", match: null, fulfilled: { kind: "boardgame", id: 40 }, resolvedBy: "eric", resolutionNote: "Matched: Catan: Seafarers (1997)" }); });
    renderPage({ username: "eric", isAdmin: true });
    await settle();
    expect(document.querySelector(".rq-banner").textContent).toMatch(/one request looks like it's been added/i);
    const table = document.querySelector(".rq-table");
    expect(within(table).getAllByRole("button", { name: /^added$/i }).length).toBe(2);
    expect(within(table).getAllByRole("button", { name: /decline/i }).length).toBe(2);
    const confirm = within(table).getByRole("button", { name: /confirm, it's added/i });
    await act(async () => { fireEvent.click(confirm); });
    await settle();
    expect(api.confirmRequestMatch).toHaveBeenCalledWith(2);
    // Fulfilled rows leave the Open view, and with it the banner.
    expect(within(table).queryByText("Catan: Seafarers")).toBeNull();
    expect(document.querySelector(".rq-banner")).toBeNull();
  });

  it("files a request for the chosen section and prepends the server's row", async () => {
    const created = { ...DUNE, id: 9, title: "Sixteen Stone", section: "music", detail: "Bush", year: 1994, requestedBy: { userId: 1, username: "eric" }, votes: 0 };
    api.createRequest.mockImplementation(() => json(created));
    renderPage({ username: "eric" });
    await settle();
    fireEvent.click(screen.getByRole("button", { name: /^music$/i }));
    fireEvent.change(screen.getByPlaceholderText(/album title/i), { target: { value: "Sixteen Stone" } });
    fireEvent.change(screen.getByPlaceholderText(/^Year/), { target: { value: "1994" } });
    fireEvent.change(screen.getByPlaceholderText(/leave the title as the artist/i), { target: { value: "Bush" } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: /add to the list/i })); });
    await settle();
    expect(api.createRequest).toHaveBeenCalledWith({ section: "music", title: "Sixteen Stone", year: 1994, detail: "Bush", link: null, notes: null });
    const firstRow = document.querySelector(".rq-table tbody tr.rq-row");
    expect(firstRow.textContent).toContain("Sixteen Stone");
  });

  it("reads the view from the URL", async () => {
    renderPage({ username: "eric" }, "/requests?status=closed&section=music");
    await settle();
    expect(api.getRequests).toHaveBeenCalledWith(expect.objectContaining({ status: "closed", section: "music" }));
    expect(screen.getByRole("button", { name: /resolved/i }).getAttribute("aria-pressed")).toBe("true");
  });
});
