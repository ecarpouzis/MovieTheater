# The catalog package — every view, for every section

`src/ui/src/catalog/` is the one browse surface the site's sections share: seven views (Grid, Wall,
List, Extended, Shelves, Newspaper, Directory), one band engine, one tweaks panel, one URL contract,
plus the rail family (facet rail, smart search, chips, saved searches) and the Explore page kit.
A section adopts it by writing a **source adapter** (how to page its rows and groups, and what to
offer) and mounting **`CatalogHost`** where its grid lives. Nothing in the package knows what a movie
or an album is; nothing in a section knows how a shelf is laid out.

## The contract (`catalog/types.ts`)

- **`CardItem`** — an image with an identity and a few labels: `kind` + `id` (+ the composite `key`),
  `title`, `subtitle`, `label`, `year`, `aspect` (w/h), `imageUrl` (+ `imageThumbUrl` for the dense
  views), `hue` (spines and placeholders), `rating` (0–100, for the Newspaper's lead pick), `badges`,
  `sortKey` (what the letter strip buckets on), `raw` (the section's row, for its modal — never for
  the views).
- **`CardGroup`** — a labelled run of cards: `key`, `label`, `totalItems` (the true size),
  `renderTotal` (how many cards will render when fully loaded — views reserve on THIS), `items`,
  `detail` (`synopsis` / `byline` / `kicker` / `tags` / `runLabel` for the Newspaper and headers).
- **`CatalogSource`** — what a section hands the views: `queryKey` (identity of the filter state —
  every band drops when it changes, never on a view/sort/tweak switch), `supports`, `groups`,
  `sorts` (+ `currentSort` when the SECTION owns the sort), `itemsModes`/`itemsLabels`,
  `listColumns`, `directory`, `fetchFlatBand(skip, top, sort)`, `fetchGroupBand(groupsSkip,
  groupsTop, perGroupTop, groupBy, sort)`, `fetchGroupMore(groupKey, skip, top, groupBy, sort)`,
  `letters(sort)`, `groupLetters(groupBy, sort)`, `onOpen(item)`, `onOpenGroup(group, groupBy)`,
  the GRID's card seam (`renderCard(item, view)` + `gridClass` + `gridCell` — see below),
  `emptyLabel`/`filtered` (below), and `dataVersion` (the SAME list edited in place: bands re-read,
  window/heights/scroll stay put).
- **`emptyLabel` + `filtered`** — an empty result is two different reports and every view used to
  file only one of them ("No games match." on a lobby with nothing ingested). A source supplies
  `emptyLabel: { empty, filtered }` and the boolean `filtered`; `StreamEmpty` picks the line
  (`views/StreamStates.tsx` → `emptyLine`). The SOURCE owns `filtered` because it is the only party
  that knows what its scope was built from — the catalog's own state carries view/group/items/sort
  and never the section's facets (Arcade: `arcadeNarrows(filters)` off `arcadeFacetSpec.ts`).

Two envelope laws the adapters encode: a flat page reports `total: -1` when the endpoint only
counts on its first page (the adapter carries the first value forward); grouped browsing is
two-phase — heads (`totalGroups` + a page of groups with their first cards) then bands and
"more of one group" by key.

## State

- **URL** — `?view=&group=&items=&sort=` (`state/useCatalogView.ts`). Every change is a
  `history.push` (Back undoes it) and is remembered as the section's default in
  `catalog.view.v1:<section>`; a stale value the source no longer offers falls back. `?view=` is the
  catalog's site-wide — a section must not use it for anything else (Music's artists/albums toggle
  moved to `?tab=`).
- **Section-owned sort** — when the section persists its own sort (Movies' NavBar "Sort by",
  Boardgames' rail, the arcade's filter panel), the adapter sets `currentSort` and the state pins to
  it; picking a sort still writes `?sort=`, and the section's own dispatcher answers with a new
  source. Where the section never had a sort control (Music), the catalog owns it and the section's
  grid must honour it too (`sortRows` / `letterKeyFor` in `musicSource.ts`).
- **Tweaks** — device-scoped, `catalog.tweaks.v1:<section>` (`tweaks/useTweaks.ts`): cover scale
  per view per pointer class, hover (lift / zoom / tilt / dim / none), corners, metadata mode, the
  Directory's show-empty, section-registered extras. Applied ONCE by the host (`data-hover` on the
  results root + the shared hover class on every card) so no view can drift from the setting.
  An extra with `perView: true` (`TweakExtra`) is stored as `<key>:<view>` and read view-first
  (`extras["backdrop:shelf"] ?? extras["backdrop"]`) — every section's backdrop is remembered per
  view this way. An extra with `render: "swatch"` draws as the 4-column swatch grid instead of a Seg.
  The panel closes on the X, on Escape, and on a tap outside (a scrim on phones); on phones it
  sits below the site's fixed top bar (`--content-top-inset`). `rows` removes the standard card rows
  that do not apply where the panel is mounted (a control that doesn't apply is REMOVED, not
  disabled), and `tweaks/PageTweaksTool.tsx` is the ⚙ for a page with no host at all (the Books
  Shelf: cover size, nothing else).
- **Skin** — the section's backdrop + type, `catalog/skin/` (below).
- **Rail state** — the section's filters (`rail/useFacetState.ts`): the URL IS the state — `q`
  (text), `f=token:value` (include), `x=token:value` (exclude), `y=min-max` (years), `r=0–100`
  (rating floor), `my=read,want` (personal flags). A `FacetSpec` (`rail/facetSpec.ts`) declares the
  facets (`token`, `valueType`, `dynamic` long tails, `render` check|swatch|tile, `excludable`,
  `labelOf`, `appliesTo: "groups"`), the text/years/rating/flags, and two loaders. Saved searches
  keep the whole query string per section (`catalog.saved.v1:<section>`).

