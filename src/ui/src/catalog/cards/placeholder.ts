import { hueSvg } from "./CardImage";
import { hueOf } from "../sources/hue";
import type { CardItem } from "../types";

/**
 * A card that arrived without art (an unconfigured media plane, a title with no thumbnail) gets a
 * hue tile in place of a broken image: the hue is derived from the title when the source gave none,
 * so the same title always gets the same tint. Pure — returns the same object when nothing is missing.
 */
export function withPlaceholderArt<T extends Pick<CardItem, "title" | "imageUrl" | "imageThumbUrl" | "hue">>(card: T): T {
  const hue = card.hue ?? hueOf(card.title || "");
  if (card.imageUrl && card.hue != null) return card;
  const imageUrl = card.imageUrl || hueSvg(hue, 100, 150);
  return { ...card, hue, imageUrl, imageThumbUrl: card.imageThumbUrl || undefined };
}
