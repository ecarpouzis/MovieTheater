/**
 * The wire shape every section's Explore endpoint answers in (the C# `CardItem`/`ExploreResponse`
 * records — nullable where the TypeScript card is optional), mapped onto the package's `CardItem`s.
 * Art that did not come is replaced by a hue tile; a card whose `kind` is a GROUP (a series, a
 * franchise, an artist) is kept as a card — the tab routes it to `onOpenGroup` — and `groupOf` builds
 * the `CardGroup` that call receives from the card's own facts.
 */
import { withPlaceholderArt } from "../cards/placeholder";
import type { CardBadgeSpec, CardGroup, CardItem, CardKind, ExploreRail, ExploreResponse } from "../types";

export interface ExploreWireBadge { label: string; tone?: string | null; title?: string | null }
export interface ExploreWireCard {
  kind: string;
  id: number;
  key?: string | null;
  title: string;
  subtitle?: string | null;
  label?: string | null;
  year?: number | null;
  aspect?: number | null;
  imageUrl?: string | null;
  imageThumbUrl?: string | null;
  hue?: number | null;
  rating?: number | null;
  badges?: ExploreWireBadge[] | null;
  groupKey?: string | null;
  sortKey?: string | null;
  raw?: unknown;
}
export interface ExploreWireRail { key: string; title: string; kind: string; items: ExploreWireCard[]; more?: { href: string } | null }
export interface ExploreWireResponse { spotlight: ExploreWireCard[]; rails: ExploreWireRail[]; seed?: number | null }

const TONES = new Set(["neutral", "rating", "system", "want", "live"]);

function toBadge(b: ExploreWireBadge): CardBadgeSpec {
  const tone = b.tone && TONES.has(b.tone) ? (b.tone as CardBadgeSpec["tone"]) : "neutral";
  return { label: b.label, tone, title: b.title ?? undefined };
}

export function toExploreCard(c: ExploreWireCard): CardItem {
  const kind = c.kind as CardKind;
  return withPlaceholderArt({
    kind,
    id: c.id,
    key: c.key || `${c.kind}:${c.id}`,
    title: c.title,
    subtitle: c.subtitle ?? undefined,
    label: c.label ?? undefined,
    year: c.year ?? undefined,
    aspect: c.aspect && c.aspect > 0 ? c.aspect : 0.66,
    imageUrl: c.imageUrl ?? "",
    imageThumbUrl: c.imageThumbUrl ?? undefined,
    hue: c.hue ?? undefined,
    rating: c.rating ?? undefined,
    badges: c.badges?.length ? c.badges.map(toBadge) : undefined,
    groupKey: c.groupKey ?? undefined,
    sortKey: c.sortKey ?? undefined,
    raw: c.raw,
  });
}

const RAIL_KINDS = new Set(["strip", "wall", "grid"]);

export function mapExplore(dto: ExploreWireResponse): ExploreResponse {
  return {
    spotlight: (dto.spotlight ?? []).map(toExploreCard),
    rails: (dto.rails ?? []).map((r): ExploreRail => ({
      key: r.key,
      title: r.title,
      kind: RAIL_KINDS.has(r.kind) ? (r.kind as ExploreRail["kind"]) : "strip",
      items: (r.items ?? []).map(toExploreCard),
      more: r.more?.href ? { href: r.more.href } : undefined,
    })),
    seed: dto.seed ?? undefined,
  };
}

/**
 * The card kinds that stand for a group rather than one item; the tab opens them through
 * `onOpenGroup`. This is the BOOKS/host default — a section whose vocabulary disagrees passes its
 * own set (`ExploreTab`'s `groupKinds`). Movies must: a `series` card there is a TV show, an ITEM,
 * and opening it as a group would land on a browse instead of the title's sheet.
 */
export const GROUP_CARD_KINDS: ReadonlySet<string> = new Set(["series", "artist"]);

/** The group kinds the SPA-composed sections use (R9 S7) — all of them stand for a facet value. */
export const FACET_GROUP_KINDS: ReadonlySet<string> = new Set(["franchise", "system", "person", "artist", "channel"]);

export function isGroupCard(card: CardItem, kinds: ReadonlySet<string> = GROUP_CARD_KINDS): boolean {
  return kinds.has(card.kind);
}

/** A group card as the `CardGroup` a section's `onOpenGroup` expects: the card is its own one-card run. */
export function groupOf(card: CardItem): CardGroup {
  const raw = (card.raw ?? {}) as { issueCount?: number; count?: number };
  const total = raw.issueCount ?? raw.count ?? card.count ?? 1;
  return { key: card.groupKey ?? String(card.id), label: card.title, totalItems: total, renderTotal: 1, items: [card] };
}
