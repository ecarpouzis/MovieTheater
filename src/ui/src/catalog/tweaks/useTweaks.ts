import { useCallback, useEffect, useState } from "react";
import { readStored, writeStored } from "../../utils/storage";
import type { ViewMode } from "../types";

/**
 * The Tweaks panel's state — device-scoped, per section, never per user. A cover size chosen on a
 * phone is a fact about that phone: the standalone site learned to keep a SEPARATE scale map for
 * coarse pointers ("a desktop-chosen 1.0× fills a landscape phone with a single shelf"), and this
 * keeps that split. Stored under `catalog.tweaks.v1:<section>` via storage.js, so a logout — which
 * removes only the username and card style — leaves it alone.
 */
export type HoverEffect = "lift" | "zoom" | "tilt" | "dim" | "none";
export type MetadataMode = "label" | "minimal";

export interface CatalogTweaks {
  /** Cover scale per view, per pointer class. Missing = the class default. */
  scale: Partial<Record<ViewMode, { fine?: number; coarse?: number }>>;
  hover: HoverEffect;
  rounded: boolean;
  metadata: MetadataMode;
  /** Directory: keep nodes with nothing under them (they render as a blank hue tile). */
  showEmptyFolders: boolean;
  /** Section-registered extras (a font theme, a backdrop), by key. */
  extras: Record<string, string>;
}

export const SCALE_MIN = 0.45;
export const SCALE_MAX = 2.5;
export const SCALE_STEP = 0.05;
export const SCALE_DEFAULT = 1;
/**
 * The touch default is not a round "a bit smaller than desktop" number: it is the pre-overhaul phone
 * poster, restored. `CardList.css` sized the narrow-screen (≤640px) poster at 172px tall × 115 wide,
 * and the movie grid's cell is 200 (`MOVIE_GRID_CELL`), so 0.86 × 200 = 172 puts the phone back
 * exactly where it was. 0.8 (= 160) had been a guess carried over from an earlier slice.
 *
 * It costs no columns: `.bx-grid .bx-card--pkg .bx-meta` has a 120px floor, and both 160 (poster
 * 106 wide) and 172 (poster 114) sit under it — the card box is 120 either way, so this only fills
 * more of the box the phone was already reserving.
 */
export const SCALE_TOUCH_DEFAULT = 0.86;

export const HOVER_EFFECTS: { value: HoverEffect; label: string }[] = [
  { value: "lift", label: "Lift" },
  { value: "zoom", label: "Zoom" },
  { value: "tilt", label: "Tilt" },
  { value: "dim", label: "Dim" },
  { value: "none", label: "None" },
];

export const DEFAULT_TWEAKS: CatalogTweaks = { scale: {}, hover: "lift", rounded: true, metadata: "label", showEmptyFolders: false, extras: {} };

export function storageKeyFor(section: string): string {
  return `catalog.tweaks.v1:${section}`;
}

/** Touch devices have no hover and less room — their own scale map, and a smaller default. */
export function isCoarsePointer(): boolean {
  try {
    return typeof window !== "undefined" && window.matchMedia("(pointer: coarse)").matches;
  } catch {
    return false;
  }
}

const clampScale = (v: number) => Math.min(SCALE_MAX, Math.max(SCALE_MIN, Math.round(v / SCALE_STEP) * SCALE_STEP));