## The engine (`engine/InfiniteBands.tsx`)

The standalone site's InfiniteScroller, ported: sparse band slots with prefix-summed heights, a
±1200 px mount window from viewport proximity (never a scroll spy), a replaced want-list with a
capped pump (MIN_WANT_AGE 150 ms, MAX_INFLIGHT 4; in-flight accounting per query key so a fetcher
that ignores its abort signal cannot hold slots across a reset), big band mounts under
`startTransition`, `overflow-anchor: none` on the scroll container, no `content-visibility` on
bands, a **resolved scroll root** (`engine/scroller.ts` — `.app-content` on desktop, the window on
phones). Letter and page jumps go through `jumpToUnit`. The flat views ride the same engine (the
Wall's true-aspect rows defeat a constant-columns window).

The Shelves view is the standalone's bookcase in full — planks, spines, reveal AND the wooden carcass
(crown, stiles, the dark walnut recess, cream labels, ghost planks while bands load) — on for every
section under every backdrop; a source sets `shelvesSkin: "plain"` for bare planks on its own surface.

## Laws (R9 S0 — the Long Box `views-perf` catalog is binding)

- **One engine, one strip — TRUE since R9 S3.** Every view of every section rides `InfiniteBands`,
  and every view's seek control is `Components/CatalogPager` (letters under an alphabetical sort,
  page numbers otherwise). `hooks/useGridWindow` and `hooks/usePagedCatalog` — the second engine the
  four Grid overrides ran on — are DELETED; there is nowhere left to re-roll one. A source that wants
  LETTERS on its flat views must offer `letters(sort)` (Books: `/browse/letters`, the flat sibling of
  `group-letters`; Movies: `/API/BrowseLetters`); with only `groupLetters` the strip falls back to
  page numbers on Grid/Wall/List. The strip is an INDEX, so it shows at any list length; page numbers
  only appear once there is more than one page.
- **The want-list pump + abort + `MIN_WANT_AGE` are a set.** Aborts alone cascade (a freed slot
  fetches the next doomed band; the server runs every query to completion); the age gate is what
  makes a scrollbar drag fire ~zero mid-flight fetches.
- **Band mounts and window shifts are `startTransition`s; spy state stays synchronous; band
  renderers are module-level components** (a renderer defined inside a view is a new type every
  render and remounts the stream).
- **`.bx-inf-scrolling { pointer-events: none }` during a scroll burst** — Chrome re-dispatches
  `pointerover` for content moving under a stationary cursor.
- **`overflow-anchor: none` on every scroller hosting a spacer stream** (it does not inherit); the
  scroll root is RESOLVED (`engine/scroller.ts`), never assumed.
- **Image failure = hue placeholder + retry with backoff, then DORMANT with a cooldown
  (`CardImage`: 3 × 1.5 s, then one fresh round every 15 s) — never a fallback `src` swap**, which a
  windowing scheme reads as "loaded" and makes one transient failure permanent.
- **Every `<img>` in a list carries `decoding="async"`** (plus `loading="lazy"` where it is off
  screen) — a card grid, a Shelves plank, the rail's collection tiles. A synchronous decode of a
  long list runs on the main thread between frames.
- **A collection prop is a hoisted constant, never `x ?? []` in the JSX.** `?? []`/`?? {}` inside a
  render is a NEW identity every render: a child effect keyed on it re-runs (`FacetOptions` dropped
  its paged long tail on every parent render this way — `FacetRail`'s `NO_OPTIONS`/`NO_VALUES` are
  the fix) and any `useMemo` under it is dead. This is the render-side twin of the "no fresh object
  literal in JSX" rule the pages already follow for style objects.
- **Abort is not just for bands.** Any fetch a UI can supersede — the rail's typeahead
  (`FacetOptions` + `FacetSpec.loadOptions(…, signal)`), a scroll-to-load page, a count query —
  carries an `AbortSignal`. A sequence guard alone drops the ANSWER while the server still runs
  every superseded query to completion.
- **Paint tiers are feature-detected, never UA-sniffed.** `index.js` sets `html.eng-gecko` on
  Firefox (software WebRender is a deployment target); `catalog-shelves.css`/`catalog-views.css`
  scope the diet (zero-blur book shadow, no static overlays over the scrolled opening, cheap hover
  lift, no cover opacity transition). Chrome keeps the rich look — do not fold the diet into the base
  rules. `(pointer: coarse)` is the touch tier.
- **No `backdrop-filter` over content that is MOVING.** The tweaks card keeps its glass because it
  drops the filter for the 160 ms around a scroll burst (`.twk-panel[data-scrolling]`) — a blur that
  stays on while the page scrolls re-composites the scrolled region every frame. The `eng-gecko`
  tier has no blur at any time.
- **A skin writes tokens on the section ROOT, once — never on a card.** `applySectionSkin` is the
  only writer, it removes what it stops setting, and every rule that reads a `--skin-*` token states
  the site token as its fallback so the unskinned state is theme.css untouched.
- **A section's own card may ride the Grid; nothing else may.** `renderCard(item, view)` is honoured
  by `GridView` alone — the Grid is the critical default detail view, and Movies' MovieCard, the
  Boardgame card, Arcade's GameCard and Music's album/artist tiles keep their exact presentation.
  Every other view keeps the package `Card`, so Wall/List/Extended/Shelves/Newspaper read as one
  site. The card renderer MUST be a module-level component; the full contract is below.
