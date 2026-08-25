import { describe, it, expect, vi } from "vitest";

// The option model both player menus render from. What these pin is the logic that used to drift
// between the two inline copies: the Off entry's null index, burned-in detection, the selected
// flag, and the delivered-audio fallback to the first track.

vi.mock("./streamCapabilities", () => ({
  detectStreamCapabilities: () => ({ maxAudioChannels: 6 }),
}));

import {
  QUALITY_LADDER, qualityOptions, audioOptions, subtitleOptions, deliveredAudio, formatPlaying, tvStatusLine,
} from "./playerMenuModel";

describe("qualityOptions", () => {
  it("returns every rung with exactly the active one selected", () => {
    const opts = qualityOptions("720-4");
    expect(opts).toHaveLength(QUALITY_LADDER.length);
    expect(opts.filter((o) => o.selected).map((o) => o.key)).toEqual(["720-4"]);
  });
});

describe("subtitleOptions", () => {
  const tracks = [
    { index: 2, label: "English", deliveryUrl: "/x.vtt" },
    { index: 5, label: "PGS", deliveryUrl: null },
  ];

  it("leads with Off (index null) and selects it when nothing is picked", () => {
    const opts = subtitleOptions(tracks, null);
    expect(opts[0]).toMatchObject({ index: null, label: "Off", selected: true });
    expect(opts.slice(1).some((o) => o.selected)).toBe(false);
  });

  it("hints 'burned in' exactly for tracks with no side-delivery URL", () => {
    const opts = subtitleOptions(tracks, 5);
    expect(opts.find((o) => o.index === 2).hint).toBeNull();
    expect(opts.find((o) => o.index === 5)).toMatchObject({ hint: "burned in", selected: true });
  });
});

describe("audioOptions", () => {
  it("marks the selected track and carries the raw track through for the select handler", () => {
    const tracks = [{ index: 1, label: "Stereo" }, { index: 3, label: "Surround" }];
    const opts = audioOptions(tracks, 3);
    expect(opts.find((o) => o.selected).track).toBe(tracks[1]);
  });
});

describe("deliveredAudio", () => {
  const tracks = [
    { index: 1, label: "Stereo", channels: 2 },
    { index: 3, label: "Surround", channels: 6 },
  ];

  it("reads the selected track's channels", () => {
    expect(deliveredAudio(tracks, 3)).toBe("5.1");
  });

  it("falls back to the FIRST track when nothing is selected — both players relied on this", () => {
    expect(deliveredAudio(tracks, null)).toBe("2.0");
  });

  it("is null with no tracks at all", () => {
    expect(deliveredAudio([], null)).toBeNull();
  });
});

describe("formatPlaying", () => {
  it("only a non-HLS direct stream may claim Original", () => {
    expect(formatPlaying({ qualityKey: "original", isDirectStream: true, isHls: false })).toContain("Original · no re-encode");
    expect(formatPlaying({ qualityKey: "original", isDirectStream: true, isHls: true })).toContain("Video copied · HLS transcode");
    expect(formatPlaying({ qualityKey: "1080-8", isDirectStream: false, isHls: true })).toContain("Transcoded");
  });
});

describe("tvStatusLine", () => {
  // The "TV" readout exists so a viewer with no cast button can tell WHICH nothing they are looking
  // at. Each case here is one of the real support conversations it replaces.
  const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15";
  const noAirPlay = { hasApi: false, supported: false, available: false, active: false };

  it("points an iPhone at AirPlay instead of at Chrome", () => {
    const line = tvStatusLine({ cast: { reason: "unsupported-browser" }, airplay: noAirPlay, userAgent: IPHONE });
    expect(line).toMatch(/iPhone or iPad/);
    expect(line).toMatch(/AirPlay/);
  });

  it("names the browser when a desktop browser has no Cast", () => {
    expect(tvStatusLine({ cast: { reason: "unsupported-browser" }, airplay: noAirPlay, userAgent: "Firefox" }))
      .toMatch(/Chrome or Edge/);
  });

  it("distinguishes a blocked SDK, a slow SDK and a plain-http visit", () => {
    expect(tvStatusLine({ cast: { reason: "sdk-blocked" }, airplay: noAirPlay })).toMatch(/blocked/);
    expect(tvStatusLine({ cast: { reason: "sdk-timeout" }, airplay: noAirPlay })).toMatch(/still loading/);
    expect(tvStatusLine({ cast: { reason: "insecure-context" }, airplay: noAirPlay })).toMatch(/https/);
  });

  it("says the SDK is fine and the network is empty when no receiver was found", () => {
    expect(tvStatusLine({ cast: { sdkReady: true, state: "no-devices" }, airplay: noAirPlay }))
      .toMatch(/No Chromecast found/);
  });

  it("tells the viewer the button is there once a receiver is found", () => {
    expect(tvStatusLine({ cast: { sdkReady: true, state: "idle" }, airplay: noAirPlay })).toMatch(/use the cast button/);
    expect(tvStatusLine({ cast: { connected: true, deviceName: "Den TV" }, airplay: noAirPlay })).toBe("Casting to Den TV.");
  });

  it("covers Safari: MSE that cannot travel, and an empty AirPlay network", () => {
    const desktopSafari = { hasApi: true, supported: false, available: true, active: false };
    expect(tvStatusLine({ cast: { reason: "unsupported-browser" }, airplay: desktopSafari })).toMatch(/can't carry/);
    const iphoneSafari = { hasApi: true, supported: true, available: false, active: false };
    expect(tvStatusLine({ cast: { reason: "unsupported-browser" }, airplay: iphoneSafari, userAgent: IPHONE }))
      .toMatch(/No AirPlay receiver/);
  });

  it("is still a sentence while probing", () => {
    expect(tvStatusLine({ cast: { reason: null }, airplay: noAirPlay })).toMatch(/Looking/);
  });
});