export function loadTweaks(section: string): CatalogTweaks {
  const out: CatalogTweaks = { ...DEFAULT_TWEAKS, scale: {}, extras: {} };
  const raw = readStored(storageKeyFor(section), null) as string | null;
  if (!raw) return out;
  try {
    const p = JSON.parse(raw) as Partial<CatalogTweaks>;
    if (p.scale && typeof p.scale === "object") {
      for (const [view, v] of Object.entries(p.scale)) {
        if (!v || typeof v !== "object") continue;
        const entry: { fine?: number; coarse?: number } = {};
        if (typeof v.fine === "number" && Number.isFinite(v.fine)) entry.fine = clampScale(v.fine);
        if (typeof v.coarse === "number" && Number.isFinite(v.coarse)) entry.coarse = clampScale(v.coarse);
        out.scale[view as ViewMode] = entry;
      }
    }
    if (HOVER_EFFECTS.some((h) => h.value === p.hover)) out.hover = p.hover as HoverEffect;
    if (typeof p.rounded === "boolean") out.rounded = p.rounded;
    if (p.metadata === "label" || p.metadata === "minimal") out.metadata = p.metadata;
    if (typeof p.showEmptyFolders === "boolean") out.showEmptyFolders = p.showEmptyFolders;
    if (p.extras && typeof p.extras === "object") {
      for (const [k, v] of Object.entries(p.extras)) if (typeof v === "string") out.extras[k] = v;
    }
  } catch {
    /* a corrupt entry is just the defaults */
  }
  return out;
}

/** The scale a view renders at on this device. */
export function scaleFor(tweaks: CatalogTweaks, view: ViewMode, coarse = isCoarsePointer()): number {
  const entry = tweaks.scale[view];
  const v = coarse ? entry?.coarse : entry?.fine;
  return v ?? (coarse ? SCALE_TOUCH_DEFAULT : SCALE_DEFAULT);
}

/**
 * The per-card class for a hover effect, or "" when there is none. ONE function, used by every
 * card everywhere — the standalone's drill view once hard-coded its own lift class and silently
 * ignored the setting, so Zoom/Tilt/Dim did nothing after a drill (the "view drift" lesson).
 * "dim" restyles the OTHER cards, so it is driven by `data-hover` on the results root, not here.
 */
export function hoverClass(effect: HoverEffect): string {
  if (effect === "none" || effect === "dim") return "";
  return `bx-hover-${effect}`;
}

/**
 * A section can read its tweaks OUTSIDE the host (Books applies its backdrop + type theme to the
 * section root) — `readTweaks` for the value, `subscribeTweaks` to hear every write the host makes.
 */
const listeners = new Map<string, Set<() => void>>();

export const readTweaks = loadTweaks;

export function subscribeTweaks(section: string, listener: () => void): () => void {
  let set = listeners.get(section);
  if (!set) listeners.set(section, (set = new Set()));
  set.add(listener);
  return () => { set!.delete(listener); };
}

function persist(section: string, next: CatalogTweaks) {
  writeStored(storageKeyFor(section), JSON.stringify(next));
  // After the write, never inside the setState updater: React may call an updater twice.
  queueMicrotask(() => { for (const l of listeners.get(section) ?? []) l(); });
}

export default function useTweaks(section: string) {
  const [tweaks, setTweaks] = useState<CatalogTweaks>(() => loadTweaks(section));
  useEffect(() => { setTweaks(loadTweaks(section)); }, [section]);

  const update = useCallback((patch: Partial<CatalogTweaks>) => {
    setTweaks((prev) => {
      const next = { ...prev, ...patch };
      persist(section, next);
      return next;
    });
  }, [section]);

  const setCoverScale = useCallback((view: ViewMode, value: number) => {
    const coarse = isCoarsePointer();
    setTweaks((prev) => {
      const entry = { ...(prev.scale[view] ?? {}) };
      if (coarse) entry.coarse = clampScale(value); else entry.fine = clampScale(value);
      const next = { ...prev, scale: { ...prev.scale, [view]: entry } };
      persist(section, next);
      return next;
    });
  }, [section]);

  const setExtra = useCallback((key: string, value: string) => {
    setTweaks((prev) => {
      const next = { ...prev, extras: { ...prev.extras, [key]: value } };
      persist(section, next);
      return next;
    });
  }, [section]);

  const coverScale = useCallback((view: ViewMode) => scaleFor(tweaks, view), [tweaks]);

  return { tweaks, update, setCoverScale, setExtra, coverScale };
}
