/**
 * Explore art from the browser's OWN media token. The host answers Explore from a day-long cache, and a
 * media token is a 12-hour ticket — a URL the host baked in can be dead by the time the page is read
 * (the whole live Explore went 403 twelve hours after the host's boot-time warm). Every other Books
 * surface builds thumbs client-side from the live token (`thumbUrl`), so Explore does too: the host's
 * URL is only the fallback while no token is minted yet.
 */
import type { CardItem, ExploreResponse } from "../../catalog/types";
import { thumbUrl } from "./booksMedia";

/** The item whose cover a card shows: the item itself, or a series card's representative issue. */
export function coverItemIdOf(card: CardItem): number | null {
  if (card.kind === "series") {
    const raw = (card.raw ?? {}) as { cover?: { id?: number } | null };
    return raw.cover?.id ?? null;
  }
  return card.id;
}

export function withLiveArt(card: CardItem): CardItem {
  const id = coverItemIdOf(card);
  const live = id != null ? thumbUrl(id) : null;
  if (!live) return card;
  return { ...card, imageUrl: live, imageThumbUrl: live };
}

/** The whole payload re-pointed at the live token (a no-op until one exists). */
export function exploreWithLiveArt(data: ExploreResponse): ExploreResponse {
  return {
    ...data,
    spotlight: data.spotlight.map(withLiveArt),
    rails: data.rails.map((r) => ({ ...r, items: r.items.map(withLiveArt) })),
  };
}
