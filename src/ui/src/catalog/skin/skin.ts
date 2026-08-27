/**
 * The catalog's SKIN — the backdrop + type layer every section wears, lifted out of
 * `Pages/Books/booksTheme.ts` (R9 S5) so the nine-backdrop tweak is not a Books privilege.
 *
 * A section registers ONE `SectionSkin` (`registerSectionSkin`); the host then offers its rows in
 * the ⚙ panel (a 4-column swatch grid for the backdrops, a Seg for the type) and writes the chosen
 * token set ONCE on the section root — never per card. The same token set is available as a style
 * object (`sectionSkinStyle`) for a portal that renders OUTSIDE that root: an antd modal's wrap.
 *
 * Three rules the Books implementation established and this keeps:
 *
 * 1. **`data-theme` is the authority.** A backdrop belongs to a family (light / dark / any); a
 *    remembered value from the other family falls back to that family's default rather than
 *    painting a dark page inside a light site. The panel still SHOWS all nine — picking one from
 *    the other family asks the site to switch theme (`requestSiteTheme`), so no swatch is inert.
 * 2. **The backdrop is remembered per view** (`perView` → stored `backdrop:<view>`): the Shelves
 *    may be Bookcase while the Grid is Paper. The type theme is section-wide.
 * 3. **The section's own surface is a swatch.** Every section's set opens with a `siteDefault`
 *    swatch that writes NOTHING — the page keeps `--content-bg` / `--card-surface` / `--text-*`
 *    from theme.css, which already follow `data-theme` × `data-feature`. A default install is
 *    therefore byte-identical to what shipped before the skin existed, and "no backdrop" is a real,
 *    selectable choice rather than an absence.
 *
 * Storage is the catalog's own: `catalog.tweaks.v1:<section>` through `tweaks/useTweaks.ts`
 * (`utils/storage.js` under it — never a bare localStorage read).
 */
import { useEffect, useState } from "react";

import type { TweakExtra } from "../types";
import { readCatalogDefaults } from "../state/useCatalogView";
import { readTweaks, subscribeTweaks } from "../tweaks/useTweaks";

export type SkinFamily = "light" | "dark";
/** A swatch that belongs to one theme family, or to both (`any` — the section's own surface). */
export type SwatchFamily = SkinFamily | "any";

export interface SkinTokens {
  bg: string;
  ink: string;
  sub: string;
  line: string;
  chrome: string;
  /** The card surface, when it should differ from the page (default: `bg`). */
  card?: string;
  /** A scene (an image layer) painted behind the content; `bg` when absent. */
  scene?: string;
}

export interface BackdropDef extends Partial<SkinTokens> {
  family: SwatchFamily;
  label: string;
  /** The colour the swatch button paints (defaults to `bg`) — a `var(--…)` reference is fine. */
  color?: string;
  /**
   * The section's own surface: selectable, but writes NO tokens, so theme.css keeps the floor.
   * Exactly one per section, and it is that section's family default.
   */
  siteDefault?: boolean;
}

export interface TypeDef {
  label: string;
  display: string;
  header: string;
  mono: string;
  tracking: string;
  weight: string;
  /** The site's own faces: writes no tokens (see `siteDefault` above). */
  siteDefault?: boolean;
}

export interface SectionSkin {
  backdrops: Record<string, BackdropDef>;
  /** The backdrop each family falls back to. */
  defaults: Record<SkinFamily, string>;
  types?: Record<string, TypeDef>;
  defaultType?: string;
  /** Remember the backdrop per view (default true — the standalone's per-layout memory). */
  perView?: boolean;
  /**
   * Also write `--<prefix>-bg` … aliases beside the canonical `--skin-*` ones. Books keeps its
   * `--books-*` names (five stylesheets and the reader are written against them).
   */
  tokenPrefix?: string;
  /**
   * Let the host PAINT its own box with the backdrop (the generic sections: the results panel is
   * the section's surface). Off for a section that paints a wider root itself — Books paints
   * `.books-section`, which is the tabs and the plates too.
   */
  paintHost?: boolean;
  backdropLabel?: string;
  typeLabel?: string;
}

