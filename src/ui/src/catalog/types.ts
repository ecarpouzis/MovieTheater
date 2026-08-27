/**
 * The catalog package's shared contracts — the seam every section's browse surface speaks.
 * Nothing here fetches or renders; adapters (`catalog/sources/*`) map a section's API rows onto
 * these shapes, and the views consume only these shapes.
 *
 * Kept deliberately small: a card is "an image with an identity and a few labels", a group is
 * "a labelled run of cards", and a source is "how to page cards and groups, and what the
 * switcher may offer". Section-specific detail (a movie's runtime, a comic's issue number) rides
 * in `raw` for the section's own modal.
 */

import type { ReactNode } from "react";
import type { HoverEffect, MetadataMode } from "./tweaks/useTweaks";

/** Which entity space an item belongs to. Ids collide across spaces (movies vs misc, comics vs books). */
export type CardKind =
  | "movie"
  | "series"
  | "misc"
  | "comic"
  | "book"
  | "album"
  | "artist"
  | "game"
  | "boardgame"
  | "photo"
  // Group spaces (R9 S7): a card that stands for a whole FACET rather than one row — a franchise,
  // an arcade system, a credited person, a TV channel. Explore routes these through `onOpenGroup`,
  // which lands on the section's browse with the matching `f=token:value`.
  | "franchise"
  | "system"
  | "person"
  | "channel";

export interface CardBadgeSpec {
  /** Short text, e.g. "IMDb 7.8", "#12", "4K". */
  label: string;
  /** Visual tone; maps onto the site's chip tokens (`--chip-*`, `--rating-*`). */
  tone?: "neutral" | "rating" | "system" | "want" | "live";
  /** Hover text. */
  title?: string;
}

export interface CardItem {
  kind: CardKind;
  id: number;
  /** Stable composite key, `${kind}:${id}` — ids collide across kinds. */
  key: string;
  title: string;
  subtitle?: string;
  /** Secondary line under the image (e.g. "1994", "Vol 3 #12", "12 tracks"). */
  label?: string;
  year?: number;
  /** Image width / height. Posters ~0.667; comics ~0.66; square art 1. */
  aspect: number;
  imageUrl: string;
  /** A smaller rendition for dense views (Wall / Shelves); falls back to `imageUrl`. */
  imageThumbUrl?: string;
  /** A hue (0–360) for shelf spines / placeholder tints when the art has not loaded. */
  hue?: number;
  /** Normalised 0–100 score for sort/badge; sources decide what it means. */
  rating?: number;
  badges?: CardBadgeSpec[];
  /** The group this card was returned under, when the source paged groups. */
  groupKey?: string;
  /** The value the source ordered by; lets a view show a letter rail or a "you are here". */
  sortKey?: string;
  /**
   * When the card stands for a whole group (the "Items: groups" mode of the flat views — one
   * representative per series/franchise/artist), how many items it stands for. Drives the
   * corner count badge and the "N titles" run label.
   */
  count?: number;
  /** Set when this card is a group's REPRESENTATIVE (the flat views' "one per group" mode); opening it opens the group. */
  group?: CardGroup;
  /** The section's own row, untouched — for the section's modal, never for the views. */
  raw: unknown;
}

/** The default aspect a source uses when the artwork's dimensions are unknown. */
export const DEFAULT_ASPECT = 0.66;

export interface CardGroup {
  key: string;
  label: string;
  /** True size of the group (header display) — not the number of cards loaded. */
  totalItems: number;
  /**
   * How many cards this group will RENDER when fully loaded. Differs from `totalItems` when the
   * source collapses a group to representatives (one card per series). Views that reserve space
   * up front must size on this, never on `totalItems`.
   */
  renderTotal: number;
  items: CardItem[];
  /**
   * Optional per-group detail a view may surface. Known keys: `runLabel` (a span like "1987 – Present"),
   * `synopsis`, `byline`, `kicker` (the Newspaper's category line), `tags` (string[]).
   */
  detail?: Record<string, unknown>;
  /** The caller's own marks on this group, when the section tracks them (Books' series/collection marks). */
  userMark?: { isRead: boolean; wantToRead: boolean; isFavorite: boolean; rating: number | null; notes: string | null };
}

export type ViewMode = "grid" | "wall" | "shelf" | "list" | "extended" | "newspaper" | "directory";

export const VIEW_LABELS: Record<ViewMode, string> = {
  grid: "Grid",
  wall: "Wall",
  shelf: "Shelves",
  list: "List",
  extended: "Extended",
  newspaper: "Newspaper",
  directory: "Directory",
};

/** The views whose content is one continuous run of cards (no group headers). */
export const FLAT_VIEWS: ReadonlySet<ViewMode> = new Set<ViewMode>(["grid", "wall", "list"]);

/** What the flat views page: every item, or one representative card per group. */
export type ItemsMode = "items" | "groups";

export interface SortSpec {
  value: string;
  label: string;
  /** An alphabetical order — the pager can offer letters (the source's `letters()`) instead of pages. */
  alpha?: boolean;
}

