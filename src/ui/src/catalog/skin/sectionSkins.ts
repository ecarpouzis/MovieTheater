/**
 * The site sections' backdrop + type sets (R9 S5). Books registers its own — the Long Box's nine
 * paper/timber scenes and its four bundled display faces live in `Pages/Books/booksTheme.ts` so
 * the `@fontsource` packages stay in the Books chunk. Everything here is data: no imports of a
 * section's code, no fonts, no images.
 *
 * How the nine were chosen, per section: the FIRST swatch is the section's own surface — it writes
 * no tokens at all, so it is exactly what theme.css already paints for that `data-feature` in
 * either theme (the reason a fresh install looks identical to the day before the skin landed).
 * The other eight are four light and four dark, drawn from the section's own hue as it is stated
 * in `theme.css` (movies' screen blue, TV's teal, boardgames' felt green, music's wine, the
 * arcade's neon purple, photos' warm paper) plus a neutral and a warm alternative in each family
 * so a reader who wants LESS colour has somewhere to go.
 *
 * A swatch states only what it changes: `bg` (the page), `card` (the surface a card sits on),
 * `ink` / `sub` (text), `line` (hairlines), `chrome` (the translucent chrome the pills and the
 * tweaks card paint with). `line` and `chrome` are derived with `color-mix` from the ink and the
 * page, so a set stays internally consistent.
 */
import { registerSectionSkin, type BackdropDef, type SectionSkin, type TypeDef } from "./skin";

/** The section's own surface: selectable, writes nothing. Every section's family default. */
const SITE: BackdropDef = { family: "any", label: "Site", color: "var(--content-bg)", siteDefault: true };

/** A designed swatch — `line`/`chrome` derived so every set is internally consistent. */
function sw(family: "light" | "dark", label: string, bg: string, card: string, ink: string, sub: string, lineMix = 13): BackdropDef {
  return {
    family, label, bg, card, ink, sub,
    line: `color-mix(in oklab, ${ink} ${lineMix}%, transparent)`,
    chrome: `color-mix(in oklab, ${bg} 86%, transparent)`,
  };
}

/**
 * The site's own faces, as a type theme: `site` writes nothing (Space Grotesk over Instrument
 * Sans, the site's charter), the other three re-cut the display voice from faces the site ALREADY
 * self-hosts (`theme.css` @font-face) — no new download on any section.
 */
const SITE_TYPES: Record<string, TypeDef> = {
  site: { label: "Site", display: "", header: "", mono: "", tracking: "", weight: "", siteDefault: true },
  serif: {
    label: "Serif",
    display: '"Marcellus", "Palatino Linotype", Georgia, serif',
    header: '"Marcellus", "Palatino Linotype", Georgia, serif',
    mono: "ui-monospace, Menlo, monospace",
    tracking: "0.005em",
    weight: "400",
  },
  plain: {
    label: "Plain",
    display: '"Instrument Sans", system-ui, -apple-system, "Segoe UI", Helvetica, Arial, sans-serif',
    header: '"Instrument Sans", system-ui, -apple-system, "Segoe UI", Helvetica, Arial, sans-serif',
    mono: "ui-monospace, Menlo, monospace",
    tracking: "-0.015em",
    weight: "700",
  },
  mono: {
    label: "Mono",
    display: "ui-monospace, Menlo, Consolas, monospace",
    header: "ui-monospace, Menlo, Consolas, monospace",
    mono: "ui-monospace, Menlo, Consolas, monospace",
    tracking: "0.01em",
    weight: "600",
  },
};

const base = (backdrops: Record<string, BackdropDef>): SectionSkin => ({
  backdrops,
  defaults: { light: "site", dark: "site" },
  types: SITE_TYPES,
  defaultType: "site",
  perView: true,
  paintHost: true,
});

/** Movies/TV — the screening room: cool paper by day, four ways of being dark at night. */
export const MOVIES_SKIN = base({
  site: SITE,
  paper: sw("light", "Paper", "#f7f5f0", "#fffdf8", "#1d1a15", "#635c52"),
  marquee: sw("light", "Marquee", "#f4ecdd", "#fdf7ea", "#241c0e", "#6f6045"),
  daylight: sw("light", "Daylight", "#eaf0f7", "#ffffff", "#17202c", "#566579"),
  blueprint: sw("light", "Blueprint", "#e4ebf4", "#f5f9fd", "#14243a", "#4d6280", 16),
  theater: sw("dark", "Theater", "#141a26", "#1e2739", "#ffffff", "#aab4c6", 10),
  midnight: sw("dark", "Midnight", "#0b0f18", "#151c2a", "#eef2fa", "#8d99ae", 10),
  noir: sw("dark", "Noir", "#16161a", "#202027", "#f2f0ec", "#9b978f", 10),
  velvet: sw("dark", "Velvet", "#1d1220", "#2a1b2e", "#f6ecf2", "#b294a8", 12),
});

/** TV — the guide's teal, on studio white or a late-night signal. */
export const TV_SKIN = base({
  site: SITE,
  studio: sw("light", "Studio", "#eef4f4", "#fbfefe", "#162326", "#55686c"),
  paper: sw("light", "Paper", "#f7f6f2", "#fffefb", "#1c1a16", "#635d53"),
  mint: sw("light", "Mint", "#eaf4ef", "#f8fdfa", "#14251e", "#4f6d61"),
  sand: sw("light", "Sand", "#f2eee5", "#fdfaf3", "#211d15", "#6a6152"),
  latenight: sw("dark", "Late night", "#10181b", "#1a262b", "#ffffff", "#aab4c6", 10),
  deepsea: sw("dark", "Deep sea", "#071316", "#102125", "#e9f4f6", "#86a3a9", 10),
  static: sw("dark", "Static", "#14171a", "#1e2327", "#f0f2f4", "#98a0a8", 10),
  neon: sw("dark", "Neon", "#08191c", "#0f272c", "#e6fbfd", "#7fc0c9", 12),
});

