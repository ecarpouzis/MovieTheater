# Handoff: Arcade — Browse / Library Page

## Overview
A full-page redesign of the **Arcade** browse screen — where a user picks a retro game to open a multiplayer "room" and invites friends via a link. It replaces a flat grey layout with a focused dark UI (and a light counterpart), a proper **Live rooms** strip, and a game grid built on **smart box-art tiles** that accept any box-art aspect ratio without cropping.

## About the Design Files
The file in this bundle (`MovieTheater Explorations.dc.html`) is a **design reference authored in HTML** — a prototype showing intended look and behavior, **not production code to copy directly**. It is an exploration canvas containing several turns; the finished Arcade design is **turn 4** (options `4a` dark and `4b` light).

The task is to **recreate this design in the target codebase's existing environment** (React, Vue, Svelte, SwiftUI, etc.) using its established component patterns, styling system, and data layer. If no front-end environment exists yet, choose the most appropriate framework for the project and implement there. Do not ship the HTML directly.

## Fidelity
**High-fidelity.** Colors, typography, spacing, radii, and layout are final and specified below. Recreate pixel-accurately using the codebase's own primitives. The only intentionally abstract part is **box art**, which is shown as striped placeholders — in production these are real game cover images (see *Smart box-art tile* below).

## Screens / Views

### Arcade Browse (single screen, two themes)
- **Purpose**: Browse/filter the game library, see any live rooms, and start a new room for a chosen game.
- **Overall layout**: Horizontal flex. Fixed **248px** left sidebar (dark in both themes) + fluid main column. Design frame width used in the mock is **1440px**; main content padding is **34px 40px 44px**. Vertical rhythm between main sections is **26px** (`gap`).
- **Theme note**: `4a` is the dark theme (hero), `4b` the light theme. Sidebar is the **same dark rail** in both. A light/dark toggle lives at the bottom of the sidebar.

#### Sidebar (248px, both themes)
Dark vertical gradient rail. Top → bottom, `gap:16px`, padding `20px 18px`:
1. **Brand**: word‑mark "Arcade" in Space Grotesk 700, 17px, letter-spacing −.01em, with a small ▾ pushed to the right. *(No logo/icon mark — the previous joystick glyph was intentionally removed.)*
2. **Account chip**: 26px circular avatar (gradient `135deg, #a44ee0→#6f2fb0`, white initial "E"), name "Eric" (13px/600), settings gear icon (stroke `#b5a3d6`). Chip bg `rgba(255,255,255,.04)`, border `rgba(255,255,255,.07)`, radius 10px, padding `9px 10px`.
3. Hairline divider `rgba(255,255,255,.07)`.
4. **FILTER LIBRARY** section (label: 9.5px/700, letter-spacing .16em, `#7c6a9c`):
   - Search field (magnifier icon + "Search title…" placeholder)
   - Selects, each with a small uppercase caption above: **SORT BY** (default "Rating (high → low)"), **SYSTEM** (default "All systems 24,710"), **PLAYERS** ("Any player count"), **GENRE** ("All genres"), **MODS & HACKS** ("Official releases")
   - **Clear filters** action (× icon + text, `#b98ee6`)
   - Field style: bg `#0c0714`, border `rgba(255,255,255,.12)`, radius 8px, padding `8px 10px`, text 12.5px `#e8dcf5`, caret glyph ▾ `#7c6a9c`
5. Pushed to bottom (`margin-top:auto`, `gap:12px`):
   - **Dark/Light mode toggle** row (label + pill switch). Dark: track `#a44ee0` w/ glow, knob right. Light: track `rgba(255,255,255,.16)`, knob left.
   - **Log out** ghost button (border `rgba(164,78,224,.3)`, radius 8px, text `#b98ee6`).

#### Main column
1. **Header row** (`space-between`, align bottom):
   - Left: `h1` "Arcade" — Space Grotesk 700, **38px**, letter-spacing −.02em, line-height 1. Subtitle 13.5px, muted, max-width 560px: *"Pick a game to open a room, then send friends the link to play together."*
   - Right (align end): two connection **pills** — `● Balanced · 5 Mbps ▾` (green status dot `#5ad19a`) and `Error correction: On ▾`; caption below: *"Applies to rooms you start"*. Pills: bg `rgba(255,255,255,.05)`, border `rgba(255,255,255,.1)`, radius 9px, padding `8px 12px`, 12px text. (Light: white bg, border `#e2ddec`, subtle shadow.)