export const BACKDROP_EXTRA = "backdrop";
/** The type-theme extra keeps the Books key so a reader's stored choice survives the lift. */
export const TYPE_EXTRA = "display";

const REGISTRY = new Map<string, SectionSkin>();

/** Register a section's skin. Idempotent — the last registration for a key wins. */
export function registerSectionSkin(section: string, skin: SectionSkin): void {
  REGISTRY.set(section, skin);
}

export function getSectionSkin(section: string): SectionSkin | undefined {
  return REGISTRY.get(section);
}

export function siteTheme(): SkinFamily {
  if (typeof document === "undefined") return "light";
  return document.documentElement.dataset.theme === "dark" ? "dark" : "light";
}

/** `data-theme`, as state — the backdrop family follows the site's light/dark switch. */
export function useSiteTheme(): SkinFamily {
  const [theme, setTheme] = useState<SkinFamily>(siteTheme);
  useEffect(() => {
    const read = () => setTheme(siteTheme());
    read();
    if (typeof MutationObserver === "undefined") return undefined;
    const mo = new MutationObserver(read);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    return () => mo.disconnect();
  }, []);
  return theme;
}

const fits = (b: BackdropDef | undefined, theme: SkinFamily) => !!b && (b.family === "any" || b.family === theme);

/** The backdrop to paint: the remembered one when its family fits, else the family default. */
export function resolveBackdrop(skin: SectionSkin | undefined, value: string | null | undefined, theme: SkinFamily): string {
  if (!skin) return "";
  const hit = value ? skin.backdrops[value] : undefined;
  return fits(hit, theme) ? value! : skin.defaults[theme];
}

export function resolveType(skin: SectionSkin | undefined, value: string | null | undefined): string {
  if (!skin?.types) return "";
  return value && skin.types[value] ? value : skin.defaultType ?? Object.keys(skin.types)[0] ?? "";
}

/** The backdrop remembered for a view: `backdrop:<view>` first, the section-wide choice second. */
export function backdropExtraFor(extras: Record<string, string> | undefined, view: string | null | undefined): string | undefined {
  if (!extras) return undefined;
  return (view ? extras[`${BACKDROP_EXTRA}:${view}`] : undefined) ?? extras[BACKDROP_EXTRA];
}

/** The family a chosen swatch belongs to, or null when it fits the current theme already. */
export function crossFamilyPick(section: string, key: string, value: string, theme: SkinFamily): SkinFamily | null {
  if (key !== BACKDROP_EXTRA && !key.startsWith(`${BACKDROP_EXTRA}:`)) return null;
  const b = getSectionSkin(section)?.backdrops[value];
  if (!b || b.family === "any" || b.family === theme) return null;
  return b.family;
}

/** The tweak rows a section's skin contributes: the swatch grid, then the type Seg. */
export function skinTweakExtras(section: string, theme: SkinFamily): TweakExtra[] {
  const skin = getSectionSkin(section);
  if (!skin) return [];
  const out: TweakExtra[] = [{
    key: BACKDROP_EXTRA,
    label: skin.backdropLabel ?? "Backdrop",
    perView: skin.perView !== false,
    render: "swatch",
    options: Object.entries(skin.backdrops).map(([value, b]) => ({
      value,
      label: b.label,
      color: b.color ?? b.bg ?? "var(--content-bg)",
      family: b.family,
      inactive: !fits(b, theme),
    })),
  }];
  if (skin.types) {
    out.push({
      key: TYPE_EXTRA,
      label: skin.typeLabel ?? "Type",
      options: Object.entries(skin.types).map(([value, t]) => ({ value, label: t.label })),
    });
  }
  return out;
}

