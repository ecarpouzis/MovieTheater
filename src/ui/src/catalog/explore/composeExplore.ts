/**
 * Composing an Explore payload IN THE BROWSER (R9 S7).
 *
 * Books' Explore comes down the wire already composed (`mapExplore` maps the host's DTO); every
 * other section composes its landing SPA-side out of endpoints it ALREADY serves — a rail is a
 * named query plus a mapper, and nothing new is asked of the API for it. These helpers are the
 * whole of that: build a rail, build a card that stands for a GROUP (a franchise, an artist, a
 * system, a person), and assemble the `ExploreResponse` the package's `ExploreTab` draws.
 *
 * Two rules the helpers enforce so a section cannot get them wrong:
 *  - an empty rail is DROPPED, never drawn as an empty shelf (`ExploreTab` filters again, but a
 *    dropped rail also costs nothing to map);
 *  - a group card carries `groupKey` + `count`, which is what `groupOf` reads when the tab hands it
 *    to `onOpenGroup` — the section then lands on its browse with the matching `f=token:value`.
 */
import { withPlaceholderArt } from "../cards/placeholder";
import type { CardItem, CardKind, ExploreColumn, ExploreDoorAxis, ExploreRail, ExploreResponse } from "../types";
import { cardKey } from "../types";
import { hueOf } from "../sources/hue";

/** A rail, or null when it has nothing to show. `more` is the section's OWN url (already mapped). */
export function exploreRail(
  key: string,
  title: string,
  kind: ExploreRail["kind"],
  items: readonly (CardItem | null | undefined)[] | null | undefined,
  more?: string | null,
): ExploreRail | null {
  const list = (items ?? []).filter((i): i is CardItem => !!i);
  if (list.length === 0) return null;
  return { key, title, kind, items: list, more: more ? { href: more } : undefined };
}

/** A `focus` rail: a name plate beside that name's works. Null when there are no works to show. */
export function exploreFocus(
  key: string,
  title: string,
  focus: NonNullable<ExploreRail["focus"]> | null | undefined,
  items: readonly (CardItem | null | undefined)[] | null | undefined,
  more?: string | null,
): ExploreRail | null {
  if (!focus?.name) return null;
  const rail = exploreRail(key, title, "focus", items, more ?? focus.href);
  return rail ? { ...rail, focus } : null;
}

/** A `columns` rail; a column with nothing in it is dropped, and so is the rail when none survive. */
export function exploreColumns(key: string, title: string, columns: readonly (ExploreColumn | null | undefined)[]): ExploreRail | null {
  const list = columns.filter((c): c is ExploreColumn => !!c && c.items.length > 0);
  if (list.length === 0) return null;
  return { key, title, kind: "columns", items: list.flatMap((c) => c.items), columns: list };
}

/** One list for a `columns` rail (null when empty, so a composer can pass a query's raw answer). */
export function exploreColumn(key: string, title: string, items: readonly (CardItem | null | undefined)[] | null | undefined, more?: string | null): ExploreColumn | null {
  const list = (items ?? []).filter((i): i is CardItem => !!i);
  return list.length ? { key, title, items: list, more: more ? { href: more } : undefined } : null;
}

/**
 * A `doors` rail. An axis whose doors have not loaded yet still draws its tab (`doors` undefined);
 * one that loaded EMPTY is dropped. The rail is dropped when no axis is left.
 */
export function exploreDoors(key: string, title: string, axes: readonly (ExploreDoorAxis | null | undefined)[], activeAxis?: string): ExploreRail | null {
  const list = axes.filter((a): a is ExploreDoorAxis => !!a && (a.doors === undefined || a.doors.length > 0));
  if (list.length === 0) return null;
  const active = list.some((a) => a.key === activeAxis) ? activeAxis : list[0].key;
  return { key, title, kind: "doors", items: [], axes: list, activeAxis: active };
}

/**
 * Give each door of an axis its OWN covers. The best-rated titles fit many doors at once (the same
 * three games fit "2 players", "3 players" and "4 players"), so taking each door's top three paints
 * the whole row with one hand of cards. Walk the doors in order and prefer covers no earlier door
 * has used; a door with nothing unused falls back to its own best.
 */