- **Rejected designs stay rejected:** settle-deferred band mounts (bare planks while scrolling),
  velocity-gated deferral, an always-on wheel→strip hijack, `content-visibility` on a JS-windowed
  band, making a section's Grid consume `ViewProps` instead of moving onto the engine (that keeps two
  engines alive forever), and replacing a section's card with the package card (it loses the detail
  the Grid is FOR).

## The Grid's card seam (R9 S3) — `renderCard`

A section keeps its card and gives up its engine. `CatalogSource.renderCard?(item, view)` returns the
section's own card; `gridClass` names the wrap it is laid out in; `gridCell` is the base cover height
in px before the cover-size tweak. What unifies is UNDER the card: `InfiniteBands`, the letter strip,
the band skeletons and the tweaks plumbing.

The contract a section's card signs:

| Tweak | How it reaches the card |
|---|---|
| Cover size | `--cell` on the wrap (`GridView`: `gridCell` × `coverScale`) — the section's CSS sizes its cover box off it; `view.cellH` is the same number as a prop, for a card that measures in JS (Arcade's cover box) |
| Hover (lift / zoom / tilt) | `view.hoverClass` beside `bx-card` on the card root; the effect lands on whatever wears `bx-cover` |
| Hover: dim | the results root's `data-hover` + `bx-cover` on the cover — nothing per-card |
| Rounded corners | `.bx-rounded .bx-cover` — `bx-cover` on the cover is the whole requirement |
| Metadata: minimal | `view.metadata === "minimal"` → the card drops its meta block (Movies: badges + cast + plot; Boardgames: chips + description; Arcade: chips + summary + foot; Music: the sub-line) |

Rules that bite:

- **`renderCard` must return a MODULE-LEVEL component.** A component type created inside the renderer
  is a new type every render and React remounts the whole band (the `BandSlot` memo law). Live
  per-render state (a Seen/Want set, an expansion map) reaches it as flat props through a renderer
  whose identity changes only when one of them changes.
- **`gridClass` must be written `.bx-grid.<class>` in CSS.** The package's own wrap rule (`.bx-grid`,
  a wrapping flex row) has the same specificity, so a bare class wins or loses on file order.
- **The engine's spacers and band skeletons need `grid-column: 1 / -1`** in a section's column grid:
  they are row-breaking blocks, and left in column 1 they shred the layout.
- **`.bx-grid .bx-card { flex-direction: column }` reaches a section card too.** A card that IS the
  row (Arcade's) takes itself back with `.bx-grid .<card>.bx-card { flex-direction: row }`.
- **`.bx-cover > img` fills and CROPS.** A card whose art is letterboxed on purpose (a movie poster,
  BGG box art, arcade box art) overrides it one class deeper, or puts `bx-cover` on the exact-sized
  cover element instead of the box around it (Arcade).

Per section: Movies `MovieCard` / `SimpleMovieCard` (`Pages/Browse/MovieCard.js`; `.bx-grid--movies`
/ `.bx-grid--simple`), Boardgames `BoardGameCard` (`.bx-grid--boardgames`), Music `AlbumCard` /
`ArtistCard` (`Pages/Music/MusicCards.js`; `.music-album-grid` / `.music-artist-grid`), Arcade
`GameCard` (`.arcade-grid`). Each has a `*Tweaks.test.jsx` beside it proving all four levers move it.

`dataVersion` is the seam's companion: a DENSE client list edited in place — Movies' Seen/Want
removal-on-untoggle, a background chunk landing — bumps it, and the stream re-reads its bands while
the window, the measured heights and the scroll position stay exactly where the reader left them.

## The skin (`catalog/skin/`) — R9 S5

Nine backdrops and a type theme, per section, from the one ⚙ panel. Lifted out of Books, which had
the only copy.

- **A section registers a `SectionSkin`** (`skin.ts` → `registerSectionSkin(section, skin)`):
  `backdrops` (nine), `defaults` per family, `types`, `perView`, and two escape hatches —
  `tokenPrefix` (Books also gets `--books-*`, the names five of its stylesheets read) and
  `paintHost: false` (Books paints `.books-section` itself, a wider root than the results box).
  `sectionSkins.ts` registers Movies, TV, Boardgames, Music, Arcade and Photos; `Pages/Books/
  booksTheme.ts` registers `books` / `books-novels` / `books-kids` and keeps its `@fontsource`
  imports in the Books chunk.
- **The first swatch of every set is the section's own surface** (`siteDefault`) and writes NO
  tokens: theme.css keeps the floor, so a device that has never opened the panel renders exactly
  what it rendered before the skin existed, and "no backdrop" is a real, selectable choice. The
  other eight are four light and four dark, drawn from the section's `data-feature` hue.
- **`data-theme` is the authority.** A backdrop belongs to a family; a remembered one from the other
  family falls back to that family's default (`resolveBackdrop`), so a dark page never opens inside
  a light site. The panel still shows all nine — an out-of-family swatch is dimmed but LIVE:
  `crossFamilyPick` reports it and `CatalogHost` answers with `requestSiteTheme(family)`
  (`hooks/useTheme.js` — a REQUEST; that hook stays the one writer of `data-theme` and of the
  stored value). No swatch is an inert control.
- **Where the tokens land.** `applySectionSkin(root, …)` writes `--skin-bg/card/ink/sub/line/chrome/
  scene/display/header/mono/tracking/weight` ONCE on the section root — `CatalogHost` on `.bx-host`,
  `BooksPage` on `.books-section` — and REMOVES what it stops setting. `skin.css` binds them to the
  package's local aliases at `.bx-host[data-catalog-skin]` (one class deeper than `.bx-host`'s own
  declarations, so it wins outright rather than on file order), each with the site token as its
  fallback. `data-skin-paint` — set only when the section asked the host to paint AND the backdrop
  is not the site's own — is what turns the results box into a painted surface; nothing is written
  per card.
