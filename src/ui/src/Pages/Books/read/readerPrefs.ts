/**
 * The canvas reader's preferences and per-book memories — the standalone's `readerPrefs.ts` verbatim
 * in behaviour, including its storage KEYS (`mybooksReaderPrefs` v2, `mybooksPageOffsets`,
 * `mybooksWebtoonModes`), so a reader who used the standalone on this device keeps every setting.
 * Storage goes through the site's `readStored`/`writeStored` (never throws).
 */
import { readStored, writeStored } from "../../../utils/storage";

export type FitMode = "auto" | "width" | "height" | "original";
export type SplitMode = "none" | "l2r" | "r2l";
/** Webtoon (vertical-scroll) reading column width; the cap never binds on a phone. */
export type WebtoonWidth = "narrow" | "normal" | "wide" | "full";

export interface ReaderPreferences {
  fitMode: FitMode;
  splitMode: SplitMode;
  coverAsPage: boolean;
  textZoom: boolean;
  webtoonWidth: WebtoonWidth;
  webtoonGap: boolean;
}

const PREFS_KEY = "mybooksReaderPrefs";
/** v2: 'auto' (whole-page contain) became the default fit; pre-v2 prefs are migrated to it. */
const PREFS_VERSION = 2;

const DEFAULT_PREFS: ReaderPreferences = {
  fitMode: "auto",
  splitMode: "none",
  coverAsPage: false,
  textZoom: true,
  webtoonWidth: "normal",
  webtoonGap: false,
};

/** Max reading-column width (CSS px) per webtoon width preset; null = full viewport. */
export const WEBTOON_WIDTH_PX: Record<WebtoonWidth, number | null> = {
  narrow: 480,
  normal: 720,
  wide: 980,
  full: null,
};

function parseJson(raw: string | null): Record<string, unknown> | null {
  if (!raw) return null;
  try {
    const v = JSON.parse(raw);
    return v && typeof v === "object" ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

export function loadReaderPrefs(): ReaderPreferences {
  const parsed = parseJson(readStored(PREFS_KEY, null) as string | null);
  if (!parsed) return { ...DEFAULT_PREFS };
  const prefs: ReaderPreferences = { ...DEFAULT_PREFS };
  const migrated = parsed.v !== PREFS_VERSION;
  if (!migrated && (parsed.fitMode === "auto" || parsed.fitMode === "width" || parsed.fitMode === "height" || parsed.fitMode === "original")) {
    prefs.fitMode = parsed.fitMode;
  }
  if (parsed.splitMode === "none" || parsed.splitMode === "l2r" || parsed.splitMode === "r2l") prefs.splitMode = parsed.splitMode;
  if (typeof parsed.coverAsPage === "boolean") prefs.coverAsPage = parsed.coverAsPage;
  if (typeof parsed.textZoom === "boolean") prefs.textZoom = parsed.textZoom;
  if (parsed.webtoonWidth === "narrow" || parsed.webtoonWidth === "normal" || parsed.webtoonWidth === "wide" || parsed.webtoonWidth === "full") {
    prefs.webtoonWidth = parsed.webtoonWidth;
  }
  if (typeof parsed.webtoonGap === "boolean") prefs.webtoonGap = parsed.webtoonGap;
  return prefs;
}

export function saveReaderPrefs(prefs: ReaderPreferences): void {
  writeStored(PREFS_KEY, JSON.stringify({ ...prefs, v: PREFS_VERSION }));
}

/** Per-book printed-page offset: shifts the page READOUT only. A 0 is removed from the map. */
const PAGE_OFFSETS_KEY = "mybooksPageOffsets";

export function loadPageOffset(itemId: number): number {
  const map = parseJson(readStored(PAGE_OFFSETS_KEY, null) as string | null) ?? {};
  const v = map[String(itemId)];
  return Number.isInteger(v) ? (v as number) : 0;
}

export function savePageOffset(itemId: number, offset: number): void {
  const map = parseJson(readStored(PAGE_OFFSETS_KEY, null) as string | null) ?? {};
  if (offset === 0) delete map[String(itemId)];
  else map[String(itemId)] = offset;
  writeStored(PAGE_OFFSETS_KEY, JSON.stringify(map));
}

/** Per-book webtoon choice: null = decide automatically; true/false = the reader's override. */
const WEBTOON_MODES_KEY = "mybooksWebtoonModes";

export function loadWebtoonMode(itemId: number): boolean | null {
  const map = parseJson(readStored(WEBTOON_MODES_KEY, null) as string | null) ?? {};
  const v = map[String(itemId)];
  return typeof v === "boolean" ? v : null;
}

export function saveWebtoonMode(itemId: number, on: boolean): void {
  const map = parseJson(readStored(WEBTOON_MODES_KEY, null) as string | null) ?? {};
  map[String(itemId)] = on;
  writeStored(WEBTOON_MODES_KEY, JSON.stringify(map));
}

// ── spread arithmetic ──

export function isSplitModeEnabled(splitMode: SplitMode): boolean {
  return splitMode !== "none";
}

export function isSinglePageSpread(pageIndex: number, splitMode: SplitMode, coverAsPage: boolean): boolean {
  return isSplitModeEnabled(splitMode) && coverAsPage && pageIndex === 0;
}

export function snapToSpreadStart(pageIndex: number, splitMode: SplitMode, coverAsPage: boolean): number {
  if (!isSplitModeEnabled(splitMode)) return pageIndex;
  if (coverAsPage) return pageIndex === 0 ? 0 : pageIndex % 2 === 1 ? pageIndex : Math.max(0, pageIndex - 1);
  return pageIndex % 2 === 0 ? pageIndex : Math.max(0, pageIndex - 1);
}

export function isRtlSplitReading(splitMode: SplitMode): boolean {
  return splitMode === "r2l";
}
