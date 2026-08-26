import { type CSSProperties } from "react";
import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import ScoreBadge from "./ScoreBadge";

/**
 * A horizontal strip of Explore cards — fixed cover height so a whole row shares one baseline, the
 * body clamped to exactly the cover's width. The rating badge sits top-right on the cover; the first
 * neutral badge ("12 issues", "collects 24") is the bottom-left corner pill.
 */
export const ROW_COVER_H = 208;

export function ExploreCard({ item, coverH = ROW_COVER_H, onOpen }: { item: CardItem; coverH?: number; onOpen: (item: CardItem) => void }) {
  const corner = item.badges?.find((b) => b.tone !== "rating");
  const sub = item.subtitle && item.label ? `${item.subtitle} · ${item.label}` : item.subtitle ?? item.label;
  return (
    <button
      type="button"
      className="xp-card"
      style={{ "--ch": `${coverH}px`, "--aspect": item.aspect || 0.66 } as CSSProperties}
      onClick={() => onOpen(item)}
      title={sub ? `${item.title} · ${sub}` : item.title}
      aria-label={item.title}
      data-kind={item.kind}
    >
      <div className="xp-card-cover">
        <CardImage src={item.imageUrl} hue={item.hue} />
        <ScoreBadge score={item.rating} className="xp-card-score" />
        {corner && <span className="xp-card-corner" title={corner.title}>{corner.label}</span>}
      </div>
      <div className="xp-card-body">
        <div className="xp-card-title">{item.title}</div>
        {sub && <div className="xp-card-sub">{sub}</div>}
      </div>
    </button>
  );
}

export default function CardRow({ items, coverH, onOpen }: { items: CardItem[]; coverH?: number; onOpen: (item: CardItem) => void }) {
  return (
    <div className="xp-row-scroll">
      {items.map((it) => <ExploreCard key={it.key} item={it} coverH={coverH} onOpen={onOpen} />)}
    </div>
  );
}
