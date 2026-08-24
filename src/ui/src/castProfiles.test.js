import { describe, it, expect } from "vitest";

import {
  CAST_PROFILES, DEFAULT_CAST_PROFILE, castProfileFor, castCapabilities, castCeilingBps,
  castSubtitleTracks, castTrackDescriptors,
} from "./castProfiles";

// What these pin is the set of decisions that turn a working cast into a black screen, and which are
// invisible until someone's TV is the one testing them.

describe("castProfileFor", () => {
  it("falls back to the safe profile when the SDK exposes no model at all", () => {
    // The common case: chrome.cast.Receiver does not document a model name, so this must not be a
    // path that only works when the SDK happens to be generous.
    expect(castProfileFor({}).key).toBe(DEFAULT_CAST_PROFILE);
    expect(castProfileFor({ modelName: null }).key).toBe("baseline");
  });

  it("keeps a plain Chromecast on the baseline", () => {
    expect(castProfileFor({ modelName: "Chromecast" }).key).toBe("baseline");
  });

  it("recognizes the 4K/HEVC hardware", () => {
    for (const model of ["Chromecast Ultra", "Chromecast with Google TV", "SHIELD Android TV", "BRAVIA 4K GB"]) {
      expect(castProfileFor({ modelName: model }).key).toBe("hevc4k");
    }
  });

  it("lets an explicit override win over the model, in both directions", () => {
    // The viewer can see their TV and we can't — an override is never second-guessed.
    expect(castProfileFor({ modelName: "Chromecast Ultra", override: "baseline" }).key).toBe("baseline");
    expect(castProfileFor({ modelName: "Chromecast", override: "hevc4k" }).key).toBe("hevc4k");
  });

  it("ignores an override naming a profile that doesn't exist", () => {
    // Stored per device in localStorage, so it outlives any rename of the profile keys.
    expect(castProfileFor({ modelName: "Chromecast", override: "gone" }).key).toBe("baseline");
  });
});

describe("castCapabilities", () => {
  it("never claims Matroska on any profile", () => {
    // The single most consequential flag: a true here makes the server hand the receiver a raw .mkv
    // direct-play url, and no Cast device on earth can open one.
    for (const profile of Object.values(CAST_PROFILES)) {
      expect(castCapabilities(profile).supportsMkv).toBe(false);
    }
  });

  it("keeps the baseline off every advanced video codec", () => {
    const caps = castCapabilities(CAST_PROFILES.baseline);
    expect(caps.supportsHevc).toBe(false);
    expect(caps.supportsAv1).toBe(false);
    expect(caps.supportsHdr).toBe(false);
    expect(caps.supportsDolbyVision).toBe(false);
    // TS segments: the most broadly supported HLS shape on cast hardware.
    expect(caps.supportsFmp4).toBe(false);
  });

  it("pairs HEVC with fMP4, because the server only offers HEVC on the fMP4 path", () => {
    const caps = castCapabilities(CAST_PROFILES.hevc4k);
    expect(caps.supportsHevc).toBe(true);
    expect(caps.supportsFmp4).toBe(true);
  });

  it("leaves Dolby Vision off even on the 4K profile", () => {
    expect(castCapabilities(CAST_PROFILES.hevc4k).supportsDolbyVision).toBe(false);
  });

  it("only advertises AC-3/E-AC-3 when pass-through is explicitly enabled", () => {
    // Default off: on Cast, Dolby is passed through to an amplifier, not decoded. Without one in the
    // chain, advertising it buys silence.
    const off = castCapabilities(CAST_PROFILES.baseline);
    expect(off.supportsAc3).toBe(false);
    expect(off.supportsEac3).toBe(false);
    const on = castCapabilities(CAST_PROFILES.baseline, { dolbyPassthrough: true });
    expect(on.supportsAc3).toBe(true);
    expect(on.supportsEac3).toBe(true);
  });

  it("does not mutate the shared profile when pass-through is toggled", () => {
    castCapabilities(CAST_PROFILES.baseline, { dolbyPassthrough: true });
    expect(CAST_PROFILES.baseline.caps.supportsAc3).toBe(false);
  });

  it("returns the safe baseline shape for a missing profile", () => {
    expect(castCapabilities(null).supportsMkv).toBe(false);
    expect(castCapabilities(undefined).supportsHevc).toBe(false);
  });
});

