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
  `letters(sort)`, `groupLetters(groupBy, sort)`, `onOpen(item)`, `onOpenGroup(group, groupBy)`.

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
  (`extras["backdrop:shelf"] ?? extras["backdrop"]`) — the Books backdrop is remembered per view this
  way. The panel closes on the X, on Escape, and on a tap outside (a scrim on phones); on phones it
  sits below the site's fixed top bar (`--content-top-inset`).
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

- **One engine, one strip.** Every view rides `InfiniteBands`; every view's seek control is
  `Components/CatalogPager` (letters under an alphabetical sort, page numbers otherwise). A section
  whose Grid still runs its own windowing (`useGridWindow`/`usePagedCatalog`) is a migration debt,
  not a second engine — R9 S3 retires it. A source that wants LETTERS on its flat views must offer
  `letters(sort)` (Books: `/browse/letters`, the flat sibling of `group-letters`; Movies:
  `/API/BrowseLetters`); with only `groupLetters` the strip falls back to page numbers on Grid/Wall/List.
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
- **Paint tiers are feature-detected, never UA-sniffed.** `index.js` sets `html.eng-gecko` on
  Firefox (software WebRender is a deployment target); `catalog-shelves.css`/`catalog-views.css`
  scope the diet (zero-blur book shadow, no static overlays over the scrolled opening, cheap hover
  lift, no cover opacity transition). Chrome keeps the rich look — do not fold the diet into the base
  rules. `(pointer: coarse)` is the touch tier.
- **Rejected designs stay rejected:** settle-deferred band mounts (bare planks while scrolling),
  velocity-gated deferral, an always-on wheel→strip hijack, `content-visibility` on a JS-windowed band.

## The rail family (`catalog/rail/`)

- **`FacetRail`** — one body, two skins: `rail` (a desktop sider column: the section mounts it in
  the site sider, e.g. `BooksSiderRail` through `BooksNavContent`) and `sheet` (a full-page phone
  sheet the section raises behind a Filters pill; z-index 1350, above the top bar). Sections:
  facets in spec order (`RailSection` collapsibles, `FacetOptions` with include/exclude controls and
  the searched, paged long tails via `useFacetOptions`), then Date range, Rating, My lists
  (`RangeFacets`), then the saved searches. A count badge on the head shows the active filters.
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
- **Specs**: `Pages/Books/booksFacetSpec.ts`, `novelsFacetSpec.ts`, `Pages/Browse/moviesFacetSpec.ts`
  (Type · Genre · MPA as one pill row · Years as the bare two-thumb range · Franchise · People
  (typeahead via `/API/BrowsePeople`) · mood/subgenre/era/theme/setting · Seen/Want/Rated flags;
  counts from `/API/BrowseFacets`, keyed on the Type scope).
- Never host the filters inside NavBar's phone drawer: it closes on every `location.search` change.

## The Explore kit (`catalog/explore/`)

`ExploreTab` renders a section's landing from data the SECTION fetches (`data / loading / error` —
the section owns the query and its keys): a `HeroSpotlight` (a `detail()` hook lets a section
headline a group instead of an item), `CardRow`s, a `CoverWall`, `CardGrid`s, `RowHead`s with a
"More" link, `ScoreBadge`s. `mapExplore.ts` maps a section's rows onto `CardItem`s;
`cards/placeholder.ts` paints hue placeholders for cards without art.

## Sources (`catalog/sources/`)

| Section | Adapter | Scope | Flat | Groups | Directory |
|---|---|---|---|---|---|
| Movies/TV | `moviesSource.ts` | the rail URL (`q/f/x/y/my`) → `/API/Browse` via `useMovieSearch.facetSearch` (`moviesFacetSpec.ts` maps the state to `BrowseFilterQuery`; pre-S2 `?mode=&value=` links are rewritten once on entry) | the `/API/Browse` envelope | `/API/BrowseGroups` genre/decade/franchise under the same filter | franchises |
| Boardgames | `boardgamesSource.ts` over `clientSource.ts` | the page's filtered+sorted list | slices | publisher/family/designer/category/mechanic (`/API/Boardgames/Facets`), decade, players | publishers |
| Music | `musicSource.ts` over `clientSource.ts` | the shelf's cached artists/albums, per tab | slices (the catalog sorts) | artist/decade/kind/letter; artists by decade/letter | artists → albums |
| Arcade | `arcadeSource.ts` | the lobby's filters | `/API/Arcade/Games` (absolute skip) | `/API/Arcade/GameGroups` system/genre/decade | systems |
| Photos | `photosSource.ts` | the timeline (+ hidden toggle) | `/API/Photos/Browse` | `/API/Photos/BrowseGroups` year/month/album/folder | top-level folders |
| Books | `booksSource.ts` over `booksOData.ts` | the rail URL (`q/f/x/y/r/my`) | `/API/Books/odata/catalog` | `/API/Books/browse/groups` collection/series/publisher/decade/franchise (+ Items one-per-series) | collection folders (`dir=`) |
| Novels | `novelsSource.ts` | the Novels rail (include-only facets; adult-romance excluded by default) | `/API/Books/novels` | — | — |
| Kids | `kidsSource.ts` | one bounded `/API/Books/kids/browse` load | client slices, best/alpha | series (client) | — |

`clientSource.ts` is the in-memory source: bands are slices, heads are walks, letters are buckets,
all instant and abort-free — for sections that already ship their whole catalog to the browser.

## Adopting the package (a section's checklist)

1. Write `sources/<section>Source.ts`: map rows → `CardItem` (kind-scoped key, poster/thumb URLs by
   the section's own rule, a real aspect, badges), decide the scope key, offer only what the data
   supports (`supports`, `groups`, `sorts`, `directory`), open through the section's URL-driven modal.
2. Mount `<CatalogHost section="<name>" source={source} overrides={{ grid: <the existing grid/> }} />`
   where the grid lives; the existing renderer stays the `grid` view untouched. Gate the section's own
   pump/letters on "the grid is the view on screen" (`resolveViewState(...).view === "grid"`).
3. If the section has a landing that keys on "no params", exclude the catalog's (`CATALOG_PARAM_KEYS`).
4. Tests: an envelope test per adapter on recorded fixtures (`*.test.ts` beside it).
5. Filters (optional): write a `FacetSpec`, mount `FacetRail` in the sider (desktop) and behind a
   Filters pill as a sheet (phone), pass `useFacetState`'s query into the source's scope key, put
   `ActiveChips` in `beforeResults`.
6. Explore (optional): a section query + `ExploreTab` with a `mapExplore` for its rows.
7. Skin (optional): section-wide tokens written on the section root from the tweaks store; a
   per-view extra (`perView`) when a look should follow the view.
8. Smoke: every view on desktop + phone, a card open keeps `?view=`, Back closes; the modal and the
   tweaks panel must not slide under the fixed top bar on phones (site modals use `zIndex={1500}`).

## Verification

- `npm run typecheck`, `npm test`, `npm run build` (the same gate the Docker UI image runs).
- Headless smoke (Playwright from the test-roms skill folder): each section's seven views on the
  desktop and phone profiles, zero page/console errors, a card open from the Wall keeps the view.
  Password-gated sections (Arcade, Photos, Books) need a password session and are checked by hand —
  Books has a full Playwright suite on a hand-captured session (`.claude/skills/books/e2e/`).
