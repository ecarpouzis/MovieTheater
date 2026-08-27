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
import type { CardItem, CardKind, ExploreRail, ExploreResponse } from "../types";
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