describe("castCeilingBps", () => {
  it("pins an Auto session to the profile ceiling instead of the ladder's position", () => {
    // Auto's ladder value is meaningless here — it was measured by an hls.js instance that no longer
    // exists, over a link that is not the one the receiver is using.
    expect(castCeilingBps(CAST_PROFILES.baseline, Infinity, true)).toBe(8_000_000);
    expect(castCeilingBps(CAST_PROFILES.hevc4k, null, true)).toBe(20_000_000);
  });

  it("caps 'Original' rather than letting an uncapped remux at a Chromecast", () => {
    expect(castCeilingBps(CAST_PROFILES.baseline, null, false)).toBe(8_000_000);
    expect(castCeilingBps(CAST_PROFILES.baseline, Infinity, false)).toBe(8_000_000);
  });

  it("honors a pinned rung below the ceiling", () => {
    // Someone choosing 1.5 Mbps knows something about their wifi that this code doesn't.
    expect(castCeilingBps(CAST_PROFILES.baseline, 1_500_000, false)).toBe(1_500_000);
    expect(castCeilingBps(CAST_PROFILES.hevc4k, 4_000_000, false)).toBe(4_000_000);
  });

  it("clamps a pinned rung above the ceiling", () => {
    expect(castCeilingBps(CAST_PROFILES.baseline, 12_000_000, false)).toBe(8_000_000);
  });

  it("falls back to the baseline ceiling with no profile", () => {
    expect(castCeilingBps(null, null, true)).toBe(CAST_PROFILES.baseline.ceilingBps);
  });
});

describe("castSubtitleTracks", () => {
  const text = { index: 1, label: "English", kind: "text", deliveryUrl: "/s/tok/Videos/1/Stream.vtt" };
  const pgs = { index: 2, label: "English (PGS)", kind: "image-pgs", deliveryUrl: "/s/tok/Videos/1/2.sup" };
  const ass = { index: 3, label: "Signs", kind: "ass", deliveryUrl: "/s/tok/Videos/1/3.ass" };
  const burned = { index: 4, label: "Français", kind: "image", deliveryUrl: null };

  it("keeps sidecar WebVTT — the only thing the Default Media Receiver renders", () => {
    expect(castSubtitleTracks([text]).tracks).toEqual([text]);
  });

  it("drops the client-rendered kinds, which have no canvas on a television", () => {
    const { tracks, dropped } = castSubtitleTracks([text, pgs, ass]);
    expect(tracks).toEqual([text]);
    expect(dropped).toEqual([pgs, ass]);
  });

  it("keeps burned-in subs, which arrive as pixels like any other part of the picture", () => {
    const { tracks, dropped } = castSubtitleTracks([burned]);
    expect(tracks).toEqual([burned]);
    expect(dropped).toEqual([]);
  });

  it("handles a title with no subtitles at all", () => {
    expect(castSubtitleTracks(null)).toEqual({ tracks: [], dropped: [] });
    expect(castSubtitleTracks([])).toEqual({ tracks: [], dropped: [] });
  });

  it("describes only the tracks the receiver can be handed a url for", () => {
    // A burned-in sub survives the menu but is not a TRACK — there is no file to give the receiver.
    const descriptors = castTrackDescriptors([text, pgs, ass, burned]);
    expect(descriptors).toEqual([
      { trackId: 1, url: text.deliveryUrl, name: "English", language: "en" },
    ]);
  });

  it("keys descriptors by the Jellyfin stream index so activeTrackIds needs no translation", () => {
    const spanish = { index: 7, label: "Español", kind: "text", language: "spa", deliveryUrl: "/s/tok/7.vtt" };
    expect(castTrackDescriptors([spanish])[0]).toMatchObject({ trackId: 7, language: "spa" });
  });
});
