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
  | "photo";

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
export interface TweakExtra {
  key: string;
  label: string;
  options: { value: string; label: string }[];
  /**
   * Remembered PER VIEW (stored under `${key}:${view}`, falling back to `key`): a backdrop chosen on
   * the Shelves need not follow you to the Grid — the standalone's per-layout background memory.
   */
  perView?: boolean;
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
  defaultSort?: string;
  /** Cards per flat band (default 48). */
  pageSize?: number;
  /** The uniform tile aspect the Grid uses (default `DEFAULT_ASPECT`). */
  defaultAspect?: number;
  /** "title", "album", "game" — the pager's tooltip noun. */
  itemNoun?: string;
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
