import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import MusicMseProbe from "./MusicMseProbe";
import { MovieAPI } from "../MovieAPI";

// The probe page has to work when it is TAPPED, which is the one thing its first version was never
// tested for — and on the phone it turned out that every tap did nothing. Two mechanisms produced
// that: a diagnostics gate screen whose button flipped a module-level flag the component never
// re-read (so the page could never leave the gate), and run buttons that refused to do anything
// until a person had filled in track slots by hand. Both are gone; these tests are what keeps them
// gone. jsdom/happy-dom has no MediaSource, which makes the no-MSE path the deterministic one to
// assert on — and it is a real route (ladder rung 7), not a stub.

const candidates = {
  mp3: { id: 1, title: "A", extension: ".mp3", sampleRateHz: 44100, channels: 2, sizeBytes: 4000000 },
  flac: { id: 2, title: "B", extension: ".flac", sampleRateHz: 44100, channels: 2, sizeBytes: 20000000 },
  hires: null,
  mono: { id: 4, title: "D", extension: ".flac", sampleRateHz: 44100, channels: 1, sizeBytes: 9000000 },
};

function mockCandidates(body = candidates) {
  vi.spyOn(MovieAPI, "getMusicProbeCandidates").mockResolvedValue({
    ok: true,
    json: async () => body,
  });
}

describe("MusicMseProbe page", () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.restoreAllMocks();
  });

  it("is usable the moment it renders — no flag to discover, no slots to fill", async () => {
    mockCandidates();
    render(<MusicMseProbe />);

    // The regression: the page used to render exactly one button ("Turn diagnostics on") that could
    // not work. The first thing here must be the thing that runs the gate.
    const go = await screen.findByRole("button", { name: /run the whole gate/i });
    expect(go).toBeEnabled();
    expect(screen.queryByRole("button", { name: /diagnostics on/i })).toBeNull();
  });

  it("shows the server-picked candidates, and says so when a slot has none", async () => {
    mockCandidates();
    render(<MusicMseProbe />);
    await screen.findByText(/A —/);
    expect(screen.getByText(/none in this library/i)).toBeInTheDocument();
  });

  it("advances the state machine on ONE tap, and renders verdicts", async () => {
    mockCandidates();
    const user = userEvent.setup();
    render(<MusicMseProbe />);
    const go = await screen.findByRole("button", { name: /run the whole gate/i });

    // Before the tap every probe row is an em dash: nothing has been run.
    expect(screen.getAllByText("—").length).toBe(6);

    await user.click(go);

    // This environment has no MediaSource, so the honest outcome is: capability FAIL, everything
    // below it skipped with a reason — and the run must END rather than sit on a disabled button,
    // which is what "the buttons do nothing" felt like.
    await waitFor(() => {
      expect(screen.getByText(/no usable MSE treatment/i)).toBeInTheDocument();
    });
    expect(screen.getAllByText(/nothing to append into/i).length).toBeGreaterThan(0);
    expect(screen.getByText("FAIL", { selector: ".mse-probe-overall" })).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole("button", { name: /run the whole gate/i })).toBeEnabled();
    });
  });

  it("keeps its verdicts across a reload — the evidence has to outlive the page", async () => {
    mockCandidates();
    const user = userEvent.setup();
    const first = render(<MusicMseProbe />);
    await user.click(await screen.findByRole("button", { name: /run the whole gate/i }));
    await waitFor(() => expect(screen.getByText(/no usable MSE treatment/i)).toBeInTheDocument());
    first.unmount();

    render(<MusicMseProbe />);
    expect(screen.getByText(/no usable MSE treatment/i)).toBeInTheDocument();
  });

  it("survives a candidates endpoint that is unavailable", async () => {
    vi.spyOn(MovieAPI, "getMusicProbeCandidates").mockResolvedValue({ ok: false, status: 503, json: async () => ({}) });
    render(<MusicMseProbe />);
    expect(await screen.findByText(/could not fetch candidates/i)).toBeInTheDocument();
    // Still usable: a run without candidates skips the sub-probes rather than blocking the page.
    expect(screen.getByRole("button", { name: /run the whole gate/i })).toBeEnabled();
  });
});
