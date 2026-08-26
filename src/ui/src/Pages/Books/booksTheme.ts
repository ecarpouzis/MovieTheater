/**
 * The Books section's own skin, layered under the site's theme: the standalone's nine backdrops and
 * four display-type themes, both offered as tweak rows and applied to the section root as
 * `data-books-backdrop` + inline `--books-*` custom properties. MT's `data-theme` remains the
 * light/dark authority: a backdrop belongs to one family, and a remembered value from the other
 * family falls back to that family's default. The backdrop is remembered PER VIEW (the standalone's
 * per-layout background memory: Bookcase on the Shelves, Paper on the Grid); the type theme is per
 * section store.
 *
 * Fonts are bundled (`@fontsource`), imported here so they ship in the Books chunk only.
 */
import "@fontsource/alfa-slab-one";
import "@fontsource/anton";
import "@fontsource/bungee";
import "@fontsource/space-mono/400.css";
import "@fontsource/space-mono/700.css";
import "@fontsource/instrument-serif";
import "@fontsource/jetbrains-mono/400.css";
import "@fontsource/jetbrains-mono/500.css";
import "@fontsource/jetbrains-mono/600.css";
import "@fontsource-variable/baloo-2";
import "@fontsource-variable/fredoka";
import { readCatalogDefaults } from "../../catalog/state/useCatalogView";
import type { TweakExtra } from "../../catalog/types";

export type SiteTheme = "light" | "dark";

export interface BackdropTokens {
  bg: string;
  ink: string;
  sub: string;
  line: string;
  chrome: string;
  /** A scene (image) painted behind the content; `bg` when absent. */
  scene?: string;
}

export const BOOKS_BACKDROPS: Record<string, BackdropTokens & { family: SiteTheme; label: string }> = {
  paper: { family: "light", label: "Paper", bg: "#f6f5f1", ink: "#1a1714", sub: "#6c6258", line: "#1a17141a", chrome: "rgba(246,245,241,0.85)" },
  snow: { family: "light", label: "Snow", bg: "#fafaf7", ink: "#19171a", sub: "#6a6766", line: "#19171a19", chrome: "rgba(250,250,247,0.85)" },
  bone: { family: "light", label: "Bone", bg: "#ebe6dc", ink: "#191613", sub: "#65594c", line: "#1916131f", chrome: "rgba(235,230,220,0.85)" },
  pulp: { family: "light", label: "Pulp", bg: "#f2e6c8", ink: "#2a1d10", sub: "#7a5d3c", line: "#2a1d1022", chrome: "rgba(242,230,200,0.85)" },
  room: { family: "light", label: "Room", bg: "#eef4f7", ink: "#22303c", sub: "#5e7280", line: "rgba(34,48,60,0.13)", chrome: "rgba(238,244,247,0.85)", scene: "#cfe6f2 url(\"/catalog/room-bg.svg\") center center / cover no-repeat" },
  slate: { family: "dark", label: "Slate", bg: "#1c1f24", ink: "#f0ece4", sub: "#9a958a", line: "#f0ece422", chrome: "rgba(28,31,36,0.85)" },
  midnight: { family: "dark", label: "Midnight", bg: "#0d0f14", ink: "#ece8df", sub: "#857f70", line: "#ece8df1a", chrome: "rgba(13,15,20,0.85)" },
  archive: { family: "dark", label: "Archive", bg: "#241a10", ink: "#efe6d6", sub: "#ab9a80", line: "rgba(239,230,214,0.14)", chrome: "rgba(28,20,12,0.86)", scene: "#241a10 url(\"/catalog/archive-bg.svg\") center center / cover no-repeat" },
  bookcase: { family: "dark", label: "Bookcase", bg: "#5d3c1e", ink: "#f3e9d8", sub: "#c9b18f", line: "rgba(243,233,216,0.16)", chrome: "rgba(93,60,30,0.85)" },
};

export const DEFAULT_BACKDROP: Record<SiteTheme, string> = { light: "paper", dark: "slate" };

export interface DisplayFontTokens {
  display: string;
  header: string;
  mono: string;
  tracking: string;
  weight: string;
  label: string;
}

