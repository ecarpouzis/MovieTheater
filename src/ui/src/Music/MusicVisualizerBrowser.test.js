import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { __resetPresetCaches } from "./butterchurnPresets";

// The preset picker used to be a <select> over the 100 presets that shipped inside the bundle. It's
// now a searchable panel over ~1,750 presets fetched one at a time, which turns "pick a preset" into
// an async, cancellable, cache-backed operation. These tests cover that path through the component:
// the list you see, the search that narrows it, and the fetch that actually reaches butterchurn.

const loadPreset = vi.fn();
const createVisualizer = vi.fn(() => ({
  connectAudio: vi.fn(),
  loadPreset,
  render: vi.fn(),
  setRendererSize: vi.fn(),
}));

// Exported BOTH at the top level and on .default because that is the interop shape the component's
// unwrapModule() probes for — and vitest's module mock throws on a miss rather than returning
// undefined, so a mock that only sets .default fails in a way the real UMD bundle would not.
vi.mock("butterchurn", () => {
  const createVisualizerFn = (...a) => createVisualizer(...a);
  return { createVisualizer: createVisualizerFn, default: { createVisualizer: createVisualizerFn } };
});

const INDEX = [
  { s: "geiss-waterfall", n: "Geiss - Waterfall", t: 0 },
  { s: "rovastar-fractopia", n: "Rovastar - Fractopia", t: 1 },
  { s: "flexi-dark-matter", n: "flexi - dark matter", t: 2 },
];
const PRESET = { baseVals: {}, pixel_eqs_str: "", warp: "", comp: "" };

let MusicVisualizer;
let realGetContext;
let realRaf;

const player = {
  ensureAudioGraph: () => ({ audioContext: {}, source: {}, analyser: {} }),
};

beforeEach(async () => {
  __resetPresetCaches();
  loadPreset.mockClear();
  createVisualizer.mockClear();
  window.localStorage.clear();
  // Pin the opening preset. Otherwise it's a random pick out of the pool, and the control strip
  // shows whatever it landed on — which makes getByText("Fractopia") ambiguous roughly half the
  // time. Tests that care about the random path clear this themselves.
  window.localStorage.setItem("music.viz.preset", "geiss-waterfall");

  // happy-dom has no WebGL and no rAF loop worth running. The component's job here is the preset
  // plumbing, so give it a canvas context that exists and a rAF that never fires.
  realGetContext = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = () => ({});
  realRaf = window.requestAnimationFrame;
  window.requestAnimationFrame = () => 1;

  vi.stubGlobal("fetch", vi.fn((url) => {
    const body = String(url).endsWith("/index.json") ? { presets: INDEX } : PRESET;
    return Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(JSON.stringify(body)) });
  }));

  ({ default: MusicVisualizer } = await import("./MusicVisualizer"));
});

afterEach(() => {
  HTMLCanvasElement.prototype.getContext = realGetContext;
  window.requestAnimationFrame = realRaf;
  vi.unstubAllGlobals();
});

async function openBrowser(user) {
  render(<MusicVisualizer player={player} />);
  // "ready" only after the engine import AND the catalogue fetch resolve.
  await waitFor(() => expect(screen.getByTestId("music-visualizer-browse")).toBeTruthy());
  await user.click(screen.getByTestId("music-visualizer-browse"));
  return screen.getByTestId("music-visualizer-browser");
}

