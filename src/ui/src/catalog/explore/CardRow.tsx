import { type CSSProperties } from "react";
import CardImage from "../cards/CardImage";
import type { CardItem } from "../types";
import ScoreBadge from "./ScoreBadge";
import ScrollRow from "./ScrollRow";

/**
 * A horizontal strip of Explore cards — fixed cover height so a whole row shares one baseline, the
 * body clamped to exactly the cover's width. The rating badge sits top-right on the cover; the first
 * neutral badge ("12 issues", "collects 24") is the bottom-left corner pill. A card with `progress`
 * draws a bar along the cover's foot (the resume rows).
 *
 * `fluid` drops the fixed height: the card fills its grid cell and the cover keeps its aspect — the
 * clamped grids use it so a row always spans the page edge to edge.
 */
export const ROW_COVER_H = 208;

export function ExploreCard({ item, coverH = ROW_COVER_H, onOpen, fluid }: { item: CardItem; coverH?: number; onOpen: (item: CardItem) => void; fluid?: boolean }) {
  const corner = item.badges?.find((b) => b.tone !== "rating");
  const sub = item.subtitle && item.label ? `${item.subtitle}, ${item.label}` : item.subtitle ?? item.label;
  const pct = item.progress == null ? null : Math.max(0, Math.min(100, item.progress));
  return (
    <button
      type="button"
      className={`xp-card${fluid ? " xp-card-fluid" : ""}`}
      style={{ "--ch": `${coverH}px`, "--aspect": item.aspect || 0.66 } as CSSProperties}
      onClick={() => onOpen(item)}
      title={sub ? `${item.title} (${sub})` : item.title}
      aria-label={item.title}
      data-kind={item.kind}
    >
      <div className="xp-card-cover">
        <CardImage src={fluid ? (item.imageThumbUrl ?? item.imageUrl) : item.imageUrl} hue={item.hue} />
        <ScoreBadge score={item.rating} className="xp-card-score" />
        {corner && pct == null && <span className="xp-card-corner" title={corner.title}>{corner.label}</span>}
        {pct != null && (
          <span className="xp-card-progress" role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label="Watched">
            <span style={{ width: `${pct}%` }} />
          </span>
        )}
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
    <ScrollRow className="xp-row-scroll">
      {items.map((it) => <ExploreCard key={it.key} item={it} coverH={coverH} onOpen={onOpen} />)}
    </ScrollRow>
  );
}