export interface GroupSpec {
  value: string;
  label: string;
}

export interface CardPage {
  items: CardItem[];
  /**
   * Total for the whole result set when the source knows it; `-1` when it does not (movies
   * report the count only on the first page — a source must carry the first value forward).
   */
  total: number;
}

export interface GroupPage {
  groups: CardGroup[];
  totalGroups: number;
}

export interface LetterBucket {
  letter: string;
  count: number;
  offset: number;
}

/** One column of the List view. `value` reads the display value straight off the card. */
export interface ListColumn {
  key: string;
  label: string;
  /** CSS grid track (e.g. "1.6fr", "64px"); default "1fr". */
  width?: string;
  align?: "left" | "right";
  /** Mono (numbers, dates) vs the body face. */
  mono?: boolean;
  value: (item: CardItem) => string | number | null | undefined;
}

/** A node of a section's own hierarchy (a folder, a franchise, an artist, a system). */
export interface DirectoryNode {
  id: string;
  label: string;
  count?: number;
  imageUrl?: string;
  hue?: number;
  hasChildren?: boolean;
}

/** The Directory view's data: a tree of nodes whose leaves page cards. */
export interface DirectorySource {
  roots(signal?: AbortSignal): Promise<DirectoryNode[]>;
  children(id: string, signal?: AbortSignal): Promise<DirectoryNode[]>;
  items(id: string, skip: number, top: number, signal?: AbortSignal): Promise<CardPage>;
}

/** A section-registered tweak (a font family, a backdrop) the panel shows as a segmented row. */
export interface TweakExtraOption {
  value: string;
  label: string;
  /** A swatch row's paint (a colour or a `var(--…)` reference). */
  color?: string;
  /** Which light/dark family the option belongs to ("any" = both). */
  family?: "light" | "dark" | "any";
  /** True when the option does not apply under the current theme — the panel says so on the chip. */
  inactive?: boolean;
}

export interface TweakExtra {
  key: string;
  label: string;
  options: TweakExtraOption[];
  /**
   * Remembered PER VIEW (stored under `${key}:${view}`, falling back to `key`): a backdrop chosen on
   * the Shelves need not follow you to the Grid — the standalone's per-layout background memory.
   */
  perView?: boolean;
  /**
   * How the row draws: a segmented control (default) or the Long Box's 4-column swatch grid — the
   * only sane shape for nine colours, which no Seg can hold (`catalog/skin/`).
   */
  render?: "seg" | "swatch";
}

/**
 * What the Grid hands a section's own card renderer (`CatalogSource.renderCard`). It is the tweak
 * contract in prop form: the card sizes its cover from `cellH` (the Grid's `--cell`), wears
 * `hoverClass` beside `bx-card` and puts `bx-cover` on its cover box so hover / rounded / dim apply
 * through the package's CSS, and hides its metadata block when `metadata` is "minimal".
 *
 * Deliberately flat primitives (no nested objects): a section card is memoized, and one fresh object
 * per render would defeat that memo for every card in the band.
 */
export interface CardRenderProps {
  /** Cover box height in px — `GRID_BASE_CELL` (or the source's `gridCell`) × the cover-size tweak. */
  cellH: number;
  /** The raw cover-size multiplier, for a card that scales more than its cover. */
  coverScale: number;
  metadata: MetadataMode;
  hover: HoverEffect;
  /** The per-card class for the current hover effect ("" for dim/none) — ONE source of truth. */
  hoverClass: string;
  /** Above-the-fold cards load their art eagerly; everything else is lazy. */
  eager: boolean;
  /** The stream's open action (a representative card opens its group, anything else opens itself). */
  onOpen: (item: CardItem) => void;
}

/**
 * What a section hands the views. Flat paging is mandatory; grouped paging, "more of one group",
 * letter buckets and a directory are optional capabilities a view checks before offering the
 * control.
 */