/** Board games — the table: felt, linen, oak. */
export const BOARDGAMES_SKIN = base({
  site: SITE,
  linen: sw("light", "Linen", "#f6f4ee", "#fffdf7", "#1d1b15", "#635e52"),
  meadow: sw("light", "Meadow", "#eef4ec", "#f9fdf8", "#17251a", "#55705c"),
  oak: sw("light", "Oak", "#f0e9dd", "#fbf6ec", "#241d12", "#6d6049", 14),
  chalk: sw("light", "Chalk", "#eef1f4", "#fbfcfd", "#191d22", "#5b6572"),
  felt: sw("dark", "Felt", "#141c17", "#1d2b22", "#ffffff", "#aab4c6", 10),
  forest: sw("dark", "Forest", "#0a1410", "#12211a", "#e9f3ec", "#86a394", 10),
  slate: sw("dark", "Slate", "#171a1c", "#212629", "#f0f2f3", "#99a1a5", 10),
  walnut: sw("dark", "Walnut", "#1d150e", "#2a2016", "#f3ebdd", "#ab9a80", 12),
});

/** Music — the sleeve: paper and blush by day, wine and vinyl at night. */
export const MUSIC_SKIN = base({
  site: SITE,
  paper: sw("light", "Paper", "#f8f5f0", "#fffdf9", "#1e1a16", "#655c52"),
  blush: sw("light", "Blush", "#f7edee", "#fefafa", "#26181a", "#74585c"),
  dust: sw("light", "Dust", "#f1ece4", "#fbf7f1", "#221d16", "#6b6154"),
  cool: sw("light", "Cool", "#edf1f5", "#fbfcfe", "#191d24", "#5a6572"),
  wine: sw("dark", "Wine", "#1c1315", "#2a1c1f", "#ffffff", "#aab4c6", 10),
  vinyl: sw("dark", "Vinyl", "#101012", "#1a1a1d", "#f1eeee", "#979193", 10),
  smoke: sw("dark", "Smoke", "#17181b", "#212328", "#eff0f3", "#969aa3", 10),
  ember: sw("dark", "Ember", "#1f1210", "#2d1b17", "#f8ece6", "#b5907f", 12),
});

/** Arcade — the cabinet: bright plastic by day, four kinds of glow in the dark. */
export const ARCADE_SKIN = base({
  site: SITE,
  paper: sw("light", "Paper", "#f7f5f1", "#fffdfa", "#1f1a15", "#665e53"),
  bubblegum: sw("light", "Bubblegum", "#f8eff7", "#fefafd", "#2a1730", "#77627e"),
  ice: sw("light", "Ice", "#eef2f9", "#fbfcfe", "#1a1f2b", "#5c6577"),
  sunset: sw("light", "Sunset", "#f9efe6", "#fefaf4", "#2a1a10", "#7a6350"),
  cabinet: sw("dark", "Cabinet", "#140c1f", "#1f142e", "#ffffff", "#a99cc0", 12),
  neon: sw("dark", "Neon", "#0c0616", "#180d28", "#f4e9ff", "#a48fc4", 12),
  crt: sw("dark", "CRT", "#07120c", "#0f1e15", "#e7fbee", "#7fb894", 12),
  synth: sw("dark", "Synth", "#1a0a22", "#26102f", "#ffe9fb", "#c08ab8", 12),
});

/** Photos — the album: a print reads as MOUNTED, so every swatch is a paper or a darkroom. */
export const PHOTOS_SKIN = base({
  site: SITE,
  mat: sw("light", "Mat", "#f2eee6", "#fefdfa", "#201c16", "#5f574a"),
  linen: sw("light", "Linen", "#ece7db", "#faf7f0", "#221e16", "#63594a", 14),
  sepia: sw("light", "Sepia", "#ece0cc", "#f9f1e2", "#2b2113", "#6f6047", 15),
  cool: sw("light", "Cool", "#e9edf1", "#fafcfd", "#1b1f24", "#5a6470"),
  shoebox: sw("dark", "Shoebox", "#14110d", "#211b15", "#f4efe6", "#c3b8a8", 10),
  darkroom: sw("dark", "Darkroom", "#0d0b09", "#171310", "#f0e9de", "#a2988a", 10),
  slate: sw("dark", "Slate", "#15171a", "#1f2225", "#eef0f2", "#969ba1", 10),
  walnut: sw("dark", "Walnut", "#1c140d", "#291f15", "#f3e9da", "#ab9a80", 12),
});

export const SITE_SECTION_SKINS: Record<string, SectionSkin> = {
  movies: MOVIES_SKIN,
  tv: TV_SKIN,
  boardgames: BOARDGAMES_SKIN,
  music: MUSIC_SKIN,
  arcade: ARCADE_SKIN,
  photos: PHOTOS_SKIN,
};

for (const [section, skin] of Object.entries(SITE_SECTION_SKINS)) registerSectionSkin(section, skin);
