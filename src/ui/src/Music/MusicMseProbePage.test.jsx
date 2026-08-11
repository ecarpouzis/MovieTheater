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

/**
 * A MediaSource that accepts everything and grows its buffered range — enough for the loop to run,
 * and no more. It measures nothing about playback; the phone does that.
 */
function installFakeMediaSource() {
  class FakeSourceBuffer extends EventTarget {
    constructor(mime) {
      super();
      this.mime = mime;
      this.mode = "";
      this.updating = false;
      this.endSec = 0;
      this.buffered = { length: 1, start: () => 0, end: () => this.endSec };
    }
    appendBuffer() {
      this.endSec += 30;
      setTimeout(() => this.dispatchEvent(new Event("updateend")), 0);
    }
    remove() { setTimeout(() => this.dispatchEvent(new Event("updateend")), 0); }
    changeType(mime) { this.mime = mime; }
  }
  class FakeMediaSource extends EventTarget {
    constructor() {
      super();
      this.readyState = "open";
      this.sourceBuffers = [];
      setTimeout(() => this.dispatchEvent(new Event("sourceopen")), 0);
    }
    static isTypeSupported() { return true; }
    addSourceBuffer(mime) {
      const sb = new FakeSourceBuffer(mime);
      this.sourceBuffers.push(sb);
      return sb;
    }
    endOfStream() { this.readyState = "ended"; }
  }
  vi.stubGlobal("MediaSource", FakeMediaSource);
  URL.createObjectURL = () => "blob:probe-test";
  URL.revokeObjectURL = () => {};
  // `paused` is getter-only here, so the fake only has to settle the promise the page awaits.
  window.HTMLMediaElement.prototype.play = () => Promise.resolve();
  window.HTMLMediaElement.prototype.pause = () => {};
}

describe("MusicMseProbe page", () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
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

  // ── The sleep-viability rule, in the live loop ─────────────────────────────────────────────────
  // The pure rule is tested in MusicMseProbe.test.js; this asserts the SLEEP PHASE actually applies
  // it — that the bytes it asks the network for are the universal lane's, not the bit-perfect lane's.
  // That wiring is what failed on the phone: the rule did not exist, a 96 kHz FLAC was appended
  // bit-perfect, its 40 s of runway lost to an 84 s execution gap, and the audio died at T+4min.
  //
  // MediaSource is faked because this environment has none. The fake is deliberately dumb: it
  // accepts appends and grows its buffered range. Nothing here claims to measure playback — only
  // which URL the loop chose.
  it("routes a high-bitrate candidate through the universal lane", async () => {
    // Only the hi-res slot is filled, so every join and the quota probe skip themselves for want of
    // a partner and the run goes straight to the sleep phase — with no measured quota, which is the
    // conservative path (assumed 12 MB, 90 s gap floor).
    mockCandidates({
      mp3: null,
      flac: null,
      hires: { id: 9, title: "HiRes", extension: ".flac", sampleRateHz: 96000, channels: 2, sizeBytes: 38418328, durationSec: 120 },
      mono: null,
    });
    vi.spyOn(MovieAPI, "startMusicTracks").mockResolvedValue({
      ok: true,
      json: async () => ({
        tracks: [{
          trackId: 9, title: "HiRes", mimeType: "audio/flac",
          url: "https://gw.example/s/t/MusicFile",
          fmp4Url: "https://gw.example/s/t/MusicFmp4",
          universalUrl: "https://gw.example/s/t/MusicUniversal",
          sizeBytes: 38418328, durationSec: 120, sampleRateHz: 96000, channels: 2,
        }],
        skipped: [],
      }),
    });

    const fetched = [];
    vi.stubGlobal("fetch", vi.fn(async (url) => {
      fetched.push(String(url));
      return { ok: true, body: null, arrayBuffer: async () => new ArrayBuffer(4096) };
    }));
    installFakeMediaSource();

    const user = userEvent.setup();
    render(<MusicMseProbe />);
    await user.click(await screen.findByRole("button", { name: /run the whole gate/i }));

    await waitFor(() => expect(screen.getByText(/turn the screen off/i)).toBeInTheDocument(), { timeout: 8000 });

    const laneUrls = fetched.filter((u) => u.includes("/s/t/"));
    expect(laneUrls.length).toBeGreaterThan(0);
    expect(laneUrls.every((u) => u.endsWith("MusicUniversal"))).toBe(true);
    expect(laneUrls.some((u) => u.endsWith("MusicFmp4"))).toBe(false);

    // …and the panel says WHY, so the rule can be seen working rather than taken on trust.
    expect(screen.getByText(/> ceiling .* → universal/)).toBeInTheDocument();
  }, 15000);

  // ── Death, recorded as an outcome ──────────────────────────────────────────────────────────────
  // The phone run died at T+4min and the panel still read RUNNING when it was picked up, because the
  // failure was never written down. A `waiting` on a hidden page is very likely the LAST code that
  // runs — silent audio costs the page its exemption — so the verdict has to be in localStorage
  // before the handler returns, not queued behind a React update.
  it("writes a FAIL to storage the instant the buffer runs dry while hidden", async () => {
    mockCandidates({
      mp3: { id: 1, title: "Cheap", extension: ".mp3", sampleRateHz: 44100, channels: 2, sizeBytes: 1000000, durationSec: 120 },
      flac: null, hires: null, mono: null,
    });
    vi.spyOn(MovieAPI, "startMusicTracks").mockResolvedValue({
      ok: true,
      json: async () => ({
        tracks: [{
          trackId: 1, title: "Cheap", mimeType: "audio/mpeg",
          url: "https://gw.example/s/t/MusicFile",
          universalUrl: "https://gw.example/s/t/MusicUniversal",
          sizeBytes: 1000000, durationSec: 120, sampleRateHz: 44100, channels: 2,
        }],
        skipped: [],
      }),
    });
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, body: null, arrayBuffer: async () => new ArrayBuffer(4096) })));
    installFakeMediaSource();

    const user = userEvent.setup();
    render(<MusicMseProbe />);
    await user.click(await screen.findByRole("button", { name: /run the whole gate/i }));
    await waitFor(() => expect(screen.getByText(/turn the screen off/i)).toBeInTheDocument(), { timeout: 8000 });

    Object.defineProperty(document, "visibilityState", { get: () => "hidden", configurable: true });
    document.querySelector(".mse-probe audio").dispatchEvent(new Event("waiting"));

    // Read storage DIRECTLY and without awaiting anything: this is the assertion that the write is
    // synchronous. A render-time check would pass even if the write had been queued.
    const stored = JSON.parse(window.localStorage.getItem("music.mse.results")).sleep;
    expect(stored.status).toBe("fail");
    expect(stored.verdict).toMatch(/buffer ran dry at T\+\d+s while hidden/);
    expect(stored).toHaveProperty("bufferedAheadAtDeathSec");
    expect(stored).toHaveProperty("sinceLastAppendSec");

    Object.defineProperty(document, "visibilityState", { get: () => "visible", configurable: true });
    await waitFor(() => expect(screen.getByText(/buffer ran dry/i)).toBeInTheDocument());
  }, 15000);

  it("survives a candidates endpoint that is unavailable", async () => {
    vi.spyOn(MovieAPI, "getMusicProbeCandidates").mockResolvedValue({ ok: false, status: 503, json: async () => ({}) });
    render(<MusicMseProbe />);
    expect(await screen.findByText(/could not fetch candidates/i)).toBeInTheDocument();
    // Still usable: a run without candidates skips the sub-probes rather than blocking the page.
    expect(screen.getByRole("button", { name: /run the whole gate/i })).toBeEnabled();
  });
});
