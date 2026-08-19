import { describe, it, expect, vi } from "vitest";

// The option model both player menus render from. What these pin is the logic that used to drift
// between the two inline copies: the Off entry's null index, burned-in detection, the selected
// flag, and the delivered-audio fallback to the first track.

vi.mock("./streamCapabilities", () => ({
  detectStreamCapabilities: () => ({ maxAudioChannels: 6 }),
}));

import {
  QUALITY_LADDER, qualityOptions, audioOptions, subtitleOptions, deliveredAudio, formatPlaying,
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
