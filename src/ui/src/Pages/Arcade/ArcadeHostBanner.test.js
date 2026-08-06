import { render, cleanup, waitFor, act, screen } from "@testing-library/react";
import { vi, describe, it, expect, afterEach, beforeEach } from "vitest";

// The banner that tells players a remote desktop session is holding the arcade PC off its own screen
// (which halves the frame rate with no error anywhere). Two behaviours are load-bearing and are what
// these pin down:
//   * it FAILS TO SILENCE — no reading, or a reading the server has stopped hearing, shows nothing;
//   * it reports the RECOVERY, so someone who saw the warning learns when it is over instead of
//     watching it silently disappear.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.matchMedia = global.matchMedia || ((q) => ({
  matches: false, media: q, onchange: null,
  addListener: vi.fn(), removeListener: vi.fn(),
  addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
}));

let status = null; // what the server is currently saying; swap it between polls

vi.mock("../../MovieAPI", () => ({
  MovieAPI: { getArcadeHostStatus: () => Promise.resolve(status) },
}));

const ArcadeHostBanner = (await import("./ArcadeHostBanner")).default;

const HEALTHY = { reported: true, degraded: false, kind: "console", stale: false };
const REMOTE = { reported: true, degraded: true, kind: "remote", stale: false };
const DISCONNECTED = { reported: true, degraded: true, kind: "disconnected", recovering: false, stale: false };

// Let the mount effect's promise settle without waiting on the poll interval.
const settle = () => act(async () => { await Promise.resolve(); });

beforeEach(() => { status = null; });
afterEach(cleanup);

describe("the arcade host-health banner", () => {
  it("says nothing when the host has never reported", async () => {
    status = null;
    const { container } = render(<ArcadeHostBanner />);
    await settle();
    expect(container.textContent).toBe("");
  });

  it("says nothing when the host is on its own console", async () => {
    status = HEALTHY;
    const { container } = render(<ArcadeHostBanner />);
    await settle();
    expect(container.textContent).toBe("");
  });

  it("warns while someone is remoted in", async () => {
    status = REMOTE;
    render(<ArcadeHostBanner />);
    await waitFor(() => expect(screen.getByText(/remoted into the arcade PC/i)).toBeTruthy());
  });

  it("warns when a remote session was closed but the console has not come back", async () => {
    status = DISCONNECTED;
    render(<ArcadeHostBanner />);
    await waitFor(() => expect(screen.getByText(/isn't on its own screen/i)).toBeTruthy());
  });

  it("says the recovery is running once the reattach has been triggered", async () => {
    status = { ...DISCONNECTED, recovering: true };
    render(<ArcadeHostBanner />);
    await waitFor(() => expect(screen.getByText(/Restoring the arcade PC/i)).toBeTruthy());
  });

  it("reports the recovery instead of just vanishing when the console comes back", async () => {
    vi.useFakeTimers();
    try {
      status = REMOTE;
      render(<ArcadeHostBanner />);
      await act(async () => { await Promise.resolve(); });
      expect(screen.getByText(/remoted into the arcade PC/i)).toBeTruthy();

      // The console is restored; the next poll picks it up.
      status = HEALTHY;
      await act(async () => { await vi.advanceTimersByTimeAsync(20000); });
      expect(screen.getByText(/Full performance restored/i)).toBeTruthy();
      expect(screen.queryByText(/remoted into the arcade PC/i)).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows the recovery note to someone who only arrives afterwards", async () => {
    // No degraded reading was ever seen by THIS page — the server's own recently-recovered window is
    // what tells a late arrival why the picture is about to get better.
    status = { ...HEALTHY, recentlyRecovered: true };
    render(<ArcadeHostBanner />);
    await waitFor(() => expect(screen.getByText(/Full performance restored/i)).toBeTruthy());
  });
});