export function distinctCovers<T>(groups: readonly T[], candidates: (g: T) => readonly { src: string; hue?: number }[], per = 3): { src: string; hue?: number }[][] {
  const used = new Set<string>();
  return groups.map((g) => {
    const all = candidates(g).filter((c) => !!c.src);
    const fresh = all.filter((c) => !used.has(c.src));
    const pick = [...fresh, ...all.filter((c) => used.has(c.src))].slice(0, per);
    for (const c of pick) used.add(c.src);
    return pick;
  });
}

/** Does this rail have anything to draw? (`doors` and `columns` carry their content outside `items`.) */
export function railHasContent(rail: ExploreRail): boolean {
  if (rail.kind === "doors") return (rail.axes?.length ?? 0) > 0;
  if (rail.kind === "columns") return (rail.columns?.length ?? 0) > 0;
  return rail.items.length > 0;
}

/** Assemble the payload; nulls (a rail whose query has not landed) drop out. */
export function exploreResponse(
  spotlight: readonly (CardItem | null | undefined)[],
  rails: readonly (ExploreRail | null | undefined)[],
  seed?: number,
): ExploreResponse {
  return {
    spotlight: spotlight.filter((i): i is CardItem => !!i),
    rails: rails.filter((r): r is ExploreRail => !!r),
    seed,
  };
}

export interface GroupCardSpec {
  /** The GROUP kind — also the `groupBy` the tab passes to `onOpenGroup` (`franchise`, `artist`, `system`, `person`). */
  kind: CardKind;
  /** The facet VALUE this card stands for; it becomes `group.key`. */
  key: string;
  title: string;
  subtitle?: string;
  /** How many rows sit behind it (the corner pill and the group's `totalItems`). */
  count?: number;
  imageUrl?: string;
  imageThumbUrl?: string;
  aspect?: number;
  hue?: number;
  /** Stable numeric id when the group HAS one (an artist, a person); otherwise the key is hashed. */
  id?: number;
  raw?: unknown;
}

/** Deterministic small positive id for a group whose key is a string (cards are keyed `${kind}:${id}`). */
export function groupCardId(key: string): number {
  let h = 0;
  for (let i = 0; i < key.length; i += 1) h = (h * 31 + key.charCodeAt(i)) % 2147483647;
  return h || 1;
}

/** A card that stands for a whole facet value. `ExploreTab` routes it through `onOpenGroup`. */
export function groupCard(spec: GroupCardSpec): CardItem {
  const id = spec.id ?? groupCardId(spec.key);
  const count = spec.count ?? 0;
  return withPlaceholderArt({
    kind: spec.kind,
    id,
    key: cardKey(spec.kind, id),
    title: spec.title,
    subtitle: spec.subtitle,
    label: count > 0 ? `${count} title${count === 1 ? "" : "s"}` : undefined,
    aspect: spec.aspect ?? 0.667,
    imageUrl: spec.imageUrl ?? "",
    imageThumbUrl: spec.imageThumbUrl,
    hue: spec.hue ?? hueOf(spec.title),
    groupKey: spec.key,
    count: count || undefined,
    badges: count > 0 ? [{ label: String(count), tone: "neutral" as const, title: `${count} in this group` }] : undefined,
    raw: spec.raw ?? { count },
  });
}

/** "mcu" → "Mcu"; "studio-ghibli" → "Studio ghibli". The server's Humanize, for a bare tag value. */
export function humanizeKey(value: string): string {
  const s = String(value ?? "").replace(/[-_]+/g, " ").trim();
  return s ? s[0].toUpperCase() + s.slice(1) : s;
}

/** Deterministic shuffle: the same seed is the same order, so Back walks the rolls. */
export function seededShuffle<T>(rows: readonly T[], seed: number): T[] {
  const list = rows.slice();
  let s = (seed || 1) >>> 0;
  for (let i = list.length - 1; i > 0; i -= 1) {
    s = (s * 1664525 + 1013904223) >>> 0;
    const j = s % (i + 1);
    [list[i], list[j]] = [list[j], list[i]];
  }
  return list;
}

/**
 * One row of `rows`, chosen by the seed. `salt` lets two modules on one page roll independently off
 * the same seed (the decade column and the mood column must not move in lockstep).
 */
export function seededItem<T>(rows: readonly T[], seed: number, salt = 0): T | undefined {
  if (rows.length === 0) return undefined;
  const mixed = Math.imul(((seed || 1) ^ Math.imul(salt + 1, 0x9e3779b1)) >>> 0, 2654435761) >>> 0;
  return rows[mixed % rows.length];
}
