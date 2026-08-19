import { render, cleanup, screen, waitFor } from "@testing-library/react";
import { vi, describe, it, expect, afterEach } from "vitest";

// Phase 5 video playback in /photos (docs/photos-plan.md §2.3). What these pin down is the promise the
// surface makes: that a duration badge says how long a clip is before anything loads, that a video the
// media server has never indexed shows an EXPLAINED state rather than a play button that leads
// nowhere, and that a synced video actually asks the family-gated endpoint to start.

global.IS_REACT_ACT_ENVIRONMENT = true;
global.ResizeObserver = global.ResizeObserver || class { observe() {} unobserve() {} disconnect() {} };

const startCalls = [];
const ok = (body) => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });

vi.mock("../../MovieAPI", () => ({
  deviceToken: () => "test-device",
  MovieAPI: {
    startPhotoVideo: (assetId, options) => {
      startCalls.push({ assetId, options });
      return ok({
        playSessionId: "session-1",
        url: "https://gateway.example/s/tok/Videos/item-1/stream.mp4",
        isHls: false,
        directPlay: true,
        durationTicks: 420000000,
      });
    },
  },
}));

// The site's hls.js construction is never exercised here — a direct-play answer needs no engine, and
// jsdom has no MSE to give one.
vi.mock("../../streamEngine", () => ({
  prefersNativeHls: (video) => !!video.canPlayType?.("application/vnd.apple.mpegurl"),
  attachDirect: (video, url) => {
    video.src = url;
    return () => {
      video.removeAttribute("src");
      video.load();
    };
  }, createHls: () => null }));
vi.mock("../../streamCapabilities", () => ({
  detectStreamCapabilities: () => ({ supportsFmp4: true, maxAudioChannels: 2 }),
}));

import PhotoVideo, { formatDuration } from "./PhotoVideo";
import { videoBadge } from "./PhotoGrid";

afterEach(() => {
  cleanup();
  startCalls.length = 0;
});

describe("duration formatting", () => {
  it("prints minutes and seconds, and hours only when there are any", () => {
    expect(formatDuration(42)).toBe("0:42");
    expect(formatDuration(90)).toBe("1:30");
    expect(formatDuration(3671)).toBe("1:01:11");
  });

  it("has nothing to say about a video whose duration was never probed", () => {
    // Before the video pass runs, DurationSec is null — the tile then shows a plain play mark rather
    // than inventing "0:00", which would read as an empty file.
    expect(formatDuration(null)).toBeNull();
    expect(formatDuration(0)).toBeNull();
    expect(formatDuration(undefined)).toBeNull();
  });
});

describe("the tile badge", () => {
  const tile = (overrides) => ({
    id: 1,
    path: "Vacation/clip.mp4",
    kind: "Video",
    width: 1920,
    height: 1080,
    thumbState: "Ready",
    ...overrides,
  });

  it("shows the length of a synced video", () => {
    expect(videoBadge(tile({ durationSec: 95, videoSynced: true })).text).toBe("▶ 1:35");
  });

  it("marks a video the media server has never indexed", () => {
    // §2.3: the file is safe on disk and everything else about it works. The tile says so instead of
    // offering a length for something that cannot play.
    const badge = videoBadge(tile({ durationSec: 95, videoSynced: false }));
    expect(badge.text).toBe("▶ !");
    expect(badge.title).toContain("Not yet synced");
    expect(badge.className).toContain("photo-tile-badge-unsynced");
  });

  it("falls back to a plain play mark before the video pass has run", () => {
    expect(videoBadge(tile({ durationSec: null, videoSynced: true })).text).toBe("▶");
  });

  it("says nothing at all about a photo", () => {
    expect(videoBadge({ kind: "Photo", durationSec: null })).toBeNull();
  });
});

describe("the player", () => {
  it("explains an unsynced video and never asks the server to start it", async () => {
    render(<PhotoVideo assetId={7} synced={false} playbackConfigured durationSec={95} />);

    expect(screen.getByText(/Not yet synced for playback/)).toBeTruthy();
    expect(screen.getByText(/safe on disk/)).toBeTruthy();
    // The length is still stated: knowing a clip is 1:35 long is useful even while it cannot play.
    expect(screen.getByText(/1:35/)).toBeTruthy();
    expect(startCalls).toHaveLength(0);
  });

  it("says so when the host cannot mint playback at all", async () => {
    render(<PhotoVideo assetId={7} synced playbackConfigured={false} />);
    expect(screen.getByText(/not configured on this server/)).toBeTruthy();
    expect(startCalls).toHaveLength(0);
  });

  it("starts a synced video and reports that the original was handed over untouched", async () => {
    render(<PhotoVideo assetId={7} synced playbackConfigured durationSec={42} />);

    await waitFor(() => expect(startCalls).toHaveLength(1));
    expect(startCalls[0].assetId).toBe(7);
    // The browser names an ASSET, never a Jellyfin item id — the server looks up what may be played.
    expect(startCalls[0].options).not.toHaveProperty("jellyfinItemId");
    expect(startCalls[0].options.deviceToken).toBeTruthy();

    await waitFor(() => expect(screen.getByText(/Original file · no re-encode/)).toBeTruthy());
    expect(screen.getByText(/0:42/)).toBeTruthy();
  });
});
