import { describe, expect, it } from "vitest";

import {
  MIME_AAC_FMP4, MIME_FLAC_FMP4, MIME_MPEG,
  appendTreatmentFor, bufferCeilingSec, chooseEngineMode,
  keepBehindSec, KEEP_BEHIND_MIN_SEC, KEEP_BEHIND_MAX_SEC,
} from "./musicTreatments";

// The routing rules the Phase 2 engine plays by. The matrix half (treatmentFor, switchReasonFor,
// sleepLaneDecision) is tested where it was measured, in MusicMseProbe.test.js — these are the two
// rules the engine adds on top: the visibility-aware combination of them, and the flag.

const mp3 = {
  mimeType: MIME_MPEG, url: "u/file", universalUrl: "u/universal",
  sizeBytes: 5_000_000, durationSec: 240, sampleRateHz: 44100, channels: 2,
};
const hiResFlac = {
  mimeType: "audio/flac", url: "u/file", fmp4Url: "u/fmp4", universalUrl: "u/universal",
  sizeBytes: 38_000_000, durationSec: 120, sampleRateHz: 96000, channels: 2,
};
const smallFlac = {
  mimeType: "audio/flac", url: "u/file", fmp4Url: "u/fmp4", universalUrl: "u/universal",
  sizeBytes: 20_000_000, durationSec: 600, sampleRateHz: 44100, channels: 2,
};
const all = () => true;

describe("appendTreatmentFor — fidelity while watching, continuity while asleep", () => {
  it("uses the bit-perfect lane while VISIBLE, even for a hi-res FLAC", () => {
    const d = appendTreatmentFor({ payload: hiResFlac, isTypeSupported: all, hidden: false });
    expect(d.treatment.lane).toBe("fmp4");
    expect(d.treatment.mime).toBe(MIME_FLAC_FMP4);
    expect(d.demoted).toBe(false);
  });

  // THE rule the phone paid for: 2.5 Mbps buys ~40 s of runway in an 11.5 MB quota, against an
  // execution gap the design sizes at 90 s. Hidden, that track has to be AAC or the audio dies.
  it("demotes a hi-res FLAC to the universal lane while HIDDEN", () => {
    const d = appendTreatmentFor({ payload: hiResFlac, isTypeSupported: all, hidden: true });
    expect(d.treatment.lane).toBe("universal");
    expect(d.treatment.mime).toBe(MIME_AAC_FMP4);
    expect(d.demoted).toBe(true);
    expect(d.reason).toMatch(/> ceiling/);
  });

  it("keeps a low-bitrate track bit-perfect while hidden — the demotion is per-track, not per-format", () => {
    const d = appendTreatmentFor({ payload: mp3, isTypeSupported: all, hidden: true });
    expect(d.treatment.lane).toBe("file");
    expect(d.demoted).toBe(false);
    const flacTooBig = appendTreatmentFor({ payload: smallFlac, isTypeSupported: all, hidden: true });
    // 20 MB over 600 s is 33 KB/s — under the ceiling, so a FLAC can absolutely stay lossless asleep.
    expect(flacTooBig.treatment.lane).toBe("fmp4");
  });

  // Rung 1: per-FORMAT capability, which is what catches Firefox's MSE having no MP3 decoder.
  it("routes an mp3 to the universal lane on a browser whose MSE won't decode MP3", () => {
    const noMpeg = (mime) => mime !== MIME_MPEG;
    const d = appendTreatmentFor({ payload: mp3, isTypeSupported: noMpeg, hidden: false });
    expect(d.treatment.lane).toBe("universal");
  });

  it("has no treatment at all when the browser refuses every row (rung 7)", () => {
    const d = appendTreatmentFor({ payload: mp3, isTypeSupported: () => false, hidden: false });
    expect(d.treatment).toBe(null);
  });

  // Rung 4: a gateway that is behind the site mints no universal URL. Demoting to a lane that
  // doesn't exist would be worse than keeping the one that does.
  it("keeps the bit-perfect lane when the server offered no universal URL", () => {
    const { universalUrl, ...noUniversal } = hiResFlac;
    expect(universalUrl).toBe("u/universal");
    const d = appendTreatmentFor({ payload: noUniversal, isTypeSupported: all, hidden: true });
    expect(d.treatment.lane).toBe("fmp4");
    expect(d.reason).toMatch(/no universal lane/);
  });
});

describe("bufferCeilingSec", () => {
  // Quota is a BYTE budget; only a bitrate turns it into the seconds an append loop can reason
  // about. Without this the probe's pump issued ~6 fetches a second forever.
  it("asks for the target when the quota can hold it", () => {
    // 32 KB/s (256 kbps AAC) into 11.5 MB ≈ 360 s of runway, so the 180 s target is affordable.
    const sec = bufferCeilingSec({ sizeBytes: 32000 * 300, durationSec: 300, quotaBytes: 11.5 * 1024 * 1024, targetSec: 180 });
    expect(sec).toBe(180);
  });

  // The spin the browser found: a 38 MB hi-res FLAC holds ~36 s in an 11.5 MB quota, and an append
  // loop asked for 180 s of it appends → QuotaExceeded → evicts → retries, forever.
  it("drops BELOW the target for a track the quota cannot hold that much of", () => {
    const sec = bufferCeilingSec({ sizeBytes: 38_000_000, durationSec: 120, quotaBytes: 11.5 * 1024 * 1024, targetSec: 180 });
    expect(sec).toBeLessThan(40);
    expect(sec).toBeGreaterThan(20);
  });

  it("falls back to the target when the bitrate is unknown", () => {
    expect(bufferCeilingSec({ sizeBytes: 0, durationSec: 0, targetSec: 180 })).toBe(180);
  });
});

