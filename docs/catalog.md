# The catalog package — every view, for every section

`src/ui/src/catalog/` is the one browse surface the site's sections share: seven views (Grid, Wall,
List, Extended, Shelves, Newspaper, Directory), one band engine, one tweaks panel, one URL contract.
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

## The engine (`engine/InfiniteBands.tsx`)

The standalone site's InfiniteScroller, ported: sparse band slots with prefix-summed heights, a
±1200 px mount window from viewport proximity (never a scroll spy), a replaced want-list with a
capped pump (MIN_WANT_AGE 150 ms, MAX_INFLIGHT 4; in-flight accounting per query key so a fetcher
that ignores its abort signal cannot hold slots across a reset), big band mounts under
`startTransition`, `overflow-anchor: none` on the scroll container, no `content-visibility` on
bands, a **resolved scroll root** (`engine/scroller.ts` — `.app-content` on desktop, the window on
phones). Letter and page jumps go through `jumpToUnit`. The flat views ride the same engine (the
Wall's true-aspect rows defeat a constant-columns window).

## Sources (`catalog/sources/`)

| Section | Adapter | Scope | Flat | Groups | Directory |
|---|---|---|---|---|---|
| Movies/TV | `moviesSource.ts` | the `useMovieSearch` URL (endpoint = mode) | the `Browse*` envelopes | `/API/BrowseGroups` genre/decade/franchise/letter | franchises |
| Boardgames | `boardgamesSource.ts` over `clientSource.ts` | the page's filtered+sorted list | slices | publisher/family/designer/category/mechanic (`/API/Boardgames/Facets`), decade, players | publishers |
| Music | `musicSource.ts` over `clientSource.ts` | the shelf's cached artists/albums, per tab | slices (the catalog sorts) | artist/decade/kind/letter; artists by decade/letter | artists → albums |
| Arcade | `arcadeSource.ts` | the lobby's filters | `/API/Arcade/Games` (absolute skip) | `/API/Arcade/GameGroups` system/genre/decade | systems |
| Photos | `photosSource.ts` | the timeline (+ hidden toggle) | `/API/Photos/Browse` | `/API/Photos/BrowseGroups` year/month/album/folder | top-level folders |

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
5. Smoke: every view on desktop + phone, a card open keeps `?view=`, Back closes.

## Verification

- `npm run typecheck`, `npm test`, `npm run build` (the same gate the Docker UI image runs).
- Headless smoke (Playwright from the test-roms skill folder): each section's seven views on the
  desktop and phone profiles, zero page/console errors, a card open from the Wall keeps the view.
  Password-gated sections (Arcade, Photos) need a password session and are checked by hand.