/** The canonical token names, in the order they are written (also the removal list). */
export const SKIN_TOKENS = ["bg", "card", "ink", "sub", "line", "chrome", "scene", "display", "header", "mono", "tracking", "weight"] as const;

/**
 * The token set for a section's current choice. EMPTY when both choices are the section's own
 * (the `siteDefault` swatch + the site faces) — the whole point: no skin means no tokens, and the
 * page keeps theme.css's own values.
 */
export function skinTokens(section: string, extras: Record<string, string> | undefined, theme: SkinFamily, view?: string | null): Record<string, string> {
  const skin = getSectionSkin(section);
  if (!skin) return {};
  const out: Record<string, string> = {};
  const key = resolveBackdrop(skin, backdropExtraFor(extras, view), theme);
  const b = skin.backdrops[key];
  if (b && !b.siteDefault) {
    const bg = b.bg ?? "var(--content-bg)";
    out["--skin-bg"] = bg;
    out["--skin-card"] = b.card ?? bg;
    out["--skin-ink"] = b.ink ?? "var(--text-primary)";
    out["--skin-sub"] = b.sub ?? "var(--text-secondary)";
    out["--skin-line"] = b.line ?? "var(--card-border)";
    out["--skin-chrome"] = b.chrome ?? bg;
    out["--skin-scene"] = b.scene ?? bg;
  }
  const typeKey = resolveType(skin, extras?.[TYPE_EXTRA]);
  const t = typeKey ? skin.types?.[typeKey] : undefined;
  if (t && !t.siteDefault) {
    out["--skin-display"] = t.display;
    out["--skin-header"] = t.header;
    out["--skin-mono"] = t.mono;
    out["--skin-tracking"] = t.tracking;
    out["--skin-weight"] = t.weight;
  }
  if (skin.tokenPrefix) {
    for (const name of SKIN_TOKENS) {
      const v = out[`--skin-${name}`];
      if (v != null) out[`--${skin.tokenPrefix}-${name}`] = v;
    }
  }
  return out;
}

/** Which backdrop key is live right now (the section root's `data-catalog-skin`). */
export function activeBackdrop(section: string, extras: Record<string, string> | undefined, theme: SkinFamily, view?: string | null): string {
  const skin = getSectionSkin(section);
  if (!skin) return "";
  return resolveBackdrop(skin, backdropExtraFor(extras, view), theme);
}

/**
 * Write the skin on a section root. Idempotent, and it REMOVES what it does not set, so switching
 * back to the section's own surface leaves no stale property behind. Called once per view/choice —
 * never per card.
 */
export function applySectionSkin(root: HTMLElement | null, section: string, extras: Record<string, string> | undefined, theme: SkinFamily, view?: string | null): void {
  if (!root) return;
  const skin = getSectionSkin(section);
  if (!skin) {
    delete root.dataset.catalogSkin;
    delete root.dataset.skinPaint;
    return;
  }
  const key = activeBackdrop(section, extras, theme, view);
  const tokens = skinTokens(section, extras, theme, view);
  root.dataset.catalogSkin = key;
  if (skin.paintHost !== false && tokens["--skin-scene"]) root.dataset.skinPaint = "1";
  else delete root.dataset.skinPaint;
  const prefixes = skin.tokenPrefix ? ["skin", skin.tokenPrefix] : ["skin"];
  for (const p of prefixes) {
    for (const name of SKIN_TOKENS) {
      const prop = `--${p}-${name}`;
      const v = tokens[prop];
      if (v != null) root.style.setProperty(prop, v);
      else root.style.removeProperty(prop);
    }
  }
}