- **The sheet takes the skin too.** `sectionSkinStyle` / `useSectionSkinStyle` / `useRouteSkinStyle`
  return the same set plus the site surface repoints (`--card-surface`, `--text-primary`, …) for an
  antd modal's wrap, which renders OUTSIDE the section root. It rides `styles={{ wrapper }}`, never
  `wrapProps.style` (that one REPLACES the wrap's inline style and takes its `zIndex` with it).
  Worn by the movie sheet, the Books item/series sheets, the arcade game sheet, the photo lightbox
  and — since R9 S6 — the boardgame sheet, whose hard-coded light surface and light-surface ink were
  tokenised for it (`--bgm-*` at the top of `BoardGameModal.css`; the category hues survive as
  color-mixes into the LIVE surface). The MUSIC album sheet is still NOT wired: it leaves antd's own
  near-white container in place, so handing it a dark backdrop's `--text-primary` paints light text
  on a white card (the bug `sheet-modal--themed` warns about in `Components/SheetModal.css`).
  Tokenise first, wire after.
- **Perf.** `.twk-panel` keeps its glass but never blurs over MOVING content: `data-scrolling` is set
  from one capturing `onAnyScroll` listener and cleared 160 ms after the last scroll, and the CSS
  swaps `backdrop-filter` for an opaque `--bg` for exactly that window. `eng-gecko` (software
  WebRender) gets no blur and a zero-spread shadow at all times.

## The rail family (`catalog/rail/`)

- **`FacetRail`** — one body, two skins: `rail` (a desktop sider column: the section mounts it in
  the site sider, e.g. `BooksSiderRail` through `BooksNavContent`) and `sheet` (a full-page phone
  sheet the section raises behind a Filters pill; z-index 1350, above the top bar). Sections:
  facets in spec order (`RailSection` collapsibles, `FacetOptions` with include/exclude controls and
  the searched, paged long tails via `useFacetOptions`), the **fixed-scale ranges** (`spec.ranges`,
  `RangeFacetDef`: two thumbs over declared stops — the Boardgames Age 3…18+ / Play time / Weight —
  URL `<token>=min-max`, a thumb at either end = an open side, BOTH thumbs filter; drawn right under
  the facet named by `after`, `StopsRangeFacet`), then Date range, Rating, My lists (`RangeFacets`),
  then the saved searches. A count badge on the head shows the active filters. The phone sheet
  binds the page tokens through `bx-rail-surface` (without it the sheet is transparent).
- **`SmartSearch`** — the rail's input: a text "Search" row first, then facet suggestions with type
  labels and counts; `token:` prefixes scope the suggestions; arrows/Enter/Escape.
- **`ActiveChips`** — `search` / `<One>` / `not <one>` / `years` / `rating` / flag chips over the
  results (`CatalogHost.beforeResults`), Clear all, and Save (a saved search).
- **`SectionIndexRail`/`SectionIndexTabs`** — the section's own index (Explore / Browse / …) in
  the sider on desktop and as tabs on phones.
- **Per-section pieces** (R9 S2): `FilterPill` (the phone's bar tool), `useRailSheet` (the sheet's
  open state — closes on URL change / desktop, and answers the phone top bar's search button through
  `requestSectionSearch`), `RailChips` (the chips row + save prompt over the results). A section
  mounts its rail in the sider through its NavContent (`BooksSiderRail`, `MoviesSiderRail`) and the
  sheet + SmartSearch-in-the-bar from its page — both read the same URL, nothing crosses through props.
- **How a section wires all that up is itself shared** (the S2 review pass) — a section writes its
  spec and its count, nothing else:
  - `useSectionRail(section, spec, { entityParams, facetsEnabled, grouped })` → `SectionRailState`
    (`state` / `actions` / `activeCount` / `grouped` / `facets` / `saved` / `saveCurrent`): the URL
    state, the spec's option lists, the grouped reading and the section's saved-search store, in
    ONE call. Both trees call it — they still agree through the URL, never through props.
  - `<SectionSiderRail rail={…} total={…} loading={…} note={…} />` is the sider column (`note`
    replaces the controls where a view has no filters — the Books Directory).
  - `sectionRailSurfaces(rail, sheet, { total, placeholder, chipsClassName })` → `{ pill, chips,
    surfaces }` for the page. NOT a hook (it calls none) — pages return early above it; and the
    sheet's `useRailSheet` stays the PAGE's, because only the tree that renders the sheet may answer
    `requestSectionSearch`.
  - `useResultCount(key, request, enabled)` / `useCountQuery` (`rail/useResultCount.ts`) is the head
    line's count: one 1-row page per state, five minutes, `totalCount` or `total` off the envelope
    (-1 = the endpoint does not count). One query key means the sider and the sheet ask once.
  - A section whose rail and page read the same CACHED list uses `hooks/useSharedCachedResource`
    (Boardgames, Music) — see the site-frontend skill for when that beats `useCachedResource`.
- **Specs**: `Pages/Books/booksFacetSpec.ts`, `novelsFacetSpec.ts`, `Pages/Browse/moviesFacetSpec.ts`
  (Type · Genre · MPA as one pill row · Years as the bare two-thumb range · Franchise · People
  (typeahead via `/API/BrowsePeople`) · mood/subgenre/era/theme/setting · Seen/Want/Rated flags;
  counts from `/API/BrowseFacets`, keyed on the Type scope).
- **`clientFacets.ts`** — the client twin of the server's `BrowseFilter` for sections whose list is
  already in the browser (Boardgames, Music, Arcade): `applyFacetState(items, state, extractors)`
  (ALL included values per facet by default, `anyOf` opts a facet into ANY; excludes NOT; facets AND;
  `q` substring; year range; the section's flag tests) and `countClientFacets(items, extractors)`
  (the rail's option rows, most-common first). The S2c sections mount their specs over it.
- Never host the filters inside NavBar's phone drawer: it closes on every `location.search` change.

## The Explore kit (`catalog/explore/`) — R9 S7: every section has one

`ExploreTab` renders a section's landing from data the SECTION fetches (`data / loading / error` —
the section owns the query and its keys): a `HeroSpotlight` (a `detail()` hook lets a section
headline a group instead of an item), `CardRow`s, a `CoverWall`, `CardGrid`s, `RowHead`s with a
"More" link, `ScoreBadge`s. `mapExplore.ts` maps a section's rows onto `CardItem`s;
`cards/placeholder.ts` paints hue placeholders for cards without art.

### The composition contract

Books' Explore comes down the wire already composed (the host answers one `/explore` payload,
`mapExplore` maps it). **Every other section composes its landing IN THE BROWSER** out of endpoints
it already served — a rail is a named query plus a mapper, and the composer is a PURE function so
the rails and their links can be asserted without a network. Three pieces make that a pattern
rather than five copies:

- **`composeExplore.ts`** — `exploreRail(key, title, kind, items, more?)` (an empty rail is
  DROPPED, never drawn as an empty shelf), `exploreResponse(spotlight, rails, seed?)`, and
  `groupCard({ kind, key, title, count, … })` for a card that stands for a FACET rather than a row.
  A group card carries `groupKey` + `count`, which is what `groupOf` reads when the tab hands it to
  `onOpenGroup`.
- **`ExploreTab`'s `groupKinds`** — which card kinds are GROUPS in this section's vocabulary.
  The default is the host's (`series` + `artist`); the SPA-composed sections pass
  `FACET_GROUP_KINDS` (`franchise` `system` `person` `artist` `channel`). Movies MUST: its `series`
  cards are TV shows — titles — and the host default would open one as a browse. It has to be a
  stable identity (a fresh `new Set()` in the JSX is a new prop every render).
- **`rail/facetUrl.ts` → `facetHref(pathname, facets, extra?)`** — a browse URL that is nothing but
  facets, which is exactly the state the section's rail would have produced by hand, so the chip is
  present the moment the page opens. **Every group card routes through `onOpenGroup` to
  `<section>?f=token:value`** — the rail URL contract from S2.

### Cheapness is the design

- Every rail fetch is its own React Query with a real `staleTime`; the page never refetches because
  a param moved, and returning to the tab redraws from cache.
- **The expensive queries wait.** `useNearViewport.ts` → `useExploreDepth()` is false until the
  reader actually moves (an idle fallback flips it after 2.5 s so a very tall window still fills
  in), and a section hangs its TAIL rails' `enabled` on it. What waits, per section, is named below.
- **Rails below the fold do not mount.** `LazyRail` reserves the rail's height and mounts nothing
  until it is approached — one `IntersectionObserver`, disconnected on the first hit. `ExploreTab`
  renders the first `eagerRails` (2) directly; the reserve is the same box the mounted rail fills,
  so revealing one never moves the page under the reader.
- The engine's laws still apply one floor up: `CardImage` for every cover (`decoding="async"`, hue
  placeholder + backoff + dormant cooldown, never a fallback `src` swap), module-level renderers,
  no per-item listeners.