export const BOOKS_DISPLAY_FONTS: Record<string, DisplayFontTokens> = {
  pulp: { label: "Pulp", display: '"Alfa Slab One", "Rockwell", "Times New Roman", serif', header: '"Alfa Slab One", "Rockwell", "Times New Roman", serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "0em", weight: "400" },
  newsprint: { label: "News", display: '"Anton", "Impact", "Arial Black", sans-serif', header: '"Anton", "Impact", "Arial Black", sans-serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "0.005em", weight: "400" },
  stencil: { label: "Stencil", display: '"Bungee", "Bowlby One", sans-serif', header: '"Bungee", "Bowlby One", sans-serif', mono: '"Space Mono", "JetBrains Mono", monospace', tracking: "0em", weight: "400" },
  editorial: { label: "Edit", display: '"Instrument Serif", "Times New Roman", serif', header: '"Instrument Serif", "Times New Roman", serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "-0.01em", weight: "400" },
};

export const DEFAULT_DISPLAY_FONT = "pulp";

export const BACKDROP_EXTRA = "backdrop";
export const DISPLAY_EXTRA = "display";

export function siteTheme(): SiteTheme {
  if (typeof document === "undefined") return "light";
  return document.documentElement.dataset.theme === "dark" ? "dark" : "light";
}

/** The backdrop to use: the remembered one when it belongs to the current family, else the family default. */
export function resolveBackdrop(value: string | null | undefined, theme: SiteTheme): string {
  const b = value ? BOOKS_BACKDROPS[value] : undefined;
  return b && b.family === theme ? value! : DEFAULT_BACKDROP[theme];
}

export function resolveDisplayFont(value: string | null | undefined): string {
  return value && BOOKS_DISPLAY_FONTS[value] ? value : DEFAULT_DISPLAY_FONT;
}

/** The backdrop remembered for a view: `backdrop:<view>` first, the section-wide choice second. */
export function backdropExtraFor(extras: Record<string, string> | undefined, view: string | null | undefined): string | undefined {
  if (!extras) return undefined;
  return (view ? extras[`${BACKDROP_EXTRA}:${view}`] : undefined) ?? extras[BACKDROP_EXTRA];
}

/**
 * Which tweaks store and which view a Books URL is on — the two facts the skin resolves from. The
 * browse, Explore, the Shelf and the reader share the `books` store; Novels and Kids have their own
 * catalog hosts and stores. The view is the URL's, else the stored default, else the source's.
 */
export function booksSkinContext(pathname: string, search: string): { store: string; view: string } {
  const rest = String(pathname || "").replace(/^\/books\/?/, "").split(/[/?#]/)[0];
  const store = rest === "novels" ? "books-novels" : rest === "kids" ? "books-kids" : "books";
  const fallback = store === "books-novels" ? "grid" : store === "books-kids" ? "shelf" : "extended";
  const view = new URLSearchParams(search).get("view") ?? readCatalogDefaults(store).view ?? fallback;
  return { store, view };
}

/** The tweak rows a Books source registers. Backdrop options follow the site's light/dark family. */
export function booksTweakExtras(theme: SiteTheme): TweakExtra[] {
  return [
    {
      key: BACKDROP_EXTRA,
      label: "Backdrop",
      perView: true,
      options: Object.entries(BOOKS_BACKDROPS).filter(([, b]) => b.family === theme).map(([value, b]) => ({ value, label: b.label })),
    },
    {
      key: DISPLAY_EXTRA,
      label: "Type",
      options: Object.entries(BOOKS_DISPLAY_FONTS).map(([value, f]) => ({ value, label: f.label })),
    },
  ];
}

/** Apply the skin to the section root. Idempotent; the tokens are read by `books.css` and the modals. */
export function applyBooksTheme(root: HTMLElement | null, extras: Record<string, string> | undefined, theme: SiteTheme, view?: string | null): void {
  if (!root) return;
  const backdrop = resolveBackdrop(backdropExtraFor(extras, view), theme);
  const font = BOOKS_DISPLAY_FONTS[resolveDisplayFont(extras?.[DISPLAY_EXTRA])];
  const t = BOOKS_BACKDROPS[backdrop];
  root.dataset.booksBackdrop = backdrop;
  root.style.setProperty("--books-bg", t.bg);
  root.style.setProperty("--books-ink", t.ink);
  root.style.setProperty("--books-sub", t.sub);
  root.style.setProperty("--books-line", t.line);
  root.style.setProperty("--books-chrome", t.chrome);
  root.style.setProperty("--books-scene", t.scene ?? t.bg);
  root.style.setProperty("--books-display", font.display);
  root.style.setProperty("--books-header", font.header);
  root.style.setProperty("--books-mono", font.mono);
  root.style.setProperty("--books-tracking", font.tracking);
  root.style.setProperty("--books-weight", font.weight);
}

/** The same tokens, as a style object, for a portal (an antd modal wrap) that is not inside the section root. */
export function booksThemeStyle(extras: Record<string, string> | undefined, theme: SiteTheme, view?: string | null): Record<string, string> {
  const backdrop = resolveBackdrop(backdropExtraFor(extras, view), theme);
  const font = BOOKS_DISPLAY_FONTS[resolveDisplayFont(extras?.[DISPLAY_EXTRA])];
  const t = BOOKS_BACKDROPS[backdrop];
  return {
    "--books-bg": t.bg, "--books-ink": t.ink, "--books-sub": t.sub, "--books-line": t.line, "--books-chrome": t.chrome,
    "--books-scene": t.scene ?? t.bg, "--books-display": font.display, "--books-header": font.header, "--books-mono": font.mono,
    "--books-tracking": font.tracking, "--books-weight": font.weight,
  };
}