describe("MusicVisualizer preset browser", () => {
  it("loads a first preset from the fetched catalogue, not from a bundled pack", async () => {
    window.localStorage.removeItem("music.viz.preset"); // no memory yet — this is the random path
    render(<MusicVisualizer player={player} />);
    await waitFor(() => expect(loadPreset).toHaveBeenCalled());
    expect(loadPreset.mock.calls[0][0]).toEqual(PRESET);
    // Blend 0 on the very first preset — anything else fades in from a blank frame.
    expect(loadPreset.mock.calls[0][1]).toBe(0.0);
    expect(fetch.mock.calls.some(([u]) => String(u) === "/butterchurn/index.json")).toBe(true);
  });

  it("lists the presets in the default pool", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    const list = screen.getByTestId("music-visualizer-preset-list");
    // Default pool is "classic" = tiers 0 and 1, so the tier-2 preset is not offered here.
    expect(within(list).getByText("Waterfall")).toBeTruthy();
    expect(within(list).getByText("Fractopia")).toBeTruthy();
    expect(within(list).queryByText("dark matter")).toBeNull();
  });

  it("switching to Everything reveals the archive tier", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    await user.click(screen.getByRole("button", { name: /Everything/ }));
    const list = screen.getByTestId("music-visualizer-preset-list");
    expect(within(list).getByText("dark matter")).toBeTruthy();
  });

  it("searches across author and title, in any term order", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    await user.type(screen.getByLabelText("Search presets"), "waterfall geiss");
    const list = screen.getByTestId("music-visualizer-preset-list");
    expect(within(list).getByText("Waterfall")).toBeTruthy();
    expect(within(list).queryByText("Fractopia")).toBeNull();
  });

  it("picking a preset fetches it by slug and hands it to butterchurn", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    const list = screen.getByTestId("music-visualizer-preset-list");
    loadPreset.mockClear();
    await user.click(within(list).getByText("Fractopia"));
    await waitFor(() => expect(loadPreset).toHaveBeenCalled());
    expect(fetch.mock.calls.some(([u]) => String(u) === "/butterchurn/presets/rovastar-fractopia.json")).toBe(true);
    // A manual pick blends rather than cutting.
    expect(loadPreset.mock.calls[0][1]).toBeGreaterThan(0);
  });

  it("favoriting a preset makes the Favorites pool find it, whatever its tier", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    await user.click(screen.getByRole("button", { name: /Everything/ }));
    const list = screen.getByTestId("music-visualizer-preset-list");
    const row = within(list).getByText("dark matter").closest(".music-viz-row");
    await user.click(within(row).getByLabelText("Add to favorites"));

    await user.click(screen.getByRole("button", { name: /Favorites/ }));
    const favList = screen.getByTestId("music-visualizer-preset-list");
    expect(within(favList).getByText("dark matter")).toBeTruthy();
    expect(within(favList).queryByText("Waterfall")).toBeNull();
    // Persisted, so the next session still has it.
    expect(JSON.parse(window.localStorage.getItem("music.viz.favorites"))).toContain("flexi-dark-matter");
  });

  it("shows an empty state instead of a blank panel when there are no favorites", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    await user.click(screen.getByRole("button", { name: /Favorites/ }));
    expect(screen.getByText(/No favorites yet/)).toBeTruthy();
  });

  it("a failed preset fetch leaves the previous visuals running instead of breaking the visualizer", async () => {
    const user = userEvent.setup();
    await openBrowser(user);
    const list = screen.getByTestId("music-visualizer-preset-list");
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    loadPreset.mockClear();
    // The auto-cycle prefetches its next pick, so this preset is already cached and a click would
    // never reach the network. Drop the cache first, or this test proves nothing.
    __resetPresetCaches();
    fetch.mockImplementation(() =>
      Promise.resolve({ ok: false, status: 404, text: () => Promise.resolve("") })
    );
    await user.click(within(list).getByText("Fractopia"));
    await waitFor(() => expect(warn).toHaveBeenCalled());
    expect(loadPreset).not.toHaveBeenCalled();
    // Still ready, still showing its controls — not the "failed to load" message.
    expect(screen.getByTestId("music-visualizer-browse")).toBeTruthy();
    warn.mockRestore();
  });

  it("reports a missing preset store rather than pretending to be ready", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    __resetPresetCaches();
    // The SPA fallback answers an unpublished path with 200 + index.html.
    fetch.mockImplementation(() =>
      Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve("<!doctype html>") })
    );
    render(<MusicVisualizer player={player} />);
    await waitFor(() => expect(screen.getByText(/failed to load/i)).toBeTruthy());
    // Nothing was handed to butterchurn, and the panel can't be opened onto an empty catalogue.
    expect(loadPreset).not.toHaveBeenCalled();
    await userEvent.setup().click(screen.getByTestId("music-visualizer-browse"));
    expect(screen.queryByTestId("music-visualizer-browser")).toBeNull();
    error.mockRestore();
  });
});