### The rails, per section

| Section | Route | Rails (name → source) | Group cards | Waits for depth |
|---|---|---|---|---|
| Movies/TV | `/movies/explore` | spotlight + **Something else entirely** → `/API/Browse?sort=random&seed=` (one seeded page feeds both) · **Keep watching** → `/API/ContinueWatching` · **On TV right now** → the `useChannelLineup` the homepage rail builds · **Picked for you** → `/API/Recommendations` · **Just added to the library** → `/API/Browse?sort=added` · **Whole runs to binge** → `/API/BrowseGroups?groupBy=franchise` · **The ‹X› run, in order** → `/API/GetFranchiseRail` anchored on the spotlight | franchise → `/?f=franchise:‹v›`; a person chip → `/?f=person:‹name›` | the franchise group index + the franchise run |
| Music | `/music/explore` | spotlight + **Reach for something** → the cached shelf, seeded shuffle · **Your favourites** → `/API/Music/Playlist/Mine` (the Favorites list's album ids, resolved in the shelf) · **Latest on the shelf** → the cached shelf, descending id · **Artists to sit with** → the cached shelf's artists | artist → `/music?f=artist:‹id›` | the playlists read |
| Arcade | `/arcade/explore` | **Recently played** → `/API/Arcade/RecentlyPlayed` (**moved from the lobby**) · **Live rooms** → `/API/Arcade/Rooms` · **Where you last earned something** → `/API/Arcade/Trophies/Mine` · **Pick a console** → `/API/Arcade/Filters` · spotlight + **Best on the shelf** → `/API/Arcade/Games?sort=rating` · **Spin the shelf: ‹System›** → one console picked by the seed | system → `/arcade?f=system:‹v›` | the trophy room + the spin |
| Photos | `/photos/explore` | spotlight (the anniversary, else the newest) · **On this day — ‹date›** → `/API/Photos/OnThisDay` · **Latest in the album** → `/API/Photos/Browse` · **The people in the album** → the people list `PhotosPage` already holds | person → `/photos/browse?f=person:‹id›` | the recent reel |
| Boardgames | `/boardgames/explore` | spotlight + **Best on the shelf** · **Newest on the shelf** · **Designers on the shelf** · **Pull one off the shelf** — ALL four off `useBoardgamesCatalog`, the copy the browse already holds | designer → `/boardgames?f=designer:‹name›` | nothing — the tab makes no request at all |
| Books | `/books/explore` | the host's composed payload (`/API/Books/explore`) | series → the series modal | — |
| TV | — | **deliberately none.** `/channels` IS the EPG: the grid guide already draws every channel, grouped by category, with what is on NOW, and its detail panel carries the ♥. An Explore of "now + favourites" would be a second, worse copy of that one page. The lineup instead surfaces where it is NOT already visible — the **On TV right now** rail on the Movies Explore. Revisit if TV grows a second axis (per-channel "up next" shelves, playlists as cards). | — | — |

Two rails carry a stated honesty note rather than a claim the data cannot support: **Music and
Boardgames have no "added" stamp** (`MusicAlbum` has `Year`, a boardgame row has `yearPublished`,
and neither records when it landed), so "Latest / Newest on the shelf" orders by descending id —
the identity column IS the ingest order — and the rail is labelled for what that actually means.

**New endpoints are the exception, and there are three.** Every other rail rides something that
already existed. Each is read-only, capped, and gated exactly as its section's browse is:
`/API/ContinueWatching` (a resume position had no read route at all; `MoviePlaybackProgress` hangs
off a `Playable`, so an episode resolves to its SERIES card), `/API/Recommendations` (the
`TitleRecommendation` rows `RecommendationMaintenanceService` keeps fresh), and
`/API/Photos/OnThisDay` (the browse narrows by year, and by month WITHIN a year, never by a day
across years). The first two are per-viewer, so they are never cached and never warmed.
`/API/Music/Playlist/Mine` widened its per-playlist `albumIds` prefix from 4 to 12 — the same ids,
a longer prefix.

## The change-driven cache warmer (`Web/CatalogWarmupService`) — R9 S7

The Long Box `views-perf` law the pods were missing. Every `CheckInterval` (5 min) the hosted
service reads a cheap **`CatalogFingerprint`** — the counts of visible movies / series / misc /
insights / viewings plus the max `UploadedDate` and `GeneratedUtc` stamps, all indexed COUNTs and
MAXes, quarantined rows excluded so it sees what the browse sees. When it MOVES (or the backstop
TTL elapses) it rebuilds the movie browse's light group indexes and its facet counts into the same
`IMemoryCache` a request would fill.

- **Gating is pure and tested** (`CatalogWarmupPlan.Decide`): first pass · changed · backstop TTL
  (4 h) · and never inside `MinInterval` (2 min). That floor matters because viewing counts are
  part of the fingerprint — without it, ticking through the Rate page would re-warm the whole index
  once per row. A change inside the floor is not lost; it warms at the next check. **Never a timer
  as the trigger, and never a request.**
- **Bounded, observable, resumable**: one target per step with a pause between, its own scope and
  `DbContext`, every pass logging its REASON and every target logging what it built and how long it
  took (`catalog-warmup: groups:Movies:genre → 412 groups in 830 ms (4/8)`). State is the cache
  itself, so a pass killed halfway leaves what it finished warm and the next pass redoes the rest.
  A failure is logged and dropped — a cold cache is slow, not broken. READ-ONLY throughout; **off
  in Development**, because the dev connection IS the live shared database.
- **What it warms**: `BrowseFilter.CountAsync` for the `Movies` and `Series` type scopes, and
  `BrowseGroups.BuildIndexAsync` for genre / decade / franchise over `Movies`, `Series` and both.
  Misc-inclusive scopes are deliberately absent (their index needs the misc CARD projection, which
  lives on the controller, and misc is a small in-memory list that costs nothing cold).
- **`Web/BrowseCacheKeys.cs` is what makes a warm reachable.** `Web/CatalogQueries`' base queries
  depend on exactly ONE viewer fact, the age restriction, so the group index and the facet counts
  are identical for every viewer at that age — EXCEPT when the filter reads the caller's own lists
  (`my=`), the only user-dependent part of `BrowseFilter`. The key now carries the user id only in
  that case. Before this, every signed-in viewer built their own private copy of an identical index
  and no warm could ever have been hit.

## Sources (`catalog/sources/`)

| Section | Adapter | Scope | Flat | Groups | Directory |
|---|---|---|---|---|---|
| Movies (dense) | `createMoviesListSource` over `clientSource.ts` | Seen · Want to watch · the back-nav restore · a one-shot browse — the rows the page already holds. Flat views only (an id list has no server grouping); the list is filled in bounded chunks and a Seen/Want untoggle edits the array + bumps `dataVersion` | slices | — | — |
| Movies/TV | `moviesSource.ts` | the rail URL (`q/f/x/y/my`) → `/API/Browse` via `useMovieSearch.facetSearch` (`moviesFacetSpec.ts` maps the state to `BrowseFilterQuery`; pre-S2 `?mode=&value=` links are rewritten once on entry) | the `/API/Browse` envelope | `/API/BrowseGroups` genre/decade/franchise under the same filter | franchises |
| Boardgames | `boardgamesSource.ts` over `clientSource.ts` | the rail URL (`q/f/x/y` + `a/t/w` ranges) applied IN MEMORY (`clientFacets` via `boardgamesFacetSpec.ts`) over the shared React-Query catalog (`useBoardgamesCatalog`); pre-S2c `?players=&age=&time=&mode=title` links are rewritten once on entry | slices | publisher/family/designer/category/mechanic (`/API/Boardgames/Facets`), decade, players — a header click scopes + drills (`DRILL_NEXT_GROUP`) | publishers |
| Music | `musicSource.ts` over `clientSource.ts` | the rail URL (`q/f/x/y`) applied IN MEMORY over the shelf the URL names (`f=kind:` is a SCOPE — the shelf is fetched, never filtered down; `musicFacetSpec.ts` over the shared `useMusicShelf` React-Query resource; `f=artist:`/`f=tag:`/`y=` filter it, `q` also drives the server song search); pre-S2c `?kind=`/`?tab=` links rewritten once | slices (the catalog sorts) | artist/decade/kind/letter; artists by decade/letter | artists → albums |
| Arcade | `arcadeSource.ts` | the rail URL (`q/f/x`) mapped onto `/API/Arcade/Games`' own params by `arcadeFacetSpec.ts` (`f=system:` repeatable → csv — the console carousel IS that facet and the rail draws no System section (`hidden`); `x=region:` → `hideRegions` (exclude-only, `includable:false`); `players/genre/variant/ra` single-valued); pre-S2c `?system=&players=…` links rewritten once | `/API/Arcade/Games` (absolute skip) | `/API/Arcade/GameGroups` system/genre/decade — a header click adds the facet | systems |
| Photos | `photosSource.ts` | the reel (the Timeline shelf + hidden toggle) narrowed by the rail URL (`q/f/x/y`) on `/photos/browse` — `photosFacetSpec.ts` maps it onto `PhotoBrowseFilterQuery` (album/person/kind/camera/years/q, `ex*` twins) which rides Browse, BrowseGroups and the Directory; option lists from `/API/Photos/Facets` (per hidden toggle). The Timeline root and the Gallery subsection stay outside it | `/API/Photos/Browse` | `/API/Photos/BrowseGroups` year/month/album/folder | top-level folders |
| Books | `booksSource.ts` over `booksOData.ts` | the rail URL (`q/f/x/y/r/my`) | `/API/Books/odata/catalog` | `/API/Books/browse/groups` collection/series/publisher/decade/franchise (+ Items one-per-series) | collection folders (`dir=`) |
| Novels | `novelsSource.ts` | the Novels rail (include-only facets; adult-romance excluded by default) | `/API/Books/novels` | — | — |
| Kids | `kidsSource.ts` | one bounded `/API/Books/kids/browse` load | client slices, best/alpha | series (client) | — |

`clientSource.ts` is the in-memory source: bands are slices, heads are walks, letters are buckets,
all instant and abort-free — for sections that already ship their whole catalog to the browser.

## Adopting the package (a section's checklist)

1. Write `sources/<section>Source.ts`: map rows → `CardItem` (kind-scoped key, poster/thumb URLs by
   the section's own rule, a real aspect, badges), decide the scope key, offer only what the data
   supports (`supports`, `groups`, `sorts`, `directory`), open through the section's URL-driven modal.
2. Mount `<CatalogHost section="<name>" source={source} />` where the grid lives. Keep the section's
   OWN card by giving the source `renderCard` + `gridClass` + `gridCell` (the seam above) — never a
   `grid` override running its own windowing, which is the second engine R9 S3 deleted. `overrides`
   survives only for a transient non-stream surface (Movies parks its first-paint skeleton there
   while a dense list loads).
3. If the section has a landing that keys on "no params", exclude the catalog's (`CATALOG_PARAM_KEYS`).
4. Tests: an envelope test per adapter on recorded fixtures (`*.test.ts` beside it).
5. Filters (optional): write a `FacetSpec`, mount `FacetRail` in the sider (desktop) and behind a
   Filters pill as a sheet (phone), pass `useFacetState`'s query into the source's scope key, put
   `ActiveChips` in `beforeResults`.
6. Explore: a tab row in `catalog/bar/sections.ts`, a route, and a PURE `compose<Section>Explore`
   over `composeExplore.ts` — named queries the section already serves, `groupCard` for a facet,
   `facetHref` for where it leads, `useExploreDepth` on the expensive ones. See "The Explore kit".
7. Skin: `registerSectionSkin("<section>", …)` with nine backdrops (a `siteDefault` first, then four
   light + four dark) — the host does the rest. See "The skin" above.
8. Smoke: every view on desktop + phone, a card open keeps `?view=`, Back closes; the modal and the
   tweaks panel must not slide under the fixed top bar on phones. A section's detail modal is the
   site's full-page sheet at EVERY size (`Components/SheetModal.css` + the section's own stylesheet
   repeating its sheet block unconditionally — `MovieModal.css`, `GameModal.css`,
   `BoardGameModal.css`, `books-modal.css`), and it opens at `SHEET_Z` (1500,
   `Components/sheetModal.js` — the one place the number lives; `SHEET_STACK_Z` = 1600 for a dialog
   a sheet raises without closing itself). Card mode is for confirm/info prompts only.

## The admin shell (`src/ui/src/admin/`) — R9 S6

Not part of the catalog package, but the same idea one floor down: every section's operator tools
wear ONE shell, at `/<section>/admin?tab=`, reached from the bar's Admin tab
(`catalog/bar/sections.ts`). It was lifted out of the Books admin, which is now just its biggest
adopter.

| Piece | What it is |
|---|---|
| `AdminShell.tsx` | The head + the tab row. The tab is in the URL (`?tab=`), so "the Users tab" is a real link and an Overview row can point at it. Only the ACTIVE tab's body mounts — ten operator tabs mounted at once is ten queries nobody asked for — inside a `Suspense` so a tab can be a lazy chunk. `when: false` REMOVES a tab (the Long Box rule the bar follows); `allowed={false}` draws the refusal plate. `readAdminTab` / `visibleTabs` / `adminTabHref` are the pure helpers. |
| `AdminOverview.tsx` | The report primitives: `AdminStats` (tiles), `NeedsAttention` (the rows), `AdminCard` (a plain block). |
| `jobs.ts` | The job vocabulary — `JobStatus`/`JobStart`, `isRunning`, `jobPercent`, `AdminApiError`, and the `JobApi` adapter a section supplies. |
| `useJobStatus.ts` | One job kind observed: the SSE feed while it runs, a 2 s poll behind it, `apply()` for the status a start/stop call already answered with. |
| `JobCard.tsx` | One job as a card: state, progress, last line, error, Start / Stop. A 409 is a warning, not a failure. |
| `driveBatches.ts` | The caller-driven chunk loop for a whole paged list: bounded per call, progress reported, **resumable** (`from`), a no-progress break, a step ceiling. The house rule for bulk work, on the client side. |
| `aliases.js` | Where the old operator routes went — one table, rendered as `<Redirect>`s by App and by PhotosPage, asserted by a test. |
| `admin.css` | The shell's surfaces (`.admin-page`, `.adm-*`). Books' four port names come from `.books-section`, so the shell states the site tokens only under `:not(.books-admin)`; `--bg` is `--content-bg`, never `--card-surface`, because the arcade's dark card surface is a GRADIENT and `color-mix()` over a gradient is invalid. |

### The Overview contract

**An Overview is a REPORT, not a dashboard.** Three rules, and they are the whole of it:

1. **Existing endpoints only.** No section grew an API for its Overview. Movies reads
   `/API/GetTotalMovieCount`, `/API/Admin/IngestReview/List?scope=batch|gaps`,
   `/API/Admin/IngestReview/SyncCandidates`, `/API/Admin/Users`, `/API/Admin/PatchedArtifacts`; TV
   reads `/API/Channel/Admin/List` + `/API/Channel/Playlist/Mine`; Arcade `/API/Arcade/Filters`,
   `/Rooms`, `/HostStatus`; Photos the single `/API/Photos/Status`; Boardgames the same
   `/odata/Boardgames` its browse reads; Music the shelf + `/API/Music/Capabilities`.
2. **A count with no source says so.** `count: null` renders as `—` and does not link. Each page
   also carries a "what this page cannot report" card naming the numbers that genuinely have no
   endpoint (a channel's pool size, arcade box-art coverage, photo scanning/dating, every CLI job) —
   a report with a stated gap beats a report that guesses.
3. **A needs-attention row names the tab that fixes it and links to it.** Zero rows are not drawn;
   a `null` row IS drawn (an unknown is worth saying); `always` pins a standing fact (the arcade
   host is degraded, the curation store is unconfigured). Nothing is rendered inert.

### The sections

| Route | Tabs | Aliased from |
|---|---|---|
| `/movies/admin` | Overview · Review ingest · Insert · Batch insert · Users (admins only) · Rate | `/review-ingest`, `/insert`, `/batchinsert` (and `NavBar/AdminModal`, deleted — its body is the Users tab) |
| `/channels/admin` | Overview · Channels · Playlists | — (`/tv/admin` is impossible: `/tv/:channelId?` is the screening room) |
| `/arcade/admin` | Overview · Game config · Saves vault · RetroAchievements | — |
| `/photos/admin` | Overview · Review · Dupes · Tag queue · Google | `/photos/review`, `/photos/dupes`, `/photos/tag`, `/photos/google` |
| `/boardgames/admin` | Overview · Batch insert | `/boardgames/batchinsert` |
| `/music/admin` | Overview | — |
| `/books/admin` | Overview · Library · ComicVine · Series · Collections · Normalization · Kids · Duplicates · Config · System | — |

`/rate` is deliberately NOT aliased: it is a member surface (the sider's "Rate Movies" row) that the
movie admin also offers as a tab.

**Gating is a courtesy.** The route re-checks the same flag the bar uses (`isAdmin`, or
`isAdmin || canEditMovies` where an editor is enough) and draws a plate for anyone else — but every
endpoint behind every tab is independently gated on the server, and that is the gate.

**A dialog-backed tool stays a dialog.** The first pass WRAPPED what existed rather than rewriting
it: where the tool is a modal or a drawer today (the channel editor, the playlist manager, the
per-game arcade config, the saves vault, the trophy hub), its tab is a card that opens it. The tab
adds a URL and a place; it does not change what the tool does.

## Verification

- `npm run typecheck`, `npm test`, `npm run build` (the same gate the Docker UI image runs).
- Headless smoke (Playwright from the test-roms skill folder): each section's seven views on the
  desktop and phone profiles, zero page/console errors, a card open from the Wall keeps the view.
  Password-gated sections (Arcade, Photos, Books) need a password session and are checked by hand —
  Books has a full Playwright suite on a hand-captured session (`.claude/skills/books/e2e/`).
