/**
 * Reflowable-EPUB reading preferences (font size/family, page theme, line spacing, margins,
 * columns) — the standalone's `epubPrefs.ts`, same key (`mybooksEpubPrefs`) so they carry over.
 */
import { readStored, writeStored } from "../../../utils/storage";

export type EpubTheme = "light" | "sepia" | "dark";
export type EpubFont = "original" | "serif" | "sans";
export type EpubMargin = "narrow" | "normal" | "wide";

export interface EpubPrefs {
  fontScale: number;
  fontFamily: EpubFont;
  theme: EpubTheme;
  lineHeight: number;
  margin: EpubMargin;
  columns: 1 | 2;
}

const KEY = "mybooksEpubPrefs";

const DEFAULTS: EpubPrefs = { fontScale: 1, fontFamily: "original", theme: "light", lineHeight: 1.5, margin: "normal", columns: 1 };

export const EPUB_THEMES: Record<EpubTheme, { bg: string; ink: string; link: string }> = {
  light: { bg: "#ffffff", ink: "#1a1a1a", link: "#1a4fa0" },
  sepia: { bg: "#f4ecd8", ink: "#5b4636", link: "#7a4a1e" },
  dark: { bg: "#121212", ink: "#cfcfcf", link: "#7fb0ff" },
};

/** `original` = no override (the book's own fonts); Serif/Sans intentionally win over them. */
export const EPUB_FONTS: Record<EpubFont, string> = {
  original: "",
  serif: 'Georgia, "Iowan Old Style", "Palatino Linotype", "Times New Roman", serif',
  sans: 'system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
};

/** Max line length (px at 100% font) per margin setting — scaled by fontScale at render time. */
export const EPUB_MEASURE: Record<EpubMargin, number> = { narrow: 760, normal: 620, wide: 500 };

export const EPUB_LINE_HEIGHTS: ReadonlyArray<readonly [string, number]> = [["Tight", 1.3], ["Normal", 1.5], ["Loose", 1.8]];

export const FONT_SCALE_MIN = 0.7;
export const FONT_SCALE_MAX = 2.2;
export const FONT_SCALE_STEP = 0.1;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

export function loadEpubPrefs(): EpubPrefs {
  const raw = readStored(KEY, null) as string | null;
  if (!raw) return { ...DEFAULTS };
  try {
    const p = JSON.parse(raw) as Record<string, unknown>;
    return {
      fontScale: clamp(typeof p.fontScale === "number" ? p.fontScale : 1, FONT_SCALE_MIN, FONT_SCALE_MAX),
      fontFamily: p.fontFamily === "serif" || p.fontFamily === "sans" ? p.fontFamily : "original",
      theme: p.theme === "sepia" || p.theme === "dark" ? p.theme : "light",
      lineHeight: typeof p.lineHeight === "number" ? clamp(p.lineHeight, 1.1, 2.2) : 1.5,
      margin: p.margin === "narrow" || p.margin === "wide" ? p.margin : "normal",
      columns: p.columns === 2 ? 2 : 1,
    };
  } catch {
    return { ...DEFAULTS };
  }
}

export function saveEpubPrefs(prefs: EpubPrefs): void {
  writeStored(KEY, JSON.stringify(prefs));
}
