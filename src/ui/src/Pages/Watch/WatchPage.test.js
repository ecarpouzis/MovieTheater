import { render, waitFor } from "@testing-library/react";
import { MemoryRouter, Route } from "react-router-dom";
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// happy-dom has no MediaSource; skip the hls.js path so the source effect doesn't touch it.
vi.mock("hls.js", () => ({ default: { isSupported: () => false } }));

vi.mock("../../MovieAPI", () => ({
  MovieAPI: {
    getTitle: vi.fn(),
    startStream: vi.fn(),
    reportStreamProgress: vi.fn(),
    stopStream: vi.fn(),
    beaconStopStream: vi.fn(),
    getMoviePoster: () => "",
  },
}));

import WatchPage from "./WatchPage";
import { MovieAPI } from "../../MovieAPI";

// Gandhi-shaped data: two playable parts (Primary + Part 2), each with a duration.
const multiPart = {
  data: { id: 691, title: "Gandhi", releaseDate: "1982-01-01", rating: "PG", runtime: "3h 11m" },
  normalized: {
    runtimeMinutes: 191,
    seasons: null,
    files: [
      { mediaFileId: 1, role: "Primary", partNumber: null, isPlayable: true, durationTicks: 50986800000, label: null },
      { mediaFileId: 2, role: "Part", partNumber: 2, isPlayable: true, durationTicks: 59182800000, label: null },
    ],
  },
};
const session = {
  hlsUrl: "blob:mock", isHls: true, isDirectStream: false, videoCodec: "h264",
  durationTicks: 50986800000, playSessionId: "ps1", resumePositionTicks: 0,
  audioTracks: [], subtitleTracks: [], selectedAudioIndex: null, selectedSubtitleIndex: null,
};

function renderWatch() {
  return render(
    <MemoryRouter initialEntries={["/watch/691"]}>
      <Route path="/watch/:movieId">
        <WatchPage userData={{}} />
      </Route>
    </MemoryRouter>
  );
}

describe("WatchPage — multi-part combined timeline", () => {
  beforeEach(() => {
    MovieAPI.getTitle.mockResolvedValue({ json: () => Promise.resolve(multiPart) });
    MovieAPI.startStream.mockResolvedValue({ ok: true, json: () => Promise.resolve(session) });
  });
  afterEach(() => vi.clearAllMocks());

  it("loads a multi-part movie into the player without throwing", async () => {
    const { container } = renderWatch();
    // reaches phase=playing → VideoPlayer mounts its <video>
    await waitFor(() => expect(container.querySelector("video")).toBeTruthy(), { timeout: 4000 });
    // the combined timeline rendered: total time is the SUM of both parts (~3h04m), not just part 1 (~85m)
    expect(container.querySelector(".vp-time-total")?.textContent).toMatch(/3:0\d:/);
  });
});
