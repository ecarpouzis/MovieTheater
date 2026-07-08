# MovieTheater — Light Theme Redesign Spec

Handoff for implementation (Claude Opus / Claude Code) against https://github.com/ecarpouzis/MovieTheater.
Reference mockups: option **3a/3b/3c (light frames)** in the Fable exploration. The mockups are miniatures —
**do NOT copy their pixel sizes. Keep the site's current card dimensions and grid density.** This spec restyles
surfaces, color, type, and chips only.

## 1. Principles
- Light content area + dark feature-colored sidebar (as today). Light won for card scannability.
- One shared card/chip/button system across Movies, Board Games, Arcade; feature identity = sidebar color + accent hue only.
- Arcade keeps its neon purple. All handpicked card metadata stays exactly as-is.
- Seen/Want must remain actionable directly on movie cards.

## 2. Type
- Font: "Instrument Sans" (Google Fonts), fallback system-ui/Helvetica. Weights 400/500/600/700.
- At real scale: card title 16px/700 + year 13px/400 muted beside it; chips 11px; cast links 12px;
  description 13px/1.5, clamp 2–3 lines (`-webkit-line-clamp`); buttons 12–13px/600.
- Sidebar: feature name 15px/700; nav rows 13px; section labels 10px/600, letter-spacing .12em, uppercase.

## 3. Color tokens (light theme)
Shared neutrals:
- Content bg `#F2F3F5` (Movies) / `#F4F5F2` (Board Games) / `#F5F3F8` (Arcade) — same lightness, hue-tinted toward the feature.
- Card surface `#FFFFFF`, border `#E3E4E8` (tint per feature: `#E2E5DF` BG, `#E5E1EC` Arcade), shadow `0 1px 2px rgba(20,30,50,.05)`.
- Primary text `#1A1C22`, secondary `#5C5F68`, muted `#9B9EA6`.
- Rating gold: text `#8A6510` on bg `#FAF3DD`.

Feature identity:
- **Movies** — sidebar `#131C2E`; accent `#2F6FC4` (Seen button, cast links, active states). Want accent `#D0426B`.
- **Board Games** — sidebar `#12261C`; accent `#2E9E63`. BGG chips keep their per-stat colors:
  players `#2C62B8` / border `#B9CDEC`; time `#8A6510` / `#E6D5A3`; rating gold chip; comments `#B0508A` / `#ECC3DC`.
- **Arcade** — sidebar `#231539`; accent (buttons, SNES chip) **neon purple `#A44EE0`**, chip text `#8A3FC0`, chip border `#D9C3EF`.

Sidebar internals (all features): inputs/selects on a darker well (`sidebar bg` darkened ~40%), 1px `rgba(255,255,255,.12)`
border, radius 5–6px; light text; active nav row = accent at 20% alpha bg.

### 3b. Dark theme (optional / toggle)
Approved as a secondary mode — see `reference-*-dark.png`. Same layout & accents; swap surfaces:
- Content bg: Movies `#141A26`, Board Games `#141C17`, Arcade `#161020` (feature-tinted near-black).
- Card surface `#1E2739` (Movies) / `#1D2B22` (BG) / `#211736` (Arcade); border `rgba(255,255,255,.09)`; no shadow.
- Text: primary `#FFFFFF`, secondary `#AAB4C6`, muted `#8B95A8`. Chips go to accent-at-~13%-alpha bg with bright text.
- Arcade: add `box-shadow:0 0 12px rgba(164,78,224,.45)` on the Start room button so the neon reads.
- **Icons in dark mode = the white PNGs as-is.** In light mode, recolor them (CSS `filter` or pre-tinted copies) to
  the neutral/accent ink so they're visible on light surfaces.

## 4. Components
**Card (Movies)** — flex row: poster left (current size, radius 4px, striped placeholder while loading);
right column: title+year → chips row (MPA outlined, runtime gray chip, ★ gold chip) → cast links (accent color,
single line ellipsis) → description clamp → actions row pinned bottom (`margin-top:auto`).
- Seen button: filled accent, white text, eye icon (SVG: ellipse + pupil circle), radius 5–6px.
- Want button: outlined gray idle (`♡ Want`), filled `#D0426B` white when active (`♥ Want`).
- Inactive state of either = outlined neutral.

**Card (Board Games)** — same shell; square box art; BGG stat chips (👥 ⏱ ★ 💬) in their colors; description clamp 3;
no Seen/Want. Keep "Powered by BGG" plaque at sidebar bottom.

**Card (Arcade)** — vertical: box art top; title; chips SNES/players/region (SNES chip in purple, others neutral);
region select where applicable; full-width `▶ Start room` button in `#A44EE0`.
- Grid container needs `align-content:start` so cards don't stretch to sidebar height.

**Sidebar** — order: feature switcher header (icon + name + ▾, dropdown lists all five features with hue dots:
Movies `#4A90E2`, TV `#38B6C9`, Arcade `#9A7BD4`, Board Games `#2E9E63`, Comics `#D98936`); user row (avatar circle,
name, **Playlists button** — small accent-outlined pill `≡ Playlists`; the old My Playlists bar is removed from the page);
Seen/Want/Rate rows with icons + count pills (Movies only); search/filter inputs; sort; letter grid.
- Icons: use `icons/*.png` (extracted from the live site, white w/ alpha; render 14–16px, `image-rendering:pixelated`).
- Seen / Want / Rate each have a filled + outline pixel icon in `icons/` (`seen-*`, `want-*`, `rate-*`): outline = inactive, filled = active.
- Optional passive fill for empty space below filters (pick one, keep subtle): giant feature icon watermark
  bottom-right, ~7% opacity, rotated ~-12°, `image-rendering:pixelated` (mock T2 — recommended); or scanline texture
  `repeating-linear-gradient(0deg, transparent 0 5px, rgba(255,255,255,.018) 5px 6px)` + centered footer plaque
  ("143 GAMES · EST. 2014") (T3).

**Now Playing rail (Movies)** — stays. Dark strip `#0D1219` above the grid: red dot + "NOW PLAYING" 10px/700
letter-spaced, channel thumbnails w/ channel name + current item, "All channels →" right-aligned.

## 5. Assets in this folder
- `icons/movie.png, tv.png, arcade.png, boardgames.png, comics.png` — white-on-transparent nav pictograms (existing site icons, re-extracted).
- `icons/seen-filled.png` + `seen-outline.png`, `want-filled.png` + `want-outline.png`, `rate-filled.png` + `rate-outline.png` —
  NEW pixel-art action icons matching the nav style. Use **filled = active/selected**, **outline = inactive** for each
  Seen / Want / Rate control (e.g. Want shows `want-outline` idle → `want-filled` once wanted). White w/ alpha; tint via CSS
  `filter` or recolor if needed; render 13–16px, `image-rendering:pixelated`.
- `reference-3a-movies-light.png`, `reference-3b-boardgames-light.png`, `reference-3c-arcade-light.png` —
  screenshots of the approved light-theme mockups (miniature scale; follow their proportions/colors, not pixel sizes).
- `reference-3a-movies-dark.png`, `reference-3b-boardgames-dark.png`, `reference-3c-arcade-dark.png` —
  the same three features in the optional dark theme (see §3b).

## 6. Out of scope / unchanged
Card dimensions & density, all data fields, filtering/sorting logic, routing, Comics (separate project).
