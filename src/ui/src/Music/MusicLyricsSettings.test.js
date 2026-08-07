import { describe, it, expect } from "vitest";
import {
  LYRICS_DEFAULTS,
  LYRICS_FONTS,
  LYRICS_SCALE_MAX,
  LYRICS_SCALE_MIN,
  clampLyricsScale,
  lyricsCreepPxPerSec,
  lyricsFontStack,
  normalizeLyricsSettings,
} from "./MusicLyricsSettings";

// These settings are read straight out of localStorage on boot, which means the input is whatever an
// older build wrote, whatever a hand-edit left, and occasionally junk. Every one of them feeds a CSS
// value or a scroll rate, so a bad field doesn't throw — it silently produces unreadable lyrics or a
// pane that scrolls at NaN px/s. Normalizing has to be total.

describe("clampLyricsScale", () => {
  it("keeps a sane value", () => {
    expect(clampLyricsScale(1.4)).toBe(1.4);
  });

  it("clamps to the legal range rather than trusting the caller", () => {
    expect(clampLyricsScale(99)).toBe(LYRICS_SCALE_MAX);
    expect(clampLyricsScale(0)).toBe(LYRICS_SCALE_MIN);
    expect(clampLyricsScale(-3)).toBe(LYRICS_SCALE_MIN);
  });

  // A slider handing back "1.3" (or storage handing back null) must not become a NaN font-size,
  // which collapses every lyric line to nothing.
  it("falls back to the default for anything that isn't a finite number", () => {
    expect(clampLyricsScale(NaN)).toBe(LYRICS_DEFAULTS.scale);
    expect(clampLyricsScale(undefined)).toBe(LYRICS_DEFAULTS.scale);
    expect(clampLyricsScale("1.3")).toBe(LYRICS_DEFAULTS.scale);
    // Infinity is garbage, not "very large": it goes to the default rather than being clamped to
    // the maximum, so a corrupt value can't leave the lyrics stuck at their biggest size.
    expect(clampLyricsScale(Infinity)).toBe(LYRICS_DEFAULTS.scale);
  });
});

describe("lyricsFontStack / lyricsCreepPxPerSec", () => {
  it("resolves every offered id to a real value", () => {
    for (const f of LYRICS_FONTS) expect(lyricsFontStack(f.id)).toBeTruthy();
  });

  it("falls back rather than returning undefined for an unknown id", () => {
    expect(lyricsFontStack("comic-sans")).toBe(LYRICS_FONTS[0].stack);
    expect(lyricsCreepPxPerSec("ludicrous")).toBeGreaterThan(0);
    expect(lyricsCreepPxPerSec(undefined)).toBeGreaterThan(0);
  });
});

describe("normalizeLyricsSettings", () => {
  it("returns the defaults for nothing at all", () => {
    expect(normalizeLyricsSettings(null)).toEqual(LYRICS_DEFAULTS);
    expect(normalizeLyricsSettings(undefined)).toEqual(LYRICS_DEFAULTS);
    expect(normalizeLyricsSettings("not an object")).toEqual(LYRICS_DEFAULTS);
  });

  it("keeps every valid field", () => {
    const stored = { scale: 1.6, font: "serif", scrim: false, follow: false, creep: "fast" };
    expect(normalizeLyricsSettings(stored)).toEqual(stored);
  });

  // The point of per-field fallbacks: one junk value must not cost the reader the other four.
  it("repairs only the bad fields", () => {
    const out = normalizeLyricsSettings({ scale: "big", font: "serif", scrim: false, follow: "yes", creep: "fast" });
    expect(out.scale).toBe(LYRICS_DEFAULTS.scale);
    expect(out.follow).toBe(LYRICS_DEFAULTS.follow);
    expect(out.font).toBe("serif");
    expect(out.scrim).toBe(false);
    expect(out.creep).toBe("fast");
  });

  // false is a legitimate value for both switches and must survive a truthiness check — the same
  // trap that once ate 693 Butterchurn presets whose shader was an empty string.
  it("preserves a deliberate false", () => {
    const out = normalizeLyricsSettings({ scrim: false, follow: false });
    expect(out.scrim).toBe(false);
    expect(out.follow).toBe(false);
  });

  it("is idempotent", () => {
    const once = normalizeLyricsSettings({ scale: 99, font: "nope", creep: 7 });
    expect(normalizeLyricsSettings(once)).toEqual(once);
  });
});
