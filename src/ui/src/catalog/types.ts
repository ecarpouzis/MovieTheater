/**
 * The catalog package's shared contracts — the seam every section's browse surface will speak
 * once the generic views (Wall / Shelves / Grid / List / Extended) land. Nothing here fetches or
 * renders; adapters (`catalog/sources/*`) map a section's API rows onto these shapes, and the
 * views consume only these shapes.
 *
 * Kept deliberately small: a card is "an image with an identity and a few labels", a group is
 * "a labelled run of cards", and a source is "how to page cards and groups". Section-specific
 * detail (a movie's runtime, a comic's issue number) rides in `raw` for the section's own modal.
 */

/** Which entity space an item belongs to. Ids collide across spaces (movies vs misc, comics vs books). */
export type CardKind =
  | "movie"
  | "series"
  | "misc"
  | "comic"
  | "book"
  | "album"
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
  /** Optional per-group detail a view may surface (synopsis, span label, …). */
  detail?: Record<string, unknown>;
}

export type ViewMode = "grid" | "wall" | "shelf" | "list" | "extended" | "newspaper" | "directory";

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

/**
 * What a section hands the views. Flat paging is mandatory; grouped paging, "more of one group"
 * and letter buckets are optional capabilities a view checks before offering the control.
 */
export interface CatalogSource {
  /** Which views this section supports; the switcher shows only these. */
  supports: ViewMode[];
  /** Group-by modes for the grouped views ("series", "genre", "decade", …); empty = ungrouped only. */
  groupModes: string[];
  fetchFlatBand(skip: number, top: number, signal?: AbortSignal): Promise<CardPage>;
  fetchGroupBand?(groupsSkip: number, groupsTop: number, perGroupTop: number, groupBy: string, signal?: AbortSignal): Promise<GroupPage>;
  fetchGroupMore?(groupKey: string, skip: number, top: number, groupBy: string, signal?: AbortSignal): Promise<CardPage>;
  letters?(): Promise<LetterBucket[]>;
  /** Open the section's detail for a card (URL-driven modal, per the site convention). */
  onOpen(item: CardItem): void;
  onOpenGroup?(group: CardGroup): void;
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
