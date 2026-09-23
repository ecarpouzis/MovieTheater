import { describe, it, expect, beforeEach } from "vitest";
import {
  AUDIO_OUTPUT_KEY, requestedAudioChannels, readAudioOutput, writeAudioOutput, audioOutputOptions,
} from "./audioOutput";

// The 2026-09-18 tablet: every 5.1 title came out garbled in Chrome and Edge because the server
// floored every client at 6 channels and Chromium/Android handed the six to the vendor HAL. The rule
// pinned here is jellyfin-web's: surround only for a browser that decodes Dolby, stereo otherwise,
// viewer override either way.

describe("requestedAudioChannels", () => {
  it("asks for stereo from a browser that decodes no Dolby, whatever its output probe says", () => {
    expect(requestedAudioChannels({ maxAudioChannels: 6 })).toBe(2);
    expect(requestedAudioChannels({ maxAudioChannels: 2 })).toBe(2);
    expect(requestedAudioChannels({})).toBe(2);
  });

  it("asks for 5.1 from a browser that decodes AC-3 or E-AC-3, even when the OS reports stereo", () => {
    expect(requestedAudioChannels({ supportsAc3: true, maxAudioChannels: 2 })).toBe(6);
    expect(requestedAudioChannels({ supportsEac3: true, maxAudioChannels: 6 })).toBe(6);
  });

  it("keeps 7.1 when the probe reports it and surround is on, and never goes above 8", () => {
    expect(requestedAudioChannels({ supportsAc3: true, maxAudioChannels: 8 })).toBe(8);
    expect(requestedAudioChannels({ supportsAc3: true, maxAudioChannels: 12 })).toBe(8);
  });

  it("honours the viewer's override in both directions", () => {
    expect(requestedAudioChannels({ supportsAc3: true, maxAudioChannels: 6 }, "stereo")).toBe(2);
    expect(requestedAudioChannels({ maxAudioChannels: 2 }, "surround")).toBe(6);
  });
});

describe("the persisted preference", () => {
  beforeEach(() => window.localStorage.clear());

  it("defaults to auto, persists a choice, and treats auto as no entry", () => {
    expect(readAudioOutput()).toBe("auto");
    writeAudioOutput("stereo");
    expect(window.localStorage.getItem(AUDIO_OUTPUT_KEY)).toBe("stereo");
    expect(readAudioOutput()).toBe("stereo");
    writeAudioOutput("auto");
    expect(window.localStorage.getItem(AUDIO_OUTPUT_KEY)).toBeNull();
  });

  it("ignores an unknown stored value", () => {
    window.localStorage.setItem(AUDIO_OUTPUT_KEY, "quadraphonic");
    expect(readAudioOutput()).toBe("auto");
  });

  it("lists the three options with exactly the current one selected", () => {
    const opts = audioOutputOptions("surround");
    expect(opts.map((o) => o.key)).toEqual(["auto", "stereo", "surround"]);
    expect(opts.filter((o) => o.selected).map((o) => o.key)).toEqual(["surround"]);
  });

  it("says what Auto means on THIS browser, so a 5.1-speaker desktop without Dolby knows to pick Surround", () => {
    const noDolby = audioOutputOptions("auto", { maxAudioChannels: 6 }).find((o) => o.key === "auto");
    expect(noDolby.hint).toMatch(/stereo/);
    expect(noDolby.hint).toMatch(/Surround/);
    const dolby = audioOutputOptions("auto", { supportsAc3: true, maxAudioChannels: 2 }).find((o) => o.key === "auto");
    expect(dolby.hint).toMatch(/surround/);
  });
});