export interface CatalogSource {
  /**
   * Identity of the section's current filter state. The engines drop every band and go back to
   * the top when it changes; it must NOT change for a view/sort/tweak switch alone.
   */
  queryKey: string;
  /** The section's display name ("Movies", "Music") — the Newspaper's masthead and the empty states. */
  title?: string;
  /** Plural noun for groups ("franchises", "artists") — headers and the Items pill. */
  groupNoun?: string;
  /** Which views this section supports; the switcher shows only these. */
  supports: ViewMode[];
  /**
   * The Shelves view's look. "bookcase" (default): the standalone's wooden carcass — crown, stiles, the dark
   * walnut recess, cream labels — is part of the view, whatever the backdrop. "plain": bare planks on the
   * section's own surface.
   */
  shelvesSkin?: "bookcase" | "plain";
  /** Group-by modes for the grouped views and the flat views' representative mode; empty = ungrouped only. */
  groups: GroupSpec[];
  sorts: SortSpec[];
  /**
   * Set when the SECTION owns the sort (its own persisted control and `?sort=` param — Movies' NavBar
   * "Sort by"): the switcher shows exactly this value and never restores a remembered one, so the
   * views always page under the order the section's endpoints are already returning. Picking a sort
   * still writes `?sort=`; the section's own URL dispatcher reacts and hands back a new source.
   */
  currentSort?: string;
  /** Offer the Items pill (every item vs one card per group). Needs `fetchGroupBand`. */
  itemsModes?: ItemsMode[];
  /** Labels for the Items pill, e.g. { items: "Titles", groups: "Franchises" }. */
  itemsLabels?: Partial<Record<ItemsMode, string>>;
  listColumns?: ListColumn[];
  directory?: DirectorySource;
  tweakExtras?: TweakExtra[];
  defaultView?: ViewMode;
  defaultGroup?: string;
  /** The Items mode a fresh visitor lands on (Music lands on "one per artist"); default "items". */
  defaultItems?: ItemsMode;
  defaultSort?: string;
  /** Cards per flat band (default 48). */
  pageSize?: number;
  /** The uniform tile aspect the Grid uses (default `DEFAULT_ASPECT`). */
  defaultAspect?: number;
  /** "title", "album", "game" — the pager's tooltip noun. */
  itemNoun?: string;
  /**
   * What an empty result says, in the section's own words. Without it every view says
   * "No <itemNoun>s match." — which is a lie when nothing is filtering: an arcade with no games
   * ingested yet, a shelf nobody has added to. `filtered` below picks the line.
   */
  emptyLabel?: { empty: string; filtered: string };
  /**
   * True when SOMETHING narrows this source — a facet, a search box, a scope the user chose. It
   * picks `emptyLabel.filtered` over `emptyLabel.empty`, and only that. The source computes it (it
   * is the only party that knows what its scope was built from); the views cannot, since the
   * catalog's own state carries the view/group/sort and never the section's filters.
   */
  filtered?: boolean;
  /**
   * The GRID's own card (R9 S3). When a section supplies one, the Grid — and only the Grid — lays
   * the section's existing card into the shared bands instead of the package `Card`; every other
   * view keeps the package card. The engine, the letter strip, the skeletons and the tweaks
   * plumbing are shared either way.
   *
   * MUST return a module-level component (`<MovieGridCard …/>`), never an inline closure component:
   * a component type created per render is a new type every render and React remounts the whole
   * band (the BandSlot memo law).
   */
  renderCard?(item: CardItem, view: CardRenderProps): ReactNode;
  /** Extra class on the Grid's wrap, so a section's own card layout (a column grid) can replace the package's wrap flow. */
  gridClass?: string;
  /** The Grid's base cover height in px before the cover-size tweak (default `GRID_BASE_CELL`). */
  gridCell?: number;
  /**
   * Bumped when the source's DATA changed under an UNCHANGED `queryKey` — a dense in-memory list
   * edited in place (Movies' Seen/Want removal-on-untoggle) or extended by a background chunk. The
   * stream re-reads its bands; the window, the measured heights and the scroll position stay put
   * (a `queryKey` change is the other thing, and it resets all three).
   */
  dataVersion?: number;
  fetchFlatBand(skip: number, top: number, sort: string, signal?: AbortSignal): Promise<CardPage>;
  fetchGroupBand?(groupsSkip: number, groupsTop: number, perGroupTop: number, groupBy: string, sort: string, signal?: AbortSignal): Promise<GroupPage>;
  fetchGroupMore?(groupKey: string, skip: number, top: number, groupBy: string, sort: string, signal?: AbortSignal): Promise<CardPage>;
  /** Letter buckets over the flat order (only meaningful for an `alpha` sort). */
  letters?(sort: string, signal?: AbortSignal): Promise<LetterBucket[]>;
  /** Letter → first group index over the grouped order (only meaningful for an `alpha` sort). */
  groupLetters?(groupBy: string, sort: string, signal?: AbortSignal): Promise<{ letter: string; firstIndex: number }[]>;
  /** Open the section's detail for a card (URL-driven modal, per the site convention). */
  onOpen(item: CardItem): void;
  /** Open a group's own browse; `groupBy` says which mode the key belongs to (a genre key is not a franchise key). */
  onOpenGroup?(group: CardGroup, groupBy: string): void;
}

/** One rail on a section's Explore tab. */
export interface ExploreRail {
  key: string;
  title: string;
  kind: "strip" | "wall" | "grid";
  items: CardItem[];
  /** Where "more" leads (a browse URL with the rail's filter applied). */
  more?: { href: string };
}

/** The envelope every section's Explore endpoint returns, already mapped onto cards by its source. */
export interface ExploreResponse {
  spotlight: CardItem[];
  rails: ExploreRail[];
  /** Seed the server used for the shuffled rails, so a "re-roll" can ask for a different one. */
  seed?: number;
}

export function cardKey(kind: CardKind, id: number): string {
  return `${kind}:${id}`;
}
