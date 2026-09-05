import { act, fireEvent, render, screen } from "@testing-library/react";
import GuidePreview from "./GuidePreview";
import { MovieAPI } from "../../MovieAPI";

vi.mock("hls.js", () => ({ default: { isSupported: () => false, Events: {} } }));
vi.mock("../../streamEngine", () => ({ createHls: vi.fn() }));
vi.mock("../../streamCapabilities", () => ({ detectStreamCapabilities: () => ({ supportsHevc: true, supportsFmp4: true }) }));
vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getChannelNow: vi.fn(),
    startStream: vi.fn(),
    reportStreamProgress: vi.fn(),
    stopStream: vi.fn(),
    beaconStopStream: vi.fn(),
  },
}));

const at = (h, m = 0) => new Date(Date.UTC(2030, 0, 1, h, m)).toISOString();
const NOW = Date.parse(at(20, 20));
const program = { title: "Out of the Past", playableId: 900, startUtc: at(20), endUtc: at(21) };
const nowAnswer = (over = {}) => ({ current: { playableId: 900, offsetSeconds: 1200.7, itemId: 5 }, paused: false, ...over });

function renderPreview(props = {}) {
  const onArm = vi.fn();
  const utils = render(<GuidePreview channelId={7} program={program} live paused={false} armed nowMs={NOW} poster="/p.jpg" onArm={onArm} {...props} />);
  return { ...utils, onArm };
}

beforeEach(() => {
  vi.useFakeTimers();
  MovieAPI.getChannelNow.mockReset();
  MovieAPI.startStream.mockReset();
  MovieAPI.stopStream.mockReset();
  MovieAPI.reportStreamProgress.mockReset();
  // happy-dom's media element has no real pipeline.
  window.HTMLMediaElement.prototype.play = vi.fn(() => Promise.resolve());
  window.HTMLMediaElement.prototype.pause = vi.fn();
  window.HTMLMediaElement.prototype.load = vi.fn();
});
afterEach(() => vi.useRealTimers());

// Drain the peek → start → json promise chain under fake timers (no waitFor: it would spin on the frozen clock).
const flush = async () => { await act(async () => { for (let i = 0; i < 12; i += 1) await Promise.resolve(); }); };

/**
 * Guide v2's live preview: a PEEK (never the room's presence-counting Now), a cheap encode (the ladder's
 * bottom rung, plain SDR caps), passive keep-alive beats, and a stop when it goes away. And the cases
 * where it must NOT stream: unarmed, a frozen channel, a programme that is not on now, a full theater.
 */
describe("Tv/GuidePreview", () => {
  it("peeks (not Now), starts a 1.5 Mbps SDR encode at the live offset, and stops it on unmount", async () => {
    MovieAPI.getChannelNow.mockResolvedValue(nowAnswer());
    MovieAPI.startStream.mockResolvedValue({ ok: true, json: () => Promise.resolve({ playSessionId: "ps1", hlsUrl: "/s/t/x.m3u8", isHls: true }) });
    const { unmount } = renderPreview();
    expect(screen.getByText("Tuning preview…")).toBeTruthy();
    expect(MovieAPI.getChannelNow).not.toHaveBeenCalled(); // debounced
    await act(async () => { vi.advanceTimersByTime(600); });
    await flush();
    expect(MovieAPI.getChannelNow).toHaveBeenCalledWith(7, expect.anything(), { peek: true });
    const req = MovieAPI.startStream.mock.calls[0][0];
    expect(req.playableId).toBe(900);
    expect(req.startSeconds).toBe(1200);
    expect(req.maxBitrateBps).toBe(1_500_000);
    expect(req.capabilities.supportsHevc).toBe(false);
    expect(req.capabilities.supportsFmp4).toBe(true);

    // Keep-alive: passive beats, never a resume write.
    await act(async () => { vi.advanceTimersByTime(10_100); });
    expect(MovieAPI.reportStreamProgress).toHaveBeenCalledWith(expect.objectContaining({ playSessionId: "ps1", passive: true }));

    unmount();
    expect(MovieAPI.stopStream).toHaveBeenCalledWith({ playSessionId: "ps1", playableId: 900 });
  });

  it("does not stream until armed — it shows ▶ Preview instead", async () => {
    const { onArm } = renderPreview({ armed: false });
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(MovieAPI.getChannelNow).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "▶ Preview" }));
    expect(onArm).toHaveBeenCalledTimes(1);
  });

  it("does not stream a frozen channel or a programme that is not on now", async () => {
    const { unmount } = renderPreview({ paused: true });
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(MovieAPI.getChannelNow).not.toHaveBeenCalled();
    expect(screen.getByText("Paused")).toBeTruthy();
    unmount();

    renderPreview({ live: false, program: { ...program, startUtc: at(21), endUtc: at(22) } });
    await act(async () => { vi.advanceTimersByTime(1000); });
    expect(MovieAPI.getChannelNow).not.toHaveBeenCalled();
    expect(screen.getByText(/^Starts /)).toBeTruthy();
  });

  it("falls back to the poster when the theater is full, without retrying", async () => {
    MovieAPI.getChannelNow.mockResolvedValue(nowAnswer());
    MovieAPI.startStream.mockResolvedValue({ ok: false, status: 503, json: () => Promise.resolve({ message: "full" }) });
    renderPreview();
    await act(async () => { vi.advanceTimersByTime(600); });
    await flush();
    expect(screen.getByText("Preview unavailable")).toBeTruthy();
    await act(async () => { vi.advanceTimersByTime(5000); });
    expect(MovieAPI.startStream).toHaveBeenCalledTimes(1);
    expect(MovieAPI.stopStream).not.toHaveBeenCalled();
  });

  it("shows the slot's progress with the minutes left", () => {
    renderPreview({ armed: false });
    expect(screen.getByText(/40 min left/)).toBeTruthy();
  });
});
