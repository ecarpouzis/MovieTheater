/**
 * The Books section's skin REGISTRATION. Since R9 S5 the machinery — resolve, apply, the tweak
 * rows, the modal style object — is the catalog's (`catalog/skin/skin.ts`); what stays here is the
 * DATA that is Books' own: the standalone's nine backdrops (five paper, four timber/night) and its
 * four display-type themes, plus the `@fontsource` imports those faces need, which must stay in
 * the Books chunk rather than shipping to every section.
 *
 * MT's `data-theme` remains the light/dark authority: a backdrop belongs to a family, and a
 * remembered value from the other family falls back to that family's default. The backdrop is
 * remembered PER VIEW (the standalone's per-layout background memory: Bookcase on the Shelves,
 * Paper on the Grid); the type theme is per store.
 *
 * `tokenPrefix: "books"` keeps the `--books-*` names five stylesheets and the reader are written
 * against; `paintHost: false` because Books paints its OWN root (`.books-section` — the tabs and
 * the plates take the backdrop too), not the catalog host's box.
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
import {
  BACKDROP_EXTRA, TYPE_EXTRA, registerSectionSkin, siteTheme,
  type BackdropDef, type SectionSkin, type SkinFamily, type TypeDef,
} from "../../catalog/skin/skin";

export type SiteTheme = SkinFamily;
export { BACKDROP_EXTRA, TYPE_EXTRA as DISPLAY_EXTRA, siteTheme };

export const BOOKS_BACKDROPS: Record<string, BackdropDef> = {
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

export const BOOKS_DISPLAY_FONTS: Record<string, TypeDef> = {
  pulp: { label: "Pulp", display: '"Alfa Slab One", "Rockwell", "Times New Roman", serif', header: '"Alfa Slab One", "Rockwell", "Times New Roman", serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "0em", weight: "400" },
  newsprint: { label: "News", display: '"Anton", "Impact", "Arial Black", sans-serif', header: '"Anton", "Impact", "Arial Black", sans-serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "0.005em", weight: "400" },
  stencil: { label: "Stencil", display: '"Bungee", "Bowlby One", sans-serif', header: '"Bungee", "Bowlby One", sans-serif', mono: '"Space Mono", "JetBrains Mono", monospace', tracking: "0em", weight: "400" },
  editorial: { label: "Edit", display: '"Instrument Serif", "Times New Roman", serif', header: '"Instrument Serif", "Times New Roman", serif', mono: '"JetBrains Mono", ui-monospace, monospace', tracking: "-0.01em", weight: "400" },
};

export const DEFAULT_DISPLAY_FONT = "pulp";

export const BOOKS_SKIN: SectionSkin = {
  backdrops: BOOKS_BACKDROPS,
  defaults: DEFAULT_BACKDROP,
  types: BOOKS_DISPLAY_FONTS,
  defaultType: DEFAULT_DISPLAY_FONT,
  perView: true,
  tokenPrefix: "books",
  // Books paints `.books-section` itself (books.css) — the whole section, not the results box.
  paintHost: false,
};

/** The browse, Novels and Kids each have their own catalog host, so each has its own store. */
export const BOOKS_SKIN_STORES = ["books", "books-novels", "books-kids"] as const;
for (const store of BOOKS_SKIN_STORES) registerSectionSkin(store, BOOKS_SKIN);

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