/**
 * The same choice as a style object for a portal that renders OUTSIDE the section root — an antd
 * modal's wrap (`styles={{ wrapper }}`; never `wrapProps.style`, which REPLACES the wrap's own
 * inline style and takes its z-index with it). It repoints the SITE surface tokens too, so a sheet
 * dressed in `--card-surface` / `--text-primary` takes the skin with no stylesheet of its own.
 *
 * Which sheets wear it, and why not all of them: a dialog that paints itself FROM the tokens takes
 * the skin as a whole — the movie sheet and the Books sheets (`sheet-modal--themed`), the arcade
 * game sheet (`background: var(--content-bg); color: var(--text-primary)`) and the photo lightbox
 * (`background: var(--card-surface)`) — and, since R9 S6, the BOARDGAME sheet, whose hard-coded
 * light surface and light-surface ink (#fafafa / #222 / #1677ff) were tokenised so it could join
 * them (`--bgm-*` at the top of `BoardGameModal.css`; the category hues survive as mixes into the
 * live surface). The MUSIC album sheet still does NOT: it leaves antd's own near-white container in
 * place, so handing it a dark backdrop's `--text-primary` would paint light text on a white card —
 * the exact bug the `sheet-modal--themed` block in `Components/SheetModal.css` exists to warn
 * about. Tokenise first, wire after.
 */
export function sectionSkinStyle(section: string, extras: Record<string, string> | undefined, theme: SkinFamily, view?: string | null): Record<string, string> {
  const t = skinTokens(section, extras, theme, view);
  if (Object.keys(t).length === 0) return t;
  const out: Record<string, string> = { ...t };
  const bg = t["--skin-bg"];
  if (bg) {
    out["--card-surface"] = t["--skin-card"] ?? bg;
    out["--card-border"] = t["--skin-line"];
    out["--content-bg"] = bg;
    out["--text-primary"] = t["--skin-ink"];
    out["--text-title"] = t["--skin-ink"];
    out["--text-secondary"] = t["--skin-sub"];
    out["--text-muted"] = t["--skin-sub"];
    out["--bg"] = bg;
    out["--ink"] = t["--skin-ink"];
    out["--sub"] = t["--skin-sub"];
    out["--line"] = t["--skin-line"];
    out["--chrome"] = t["--skin-chrome"];
  }
  if (t["--skin-display"]) {
    out["--font-display"] = t["--skin-display"];
    out["--font-header"] = t["--skin-header"];
    out["--font-mono"] = t["--skin-mono"];
    out["--display-tracking"] = t["--skin-tracking"];
    out["--display-weight"] = t["--skin-weight"];
  }
  return out;
}

/**
 * The view a section's skin resolves against when the reader is NOT inside the catalog host (a
 * modal, a page beside the grid): the URL's `?view=`, else the section's remembered default, else
 * the fallback the caller names.
 */
export function skinViewFor(section: string, search: string, fallback = "grid"): string {
  const fromUrl = new URLSearchParams(search || "").get("view");
  return fromUrl ?? readCatalogDefaults(section).view ?? fallback;
}

/**
 * A modal's skin tokens, live: re-reads on every tweak write and on a light/dark switch. The
 * portal renders outside the section root, so it cannot inherit them — they ride
 * `styles={{ wrapper }}` (never `wrapProps.style`).
 */
export function useSectionSkinStyle(section: string, view?: string | null): Record<string, string> {
  const theme = useSiteTheme();
  const [tick, setTick] = useState(0);
  useEffect(() => subscribeTweaks(section, () => setTick((t) => t + 1)), [section]);
  void tick;
  return sectionSkinStyle(section, readTweaks(section).extras, theme, view);
}

/**
 * The same for a section's detail modal, which is opened BY the URL (`?title=`, `?game=`,
 * `?album=`, `?photo=`) and mounts fresh with `?view=` already on it: the view is read from the
 * live location rather than from a router context, so a modal rendered outside a `<Router>` (the
 * arcade's own unit tests do exactly that) still gets its skin instead of throwing.
 */
export function useRouteSkinStyle(section: string, fallbackView = "grid"): Record<string, string> {
  const search = typeof window !== "undefined" ? window.location.search : "";
  return useSectionSkinStyle(section, skinViewFor(section, search, fallbackView));
}