describe("keepBehindSec", () => {
  // The seek-backwards window. It was a flat 20 s on every lane, which is where "seeking goes back
  // to the start of the song" came from: seekPlan can only honour a target that is still buffered.
  const QUOTA = 11.5 * 1024 * 1024;

  it("spends the quota the ahead window is not using", () => {
    // 16 KB/s (128 kbps): ~735 s of runway, of which the 180 s ahead window uses a quarter. Keeping
    // 20 s behind out of that was throwing away the whole rest of the budget for nothing.
    const sec = keepBehindSec({ sizeBytes: 16000 * 300, durationSec: 300, quotaBytes: QUOTA, aheadSec: 180 });
    expect(sec).toBeGreaterThan(400);
    expect(sec).toBeLessThanOrEqual(KEEP_BEHIND_MAX_SEC);
  });

  it("stops hoarding at the ceiling, however much room there is", () => {
    // A tiny 64 kbps file could keep half an hour behind. Nobody scrubs back half an hour.
    const sec = keepBehindSec({ sizeBytes: 8000 * 600, durationSec: 600, quotaBytes: QUOTA, aheadSec: 180 });
    expect(sec).toBe(KEEP_BEHIND_MAX_SEC);
  });

  it("returns the floor for Winterbreak, because there is genuinely nothing spare", () => {
    // The real track, from the DB: 1568 kbps FLAC, 4:57, 55.6 MB → 191.6 KB/s. 11.5 MB of quota is
    // 61 s of it, and the 49 s ahead window has already spent nearly all of that. No arithmetic
    // here can make a 297 s song seekable — that is what the seek detour exists for.
    const sec = keepBehindSec({ sizeBytes: 55.6 * 1024 * 1024, durationSec: 297, quotaBytes: QUOTA, aheadSec: 49 });
    expect(sec).toBe(KEEP_BEHIND_MIN_SEC);
  });

  it("never returns less than the floor, even when the ahead window has overspent", () => {
    const sec = keepBehindSec({ sizeBytes: 300_000 * 300, durationSec: 300, quotaBytes: QUOTA, aheadSec: 180 });
    expect(sec).toBe(KEEP_BEHIND_MIN_SEC);
  });

  it("keeps the old constant when the bitrate is unknown — a guess must not be optimistic", () => {
    expect(keepBehindSec({ sizeBytes: 0, durationSec: 0, quotaBytes: QUOTA, aheadSec: 180 })).toBe(KEEP_BEHIND_MIN_SEC);
  });
});

describe("chooseEngineMode", () => {
  const storage = () => {
    const map = new Map();
    return {
      getItem: (k) => (map.has(k) ? map.get(k) : null),
      setItem: (k, v) => map.set(k, v),
      removeItem: (k) => map.delete(k),
      map,
    };
  };

  // Phase 5: the phone gate passed, the timeline made it daily-usable, so the default flipped.
  it("is the ENGINE by default where anything is supported", () => {
    expect(chooseEngineMode({ search: "", storage: storage(), supported: true })).toBe("mse");
  });

  it("?mse=0 opts out and is REMEMBERED; ?mse=1 clears the opt-out", () => {
    const s = storage();
    expect(chooseEngineMode({ search: "?mse=0", storage: s, supported: true })).toBe("decks");
    expect(s.getItem("music.engine")).toBe("decks");
    // The escape hatch survives a plain reload — that is what makes it one.
    expect(chooseEngineMode({ search: "", storage: s, supported: true })).toBe("decks");
    expect(chooseEngineMode({ search: "?mse=1", storage: s, supported: true })).toBe("mse");
    expect(chooseEngineMode({ search: "", storage: s, supported: true })).toBe("mse");
  });

  // The flag is a request, not an assertion: a browser that proves no treatment keeps the decks
  // however loudly it is asked otherwise (rung 7).
  it("refuses to turn on where nothing is supported", () => {
    const s = storage();
    expect(chooseEngineMode({ search: "?mse=1", storage: s, supported: false })).toBe("decks");
    expect(chooseEngineMode({ search: "", storage: s, supported: false })).toBe("decks");
  });

  it("survives storage being unavailable (private mode)", () => {
    const broken = {
      getItem: () => { throw new Error("nope"); },
      setItem: () => { throw new Error("nope"); },
      removeItem: () => { throw new Error("nope"); },
    };
    // The default needs no storage, and a query is honoured for the session it was typed in even
    // though it cannot be remembered.
    expect(chooseEngineMode({ search: "", storage: broken, supported: true })).toBe("mse");
    expect(chooseEngineMode({ search: "?mse=0", storage: broken, supported: true })).toBe("decks");
  });
});