2. **Live rooms** section:
   - Heading row: pulsing red dot `#e8657f` (with glow in dark) + "Live rooms" (Space Grotesk 600, 15px) + "1 open now" muted.
   - **Room card**: horizontal flex, `gap:18px`, padding `16px 18px`, radius 14px, **3px left accent border `#a44ee0`**. Dark bg is a left-to-right purple wash `linear-gradient(90deg, rgba(164,78,224,.14), …, transparent)`; light bg is white with soft shadow.
     - 64×64 rounded thumbnail (smart box-art tile, small variant)
     - Title "007 - GoldenEye" (16px/700) + system chip "Nintendo 64" + "1 playing · 3 seats free"
     - Right cluster: **seat dots** (1 filled `#a44ee0`, 3 dashed empty `rgba(255,255,255,.28)` / `#c9bcdd`), host avatar + "Eric hosting", and a **Join room** button (filled accent, radius 9px, padding `10px 20px`, glow in dark).
3. **Games** heading: "Games" (Space Grotesk 600, 15px) + "13,178 titles" muted + a mono hint: *"box art natural aspect (never cropped) · details + summary beside it · two-up grid"*.
4. **Game grid**: CSS Grid, **`repeat(auto-fill, minmax(355px, 1fr))`**, **`gap:16px`** — a proper responsive multi-column card grid (3-up at the 1440px design width, reflowing to fewer columns as the viewport narrows). Each cell is a game card (below).

#### Game card (art left, details right)
Horizontal card — box art on the left at its **natural aspect ratio**, all textual detail in a column to its right (never crammed underneath).
- Container: **horizontal flex**, `gap:14px`, padding 12px, radius 12px. Dark: `linear-gradient(180deg,#241834,#1a1029)`, border `rgba(164,78,224,.18)`. Light: `#fff`, border `#e5e1ec`, shadow `0 2px 6px rgba(40,20,60,.06)`.
- **Left: box art** — `flex:none`, **`height:118px`**, **`aspect-ratio:<w>/<h>`** (the cover's own ratio → width follows, so landscape boxes are wider, portrait narrower, all the same height), radius 7px, `overflow:hidden`. Dark border `rgba(255,255,255,.08)` + shadow `0 6px 16px rgba(0,0,0,.5)`; light border `#e5e1ec` + shadow `0 3px 10px rgba(40,20,60,.16)`. Card uses `align-items:center` so the art is vertically centred against the details. Mock uses a per-game tinted striped placeholder; **production = the real cover image** (`object-fit:cover`; the box already matches the cover ratio so nothing crops). **Rating badge** absolute top-right (`★ <score>`, gold `#f5c451` dark / `#b58200` light, translucent chip).
- **Right: details** — `flex:1`, `min-width:0`, column, `gap:7px`:
  - Title 15px/700, single line w/ ellipsis
  - **Tag line 1** (`display:flex; gap:5px`): **system chip** (dark `#d3aef7` on `rgba(164,78,224,.4)` border; light `#8a3fc0` on `#d9c3ef`) + **players** meta chip.
  - **Tag line 2** (`display:flex; gap:5px`): **region** + **genre** meta chips.
  - *(Tags are two fixed lines, not one wrapping row — every card has the identical two-line structure regardless of chip lengths. Meta chip dark `#c3b8d9` on `rgba(255,255,255,.06)`; light `#6b6478` on `#f0edf5`; radius 4px, 10px.)*
  - **Summary**: DB blurb, 11.5px, line-height 1.4, muted (`#a99cc0` dark / `#6b6478` light), **2-line clamp**.
  - **Actions** (`display:flex; align-items:center; gap:12px; flex-wrap:nowrap` — **always one line, side-by-side**; both children `flex:none`): **▶ Start room** button (filled accent, radius 8px, padding `8px 14px`, 11.5px/600, `white-space:nowrap`; dark glow `0 0 14px rgba(164,78,224,.4)`) + **My saves** text link (11px/600, `white-space:nowrap`, `#b98ee6` dark / `#8f3fd4` light). Art height is tuned to 118px specifically so the widest (4:3 landscape) covers still leave room for this row to stay on one line at the 3-up column width.

## Box art: natural aspect, uniform height (the key mechanic)
Real game box art has **wildly different aspect ratios** — PlayStation/PS2 jewel cases are portrait (~3:4), SNES and Sega Master System boxes are landscape (~4:3), cartridge scans vary. Instead of cropping/letterboxing everything to a fixed poster shape (or matting it on a colored fill), each cover is shown **at its true ratio, pinned to one shared height** (`height:118px; aspect-ratio:<w>/<h>` → width is derived). Because the art lives on the **left of a horizontal card**, its variable width is absorbed inside the card — the details column simply flexes to whatever width is left. Nothing is ever cropped, and the responsive multi-column grid (`auto-fill, minmax(355px,1fr)`) stays tidy because every card is the same total width regardless of its cover's shape.

## Interactions & Behavior
- **Start room** → creates a room for that game and routes to the room screen (not in this mock); user then shares the room link.
- **Join room** (Live rooms) → joins the open room.
- **Filters** (sidebar) filter/sort the grid live; **Clear filters** resets them.
- **Light/Dark toggle** switches theme.
- **Hover states** (not shown in mock — recommended): card lift/scale on hover, Start-room button brighten, room card border intensify. Buttons/links get standard pointer affordance.
- **Responsive**: grid should reflow column count (e.g. `repeat(auto-fill, minmax(200px, 1fr))`) below the 1440px design width; sidebar can collapse to a drawer on narrow viewports.

## State Management
- `filters`: `{ query, sortBy, system, players, genre, modsHacks }`
- `theme`: `'dark' | 'light'`
- `games`: paginated list (count shown as "13,178 titles") with per-game `{ title, system, genre, players, region, rating, summary, coverUrl, coverAspect }` (**summary** = the DB blurb shown as the 2-line description; **coverAspect** = `width/height` of the cover, used for the box's `aspect-ratio`)
- `liveRooms`: list of open rooms `{ game, system, playersIn, seatsFree, host, seats[] }`
- Data fetching: game library (filtered/sorted/paged) and live rooms (ideally realtime/polled).
- No color extraction needed — covers render at natural aspect on a plain card (the earlier ambient-fill approach was dropped).

## Design Tokens

**Accent (Arcade purple)**
- Primary `#a44ee0` · deep `#6f2fb0` · light-theme button `#8f3fd4`
- Text-on-dark accent `#d3aef7` · link `#b98ee6` (dark) / `#8f3fd4` (light) · muted-accent `#c9a3ef`

**Dark theme surfaces**
- Page `#140c1f` (+ radial purple glow top-right `rgba(164,78,224,.16)`) · scanline overlay `rgba(255,255,255,.014)` 3/4px stripes
- Sidebar `linear-gradient(180deg,#180f26,#120b1c)` · field `#0c0714`
- Card `linear-gradient(180deg,#241834,#1a1029)` · tile base `#160e22`
- Text `#fff` / `#f0e8fa` · muted `#a99cc0` / `#7c6a9c` · caption `#8a76ab`

**Light theme surfaces**
- Page `#f5f3f9` · card `#fff` · tile base `#ece7f3`
- Text `#221733` / title `#1c0f2c` · muted `#6b6478` / `#9b91ab`
- Borders `#e5e1ec` / `#e2ddec` / chip meta bg `#f0edf5`

**Status**
- Live/red dot `#e8657f` · connection-ok green `#5ad19a` / `#2fae7a` · rating gold `#f5c451` / `#b58200`

**Spacing** — section gap 26px · card grid gap 18px · card body gap 8px · sidebar gap 16px/11px · main padding `34px 40px 44px`
**Radius** — pills/fields 8–10px · cards/room 12–14px · avatars/dots 50% · art 3px
**Shadow** — card (light) `0 2px 6px rgba(40,20,60,.06)` · art (dark) `0 8px 24px rgba(0,0,0,.55)` · accent glow `0 0 16–18px rgba(164,78,224,.4–.45)`

**Typography**
- Display / headings: **Space Grotesk** (700 titles, 600 section heads)
- Body / UI: **Instrument Sans** (400/500/600/700)
- Mono captions: system `ui-monospace, Menlo, monospace`
- Scale: h1 38px · room/card title 16px · section head 15px · body 13.5px · controls 12.5px · chips 10px · captions 9.5–11px

## Assets
- **Box art / covers**: real game cover images (per game). Mock uses per-game tinted striped placeholders — wire each to the real cover image (fixed height, natural aspect).
- **Icons**: inline SVG (magnifier, gear, ×, ▾/▶/★ glyphs). No raster icon required. The prior `arcade.png` joystick mark was **removed** and should not be reintroduced.
- **Fonts**: Space Grotesk + Instrument Sans (Google Fonts).

## Files
- `MovieTheater Explorations.dc.html` — the design reference (this repo's exploration canvas). **Turn 4** contains the finished Arcade design: `#4a` (dark) and `#4b` (light). Earlier turns (`#t1`–`#t3`) are prior explorations for related screens and are context only.
- `arcade-dark-4a.png` — full-page render of the dark theme (the hero).
- `arcade-light-4b.png` — full-page render of the light theme.
